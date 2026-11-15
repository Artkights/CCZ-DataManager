using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataManager.Core.Domain;
using DataManager.Core.Editing;

namespace DataManager.Core.Scanner;

/// <summary>
/// Owns the .strh PE section and the two native hooks used by the strategy
/// modifier rules. All work happens on an in-memory image; the caller stages
/// the resulting candidate in the workspace save transaction.
/// </summary>
public sealed class StrategyHookSectionService
{
    public const string SectionName = ".strh";
    public const uint DamageHookVa = 0x0043C205;
    public const uint DamageReturnVa = 0x0043C20D;
    public const uint HitHookVa = 0x0043B9A2;
    public const uint HitReturnVa = 0x0043B9AA;

    private static readonly byte[] DamageOriginal = [0x8B, 0x4D, 0xF0, 0xE8, 0x82, 0xA3, 0xFC, 0xFF];
    private static readonly byte[] HitOriginal = [0x6A, 0x00, 0x6A, 0x01, 0x6A, 0x3C, 0x6A, 0x1C];

    private const uint TableOffset = 0x1000;
    private const uint NotesOffset = 0x1300;
    private const uint StateOffset = 0x1D00;
    private const int NoteBytes = 40;
    private const uint HitCodeOffset = 0x0800;
    private const uint SectionCharacteristics = 0xE0000020;
    private const uint HelperStrategyRecord = 0x00484002;
    private const uint HelperStrategyType = 0x0044270C;
    private const uint HelperJobSeries = 0x0040BF0C;
    private const uint HelperCasterSeries = 0x0043B4A7;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public sealed record State(
        IReadOnlyList<StrategyJobModifierRule> Rules,
        bool HookInstalled,
        uint SectionVa,
        uint SectionRaw,
        uint SectionRawSize,
        string Diagnostic);

    private sealed record PeLayout(
        int PeOffset,
        int OptionalOffset,
        int SectionTableOffset,
        ushort SectionCount,
        uint ImageBase,
        uint SectionAlignment,
        uint FileAlignment,
        int LastSectionHeader,
        uint LastVirtualAddress,
        uint LastVirtualSize,
        uint LastRawPointer,
        uint LastRawSize,
        uint SizeOfImage,
        uint SizeOfCode,
        uint SizeOfInitializedData,
        ushort DllCharacteristics,
        uint FirstRawPointer);

    private sealed record StoredState(
        int Version,
        bool Installed,
        string OriginalSectionHeader,
        uint OriginalSizeOfImage,
        uint OriginalSizeOfCode,
        uint OriginalSizeOfInitializedData,
        ushort OriginalSectionCount,
        uint SectionVa,
        uint SectionRaw,
        uint SectionRawSize,
        string SourceHash,
        string HitOriginal);

    private readonly record struct SectionInfo(string Name, uint VirtualAddress, uint RawOffset, uint RawSize, uint VirtualSize);

    public static int VaToFileOffset(byte[] image, uint va)
        => VaToOffset(image, ReadLayout(image), va);

    public State? Read(byte[] image)
    {
        var layout = ReadLayout(image);
        var section = FindSection(image, layout, SectionName);
        if (section == null) return null;
        var header = ReadRuleHeader(image, section.Value.RawOffset);
        if (header == null) return null;
        var rules = ReadRuntimeRules(image, section.Value.RawOffset);
        var notes = ReadNotes(image, section.Value.RawOffset);
        var combined = rules.Select((rule, index) => rule with
        {
            Note = index < notes.Count ? notes[index] : string.Empty
        }).ToArray();
        var installed = IsOurHook(image, layout, DamageHookVa, section.Value.VirtualAddress)
            && IsOurHook(image, layout, HitHookVa, section.Value.VirtualAddress);
        var stored = ReadStoredState(image, section.Value.RawOffset);
        return new State(combined, installed, section.Value.VirtualAddress, section.Value.RawOffset,
            section.Value.RawSize, stored == null ? "规则状态 JSON 缺失，已按运行时表读取。" : "已读取 .strh 规则表。");
    }

    public WorkspaceBinaryCandidate Build(string filePath, byte[] before,
        IReadOnlyList<StrategyJobModifierRule> rules, bool installHook)
    {
        if (rules.Count > StrategyJobModifierRule.Capacity)
            throw new InvalidDataException($"规则超过 {StrategyJobModifierRule.Capacity} 条上限。");
        var layout = ReadLayout(before);
        if ((layout.DllCharacteristics & 0x0040) != 0)
            throw new InvalidDataException("目标 EXE 启用了 ASLR（DYNAMIC_BASE），不能安装绝对地址 Hook。");
        var existing = FindSection(before, layout, SectionName);
        if (existing == null && !installHook && rules.Count == 0)
            return new WorkspaceBinaryCandidate(filePath, before, before, "策略专项");

        var effectiveInstall = installHook && rules.Count > 0;
        var hitOriginal = ResolveHitOriginal(before, layout, existing);
        var after = existing == null
            ? AppendSection(before, layout, rules, effectiveInstall, hitOriginal)
            : UpdateExistingSection(before, layout, existing.Value, rules, effectiveInstall, hitOriginal);
        var finalLayout = ReadLayout(after);
        var finalSection = FindSection(after, finalLayout, SectionName)
            ?? throw new InvalidDataException("写入后未找到 .strh 节。");
        WriteHooks(after, finalLayout, finalSection.VirtualAddress, effectiveInstall, hitOriginal);
        var verify = Read(after);
        if (verify == null) throw new InvalidDataException(".strh 节复读失败。");
        if (effectiveInstall && (!IsOurHook(after, finalLayout, DamageHookVa, finalSection.VirtualAddress)
            || !IsOurHook(after, finalLayout, HitHookVa, finalSection.VirtualAddress)))
            throw new InvalidDataException("Hook 站点复读失败。");
        return new WorkspaceBinaryCandidate(filePath, before, after, "策略专项");
    }

    public byte[] RestoreHooks(byte[] image)
    {
        var layout = ReadLayout(image);
        var section = FindSection(image, layout, SectionName);
        var hitOriginal = ResolveHitOriginal(image, layout, section);
        if (section == null)
        {
            var clone = (byte[])image.Clone();
            var damage = VaToOffset(image, layout, DamageHookVa);
            var hit = VaToOffset(image, layout, HitHookVa);
            if (!image.AsSpan(damage, 8).SequenceEqual(DamageOriginal)
                || !image.AsSpan(hit, 8).SequenceEqual(hitOriginal))
                throw new InvalidDataException("未找到 .strh 节，但 Hook 站点不是原始字节。");
            return clone;
        }
        var after = (byte[])image.Clone();
        WriteHooks(after, layout, section.Value.VirtualAddress, false, hitOriginal);
        SetInstalledFlag(after, section.Value.RawOffset, false);
        return after;
    }

    private static byte[] AppendSection(byte[] before, PeLayout layout,
        IReadOnlyList<StrategyJobModifierRule> rules, bool installHook, byte[] hitOriginal)
    {
        var headerEnd = layout.SectionTableOffset + (layout.SectionCount + 1) * 40;
        if (headerEnd > layout.FirstRawPointer)
            throw new InvalidDataException("PE 节表空间不足，无法追加 .strh 节。");
        var sectionVa = Align(layout.LastVirtualAddress + Math.Max(layout.LastVirtualSize, layout.LastRawSize), layout.SectionAlignment);
        var sectionRva = sectionVa - layout.ImageBase;
        var raw = Align((uint)before.Length, layout.FileAlignment);
        var rawSize = Align(StrategyJobModifierRule.SectionSize, layout.FileAlignment);
        var after = new byte[checked((int)(raw + rawSize))];
        before.CopyTo(after, 0);
        var header = layout.SectionTableOffset + layout.SectionCount * 40;
        Array.Clear(after, header, 40);
        Encoding.ASCII.GetBytes(SectionName).CopyTo(after, header);
        Put32(after, header + 8, StrategyJobModifierRule.SectionSize);
        Put32(after, header + 12, sectionRva);
        Put32(after, header + 16, rawSize);
        Put32(after, header + 20, raw);
        Put32(after, header + 36, SectionCharacteristics);
        Put16(after, layout.PeOffset + 6, (ushort)(layout.SectionCount + 1));
        Put32(after, layout.OptionalOffset + 56, Align(sectionRva + StrategyJobModifierRule.SectionSize, layout.SectionAlignment));
        Put32(after, layout.OptionalOffset + 4, layout.SizeOfCode + rawSize);
        Put32(after, layout.OptionalOffset + 8, layout.SizeOfInitializedData + rawSize);
        Put32(after, layout.OptionalOffset + 64, 0);
        var block = BuildSectionBlock(sectionVa, raw, rules, installHook, layout, before, hitOriginal);
        block.CopyTo(after, checked((int)raw));
        return after;
    }

    private static byte[] UpdateExistingSection(byte[] before, PeLayout layout, SectionInfo section,
        IReadOnlyList<StrategyJobModifierRule> rules, bool installHook, byte[] hitOriginal)
    {
        if (section.RawSize != Align(StrategyJobModifierRule.SectionSize, layout.FileAlignment)
            || checked((int)(section.RawOffset + section.RawSize)) > before.Length)
            throw new InvalidDataException(".strh 节尺寸与当前版本不一致，请先卸载后重建 Hook。");
        var after = (byte[])before.Clone();
        var stored = ReadStoredState(before, section.RawOffset);
        var block = BuildSectionBlock(section.VirtualAddress, section.RawOffset, rules, installHook, layout, before,
            hitOriginal, stored);
        block.CopyTo(after, checked((int)section.RawOffset));
        return after;
    }

    private static byte[] BuildSectionBlock(uint sectionVa, uint sectionRaw,
        IReadOnlyList<StrategyJobModifierRule> rules, bool installHook, PeLayout layout,
        byte[] sourceImage, byte[] hitOriginal, StoredState? existing = null)
    {
        var block = new byte[StrategyJobModifierRule.SectionSize];
        var tableVa = sectionVa + TableOffset;
        BuildEvaluator(sectionVa, tableVa, isHit: false, DamageOriginal).CopyTo(block, 0);
        BuildEvaluator(sectionVa + HitCodeOffset, tableVa, isHit: true, hitOriginal).CopyTo(block, checked((int)HitCodeOffset));
        WriteRuleTable(block, TableOffset, rules, installHook);
        WriteNotes(block, NotesOffset, rules);
        var stored = new StoredState(
            1,
            installHook,
            existing?.OriginalSectionHeader ?? string.Empty,
            existing?.OriginalSizeOfImage ?? layout.SizeOfImage,
            existing?.OriginalSizeOfCode ?? layout.SizeOfCode,
            existing?.OriginalSizeOfInitializedData ?? layout.SizeOfInitializedData,
            existing?.OriginalSectionCount ?? layout.SectionCount,
            sectionVa,
            sectionRaw,
            StrategyJobModifierRule.SectionSize,
            Convert.ToHexString(SHA256.HashData(sourceImage)),
            Convert.ToBase64String(hitOriginal));
        WriteStoredState(block, StateOffset, stored);
        return block;
    }

    private static byte[] BuildEvaluator(uint codeVa, uint tableVa, bool isHit, byte[] displaced)
    {
        var x = new X86(codeVa);
        const string done = "done";
        const string loop = "loop";
        const string next = "next";
        const string caster = "caster";
        const string seriesDone = "seriesDone";
        const string byId = "byId";
        const string apply = "apply";
        const string clampDone = "clampDone";

        x.Emit(0x9C, 0x60);                         // pushfd; pushad
        x.Emit(0x8B, 0x6C, 0x24, 0x08);             // mov ebp,[esp+8]
        x.MovEdiImm32(tableVa);
        x.Emit(0x0F, 0xB7, 0x5F, 0x06);             // movzx ebx, word [edi+6]
        x.Emit(0x85, 0xDB);                         // test ebx,ebx
        x.JccLabel(0x84, done);                     // jz done
        x.MovEsiImm32(tableVa + StrategyJobModifierRule.HeaderSize);
        x.Mark(loop);
        x.Emit(0x0F, 0xB6, 0x46, 0x06);             // movzx eax, byte [esi+6]
        x.Emit(0xA8, 0x01);                         // test al,1
        x.JccLabel(0x84, next);                     // jz next
        x.Emit(0x8A, 0x06);                         // mov al,[esi]
        x.Emit(0x84, 0xC0);                         // test al,al
        x.JccLabel(0x85, caster);                   // jnz caster
        x.Emit(0x8B, 0x4D, 0x0C);                   // mov ecx,[ebp+0x0C] target
        x.CallAbs(HelperJobSeries);
        x.JumpLabel(seriesDone);
        x.Mark(caster);
        x.Emit(0x8B, 0x4D, isHit ? (byte)0xE8 : (byte)0xF0); // caster [ebp-0x18] / [ebp-0x10]
        x.CallAbs(HelperCasterSeries);
        x.Mark(seriesDone);
        x.Emit(0x3A, 0x46, 0x03);                   // cmp al,[esi+3]
        x.JccLabel(0x85, next);                     // jne next
        x.Emit(0x8A, 0x46, 0x01);                   // mov al,[esi+1] matchKind
        x.Emit(0x84, 0xC0);                         // test al,al
        x.JccLabel(0x85, byId);                     // jnz byId
        x.Emit(0x8B, 0x4D, 0x10);                   // mov ecx,[ebp+0x10] strategy id
        x.CallAbs(HelperStrategyRecord);
        x.CallAbs(HelperStrategyType);
        x.Emit(0x3A, 0x46, 0x02);                   // cmp al,[esi+2]
        x.JccLabel(0x85, next);                     // jne next
        x.JumpLabel(apply);
        x.Mark(byId);
        x.Emit(0x8A, 0x45, 0x10);                   // mov al,[ebp+0x10]
        x.Emit(0x3A, 0x46, 0x02);                   // cmp al,[esi+2]
        x.JccLabel(0x85, next);                     // jne next
        x.Mark(apply);
        if (isHit)
        {
            x.Emit(0x8B, 0x45, 0xF0);               // mov eax,[ebp-0x10]
            x.Emit(0x0F, 0xBE, 0x56, 0x05);         // movsx edx, byte [esi+5]
            x.Emit(0x0F, 0xAF, 0xC2);               // imul eax,edx
            x.MovEcxImm32(100);
            x.Emit(0x99);                           // cdq
            x.Emit(0xF7, 0xF9);                     // idiv ecx
            x.Emit(0x01, 0x45, 0xF0);               // add [ebp-0x10],eax
            x.Emit(0x8B, 0x45, 0xF0);
            x.Emit(0x85, 0xC0);
            x.JccLabel(0x89, "hitNonNegative");     // jns
            x.Emit(0x31, 0xC0);
            x.Emit(0x89, 0x45, 0xF0);
            x.Mark("hitNonNegative");
            x.Emit(0x83, 0xF8, 0x64);               // cmp eax,100
            x.JccLabel(0x8E, clampDone);            // jle clampDone
            x.Emit(0xB8, 0x64, 0x00, 0x00, 0x00);   // mov eax,100
            x.Emit(0x89, 0x45, 0xF0);
        }
        else
        {
            x.Emit(0x8B, 0x45, 0xFC);               // mov eax,[ebp-4]
            x.Emit(0x0F, 0xBE, 0x56, 0x04);         // movsx edx, byte [esi+4]
            x.Emit(0x0F, 0xAF, 0xC2);               // imul eax,edx
            x.MovEcxImm32(100);
            x.Emit(0x99);
            x.Emit(0xF7, 0xF9);
            x.Emit(0x01, 0x45, 0xFC);               // add [ebp-4],eax
            x.Emit(0x8B, 0x45, 0xFC);
            x.Emit(0x85, 0xC0);
            x.JccLabel(0x89, clampDone);            // jns clampDone
            x.Emit(0x31, 0xC0);
            x.Emit(0x89, 0x45, 0xFC);
        }
        x.Mark(clampDone);
        x.Mark(next);
        x.Emit(0x83, 0xC6, 0x08);                   // add esi,8
        x.Emit(0x4B);                               // dec ebx
        x.JccLabel(0x85, loop);                     // jnz loop
        x.Mark(done);
        x.Emit(0x61, 0x9D);                         // popad; popfd
        if (isHit)
        {
            x.Emit(displaced);
            x.Jump(HitReturnVa);
        }
        else
        {
            x.Emit(displaced);
            x.Jump(DamageReturnVa);
        }
        return x.Finish();
    }

    private static void WriteRuleTable(byte[] block, uint offset,
        IReadOnlyList<StrategyJobModifierRule> rules, bool installed)
    {
        var at = checked((int)offset);
        Encoding.ASCII.GetBytes("STRH").CopyTo(block, at);
        Put16(block, at + 4, 1);
        Put16(block, at + 6, (ushort)Math.Min(rules.Count, StrategyJobModifierRule.Capacity));
        Put16(block, at + 8, StrategyJobModifierRule.Capacity);
        Put16(block, at + 0x0A, StrategyJobModifierRule.RuleSize);
        Put16(block, at + 0x0C, (ushort)(installed && rules.Count > 0 ? 1 : 0));
        Put16(block, at + 0x0E, 0);
        var ruleAt = at + StrategyJobModifierRule.HeaderSize;
        foreach (var rule in rules.Take(StrategyJobModifierRule.Capacity))
        {
            block[ruleAt + 0] = (byte)rule.Side;
            block[ruleAt + 1] = (byte)rule.MatchKind;
            block[ruleAt + 2] = rule.MatchValue;
            block[ruleAt + 3] = rule.Series;
            block[ruleAt + 4] = unchecked((byte)rule.DamagePercent);
            block[ruleAt + 5] = unchecked((byte)rule.HitPercent);
            block[ruleAt + 6] = (byte)(rule.Enabled ? 1 : 0);
            block[ruleAt + 7] = 0;
            ruleAt += StrategyJobModifierRule.RuleSize;
        }
    }

    private static void WriteNotes(byte[] block, uint offset, IReadOnlyList<StrategyJobModifierRule> rules)
    {
        var at = checked((int)offset);
        for (var index = 0; index < StrategyJobModifierRule.Capacity; index++)
        {
            var slot = at + index * NoteBytes;
            Array.Clear(block, slot, NoteBytes);
            if (index >= rules.Count) continue;
            var note = rules[index].Note ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(note);
            if (bytes.Length > NoteBytes)
                throw new InvalidDataException($"第 {index + 1} 条规则的备注超过 {NoteBytes} 字节（UTF-8）。");
            bytes.CopyTo(block, slot);
        }
    }

    private static List<string> ReadNotes(byte[] image, uint raw)
    {
        var result = new List<string>();
        var at = checked((int)(raw + NotesOffset));
        for (var index = 0; index < StrategyJobModifierRule.Capacity; index++)
        {
            var slot = at + index * NoteBytes;
            if (slot + NoteBytes > image.Length) break;
            var zero = Array.IndexOf(image, (byte)0, slot, NoteBytes);
            var length = zero < 0 ? NoteBytes : zero - slot;
            result.Add(length == 0 ? string.Empty : Encoding.UTF8.GetString(image, slot, length));
        }
        return result;
    }

    private static List<StrategyJobModifierRule> ReadRuntimeRules(byte[] image, uint raw)
    {
        var result = new List<StrategyJobModifierRule>();
        var at = checked((int)(raw + TableOffset));
        if (at + StrategyJobModifierRule.HeaderSize > image.Length) return result;
        if (Encoding.ASCII.GetString(image, at, 4) != "STRH") return result;
        var count = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at + 6)), StrategyJobModifierRule.Capacity);
        var ruleAt = at + StrategyJobModifierRule.HeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (ruleAt + StrategyJobModifierRule.RuleSize > image.Length) break;
            result.Add(new StrategyJobModifierRule(
                (StrategyModifierSide)image[ruleAt],
                (StrategyModifierMatchKind)image[ruleAt + 1],
                image[ruleAt + 2],
                image[ruleAt + 3],
                unchecked((sbyte)image[ruleAt + 4]),
                unchecked((sbyte)image[ruleAt + 5]),
                (image[ruleAt + 6] & 1) != 0,
                string.Empty));
            ruleAt += StrategyJobModifierRule.RuleSize;
        }
        return result;
    }

    private static (bool Installed, int Count)? ReadRuleHeader(byte[] image, uint raw)
    {
        var at = checked((int)(raw + TableOffset));
        if (at + StrategyJobModifierRule.HeaderSize > image.Length) return null;
        if (Encoding.ASCII.GetString(image, at, 4) != "STRH") return null;
        return ((BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at + 0x0C)) & 1) != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at + 6)));
    }

    private static void SetInstalledFlag(byte[] image, uint raw, bool installed)
    {
        var at = checked((int)(raw + TableOffset + 0x0C));
        if (at + 2 > image.Length) return;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at));
        Put16(image, at, (ushort)(installed ? flags | 1 : flags & ~1));
    }

    private static StoredState? ReadStoredState(byte[] image, uint raw)
    {
        try
        {
            var at = checked((int)(raw + StateOffset));
            var end = Math.Min(image.Length, at + StrategyJobModifierRule.StateJsonSize);
            var zero = Array.IndexOf(image, (byte)0, at, end - at);
            var length = zero < 0 ? end - at : zero - at;
            if (length <= 0) return null;
            return JsonSerializer.Deserialize<StoredState>(Encoding.UTF8.GetString(image, at, length), Json);
        }
        catch { return null; }
    }

    private static void WriteStoredState(byte[] block, uint offset, StoredState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        if (json.Length >= StrategyJobModifierRule.StateJsonSize)
            throw new InvalidDataException("规则状态 JSON 超出固定容量。");
        json.CopyTo(block, checked((int)offset));
        Array.Clear(block, checked((int)offset) + json.Length, StrategyJobModifierRule.StateJsonSize - json.Length);
    }

    private static void WriteHooks(byte[] image, PeLayout layout, uint sectionVa, bool install, byte[] hitOriginal)
    {
        EnsureHookSite(image, layout, DamageHookVa, DamageOriginal, sectionVa);
        EnsureHitHookSite(image, layout, sectionVa);
        if (install)
        {
            WriteJump(image, layout, DamageHookVa, sectionVa);
            WriteJump(image, layout, HitHookVa, sectionVa + HitCodeOffset);
        }
        else
        {
            var damage = VaToOffset(image, layout, DamageHookVa);
            var hit = VaToOffset(image, layout, HitHookVa);
            DamageOriginal.AsSpan(0, 8).CopyTo(image.AsSpan(damage));
            hitOriginal.AsSpan(0, 8).CopyTo(image.AsSpan(hit));
        }
    }

    private static void EnsureHookSite(byte[] image, PeLayout layout, uint hookVa, byte[] original, uint sectionVa)
    {
        var offset = VaToOffset(image, layout, hookVa);
        if (offset + original.Length > image.Length)
            throw new InvalidDataException($"Hook 站点 0x{hookVa:X8} 超出文件。");
        if (image.AsSpan(offset, original.Length).SequenceEqual(original)) return;
        if (IsOurHook(image, layout, hookVa, sectionVa)) return;
        throw new InvalidDataException($"Hook 站点 0x{hookVa:X8} 已被其他代码占用。");
    }

    private static void EnsureHitHookSite(byte[] image, PeLayout layout, uint sectionVa)
    {
        if (IsOurHook(image, layout, HitHookVa, sectionVa)) return;
        _ = CaptureHitOriginal(image, layout);
    }

    private static byte[] ResolveHitOriginal(byte[] image, PeLayout layout, SectionInfo? section)
    {
        if (section != null)
        {
            var stored = ReadStoredState(image, section.Value.RawOffset);
            if (stored?.HitOriginal is { Length: > 0 } encoded)
            {
                try
                {
                    var bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length == 8) return bytes;
                }
                catch (FormatException) { }
            }
            var offset = VaToOffset(image, layout, HitHookVa);
            if (image[offset] == 0xE9)
                throw new InvalidDataException(".strh 状态缺少命中 Hook 原始字节，无法安全卸载/更新。");
        }
        return CaptureHitOriginal(image, layout);
    }

    private static byte[] CaptureHitOriginal(byte[] image, PeLayout layout)
    {
        var offset = VaToOffset(image, layout, HitHookVa);
        if (offset + 8 > image.Length)
            throw new InvalidDataException("命中 Hook 站点超出文件。");
        var bytes = image.AsSpan(offset, 8).ToArray();
        if (bytes[0] == 0x6A && bytes[2] == 0x6A && bytes[4] == 0x6A && bytes[6] == 0x6A && bytes[7] == 0x1C)
            return bytes;
        throw new InvalidDataException("命中 Hook 站点原字节不是已知模式（6A ?? 6A ?? 6A ?? 6A 1C）。");
    }

    private static void WriteJump(byte[] image, PeLayout layout, uint hookVa, uint targetVa)
    {
        var offset = VaToOffset(image, layout, hookVa);
        image[offset] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset + 1), unchecked((int)(targetVa - (hookVa + 5))));
        for (var index = 5; index < 8; index++) image[offset + index] = 0x90;
    }

    private static bool IsOurHook(byte[] image, PeLayout layout, uint hookVa, uint sectionVa)
    {
        var offset = VaToOffset(image, layout, hookVa);
        if (offset < 0 || offset + 5 > image.Length || image[offset] != 0xE9) return false;
        var target = hookVa + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset + 1));
        return target >= sectionVa && target < sectionVa + StrategyJobModifierRule.SectionSize;
    }

    private static SectionInfo? FindSection(byte[] image, PeLayout layout, string name)
    {
        for (var i = 0; i < layout.SectionCount; i++)
        {
            var at = layout.SectionTableOffset + i * 40;
            var current = Encoding.ASCII.GetString(image, at, 8).TrimEnd('\0');
            if (!current.Equals(name, StringComparison.Ordinal)) continue;
            return new SectionInfo(current, layout.ImageBase + U32(image, at + 12),
                U32(image, at + 20), U32(image, at + 16), U32(image, at + 8));
        }
        return null;
    }

    private static PeLayout ReadLayout(byte[] image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
            throw new InvalidDataException("不是有效的 PE 文件。");
        var pe = checked((int)U32(image, 0x3C));
        if (pe <= 0 || pe + 0x18 > image.Length || U32(image, pe) != 0x00004550)
            throw new InvalidDataException("PE 头无效。");
        var count = U16(image, pe + 6);
        var optionalSize = U16(image, pe + 20);
        var optional = pe + 24;
        if (U16(image, optional) != 0x10B)
            throw new InvalidDataException("策略 Hook 只支持 PE32。");
        var sectionTable = optional + optionalSize;
        if (count == 0) throw new InvalidDataException("PE 节表为空。");
        var last = sectionTable + (count - 1) * 40;
        var lastRva = U32(image, last + 12);
        var lastRaw = U32(image, last + 20);
        var lastSize = U32(image, last + 16);
        for (var i = 0; i < count - 1; i++)
        {
            var at = sectionTable + i * 40;
            if (U32(image, at + 12) >= lastRva || U32(image, at + 20) + U32(image, at + 16) > lastRaw)
                throw new InvalidDataException("PE 末节不是最高文件/虚拟节，不能安全追加 .strh。");
        }
        if (U32(image, optional + 96 + 6 * 8) != 0 && U32(image, optional + 100 + 6 * 8) != 0)
            throw new InvalidDataException("该 PE 含调试目录，尚未验证追加节时的文件偏移重定位。");
        var firstRaw = Enumerable.Range(0, count)
            .Select(i => U32(image, sectionTable + i * 40 + 20))
            .Where(value => value != 0).DefaultIfEmpty(uint.MaxValue).Min();
        return new PeLayout(pe, optional, sectionTable, count,
            U32(image, optional + 28), U32(image, optional + 32), U32(image, optional + 36),
            last, U32(image, optional + 28) + lastRva, U32(image, last + 8), lastRaw, lastSize,
            U32(image, optional + 56), U32(image, optional + 4), U32(image, optional + 8),
            U16(image, optional + 70), firstRaw);
    }

    private static int VaToOffset(byte[] image, PeLayout layout, uint va)
    {
        var rva = va - layout.ImageBase;
        for (var i = 0; i < layout.SectionCount; i++)
        {
            var at = layout.SectionTableOffset + i * 40;
            var virtualSize = U32(image, at + 8);
            var virtualAddress = U32(image, at + 12);
            var rawSize = U32(image, at + 16);
            var rawPointer = U32(image, at + 20);
            if (rva < virtualAddress || rva >= virtualAddress + Math.Max(virtualSize, rawSize)) continue;
            var delta = rva - virtualAddress;
            if (delta >= rawSize) throw new InvalidDataException($"VA 0x{va:X8} 没有文件数据。");
            return checked((int)(rawPointer + delta));
        }
        throw new InvalidDataException($"VA 0x{va:X8} 不在任何节内。");
    }

    private static uint Align(uint value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private sealed class X86(uint address)
    {
        private readonly List<byte> _code = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int At, string Label)> _fixups = [];

        public void Emit(params byte[] bytes) => _code.AddRange(bytes);
        public void U32(uint value) => _code.AddRange(BitConverter.GetBytes(value));
        public void Mark(string label) => _labels[label] = _code.Count;
        public void CallAbs(uint target) { Emit(0xE8); U32(unchecked(target - (address + (uint)_code.Count + 4))); }
        public void Jump(uint target) { Emit(0xE9); U32(unchecked(target - (address + (uint)_code.Count + 4))); }
        public void JumpLabel(string label) { Emit(0xE9); _fixups.Add((_code.Count, label)); U32(0); }
        public void JccLabel(byte condition, string label)
        {
            Emit(0x0F, condition);
            _fixups.Add((_code.Count, label));
            U32(0);
        }
        public void MovEsiImm32(uint value) { Emit(0xBE); U32(value); }
        public void MovEdiImm32(uint value) { Emit(0xBF); U32(value); }
        public void MovEcxImm32(uint value) { Emit(0xB9); U32(value); }
        public byte[] Finish()
        {
            var result = _code.ToArray();
            foreach (var fixup in _fixups)
            {
                if (!_labels.TryGetValue(fixup.Label, out var target))
                    throw new InvalidDataException("策略 Hook 标签缺失：" + fixup.Label);
                Put32(result, fixup.At, unchecked((uint)(target - fixup.At - 4)));
            }
            return result;
        }
    }
}
