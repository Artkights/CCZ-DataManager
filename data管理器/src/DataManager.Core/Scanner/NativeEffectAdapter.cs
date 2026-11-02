using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataManager.Core.Scanner;

public sealed record NativeEffectHook(uint Address, uint OriginalTarget, uint AdapterTarget, bool IsAuxiliary, int RequestedAbilityOffset);
public sealed record NativeEffectAdapterState(bool IsSupported, bool IsInstalled,
    byte HpBaselineSelector, byte MpBaselineSelector, byte HpCurrentSelector, byte MpCurrentSelector,
    bool HasFfGuard, IReadOnlyList<string> Diagnostics, IReadOnlyList<NativeEffectHook> Hooks);
public sealed record NativeEffectAdapterBuildResult(byte[] Before, byte[] After,
    NativeEffectAdapterState State, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Pure byte-array PE32 adapter. It never writes a project file. Every installed E8 is recorded,
/// checked on read, and restored on removal. HP/MP override membership at six consumers rather
/// than changing a classifier's single return value. All other classifications keep their ABI.
/// </summary>
public static class NativeEffectAdapter
{
    private const string SectionName = ".dmefx";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DM.NATIVE.FX.V1\0");
    private const int CodeOffset = 4096;
    private const int Version = 1;

    public static NativeEffectAdapterState Read(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        try
        {
            var pe = new Pe(exe);
            var section = pe.Sections.SingleOrDefault(s => s.Name == SectionName);
            if (section is null)
            {
                var profile = Discover(pe);
                return State(profile, false, 37, 38, false, profile.Hooks.Select(h =>
                    new NativeEffectHook(h.Va, h.Target, 0, h.Auxiliary, h.RequestOffset)).ToArray());
            }
            var data = ReadInstalled(pe, section);
            var original = Restore(exe, data);
            var baseline = Discover(new Pe(original));
            if (data.HpBaseline != 37 || data.MpBaseline != 38 || baseline.Hooks.Count != 6)
                throw new InvalidDataException("原生适配器基准与已验证能力消费者不符。");
            return State(baseline, true, data.Hp, data.Mp, data.Guard,
                data.Hooks.Select(h => new NativeEffectHook(h.Va, h.Target, h.AdapterTarget, h.Auxiliary, h.RequestOffset)).ToArray());
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentException or JsonException)
        {
            return new(false, false, 37, 38, 37, 38, false,
                ["原生能力适配不可用：" + exception.Message], []);
        }
    }

    public static NativeEffectAdapterBuildResult Build(byte[] exe, byte hpSelector, byte mpSelector, bool ensureFfGuard = true)
    {
        ArgumentNullException.ThrowIfNull(exe);
        var state = Read(exe);
        if (!state.IsSupported) throw new InvalidDataException(string.Join("\n", state.Diagnostics));
        var source = state.IsInstalled ? Remove(exe).After : (byte[])exe.Clone();
        var pe = new Pe(source);
        var profile = Discover(pe);
        if (pe.SecuritySize != 0) throw new InvalidDataException("包含 Authenticode 签名的 EXE 不适用此原生适配器。");
        var sectionHeader = pe.SectionTable + pe.Sections.Count * 40;
        var firstRaw = pe.Sections.Where(s => s.RawSize != 0).Min(s => s.Raw);
        if (sectionHeader + 40 > firstRaw || sectionHeader + 40 > pe.SizeOfHeaders)
            throw new InvalidDataException("PE 节表没有完整的空闲节头空间。");
        // Some 6.4 images retain an obsolete, uncounted section header in this padding.
        // It is not a mapped section. Keep its exact bytes in OriginalHeader, and only use
        // padding when no entry point or data directory addresses the header area.
        if (U32(source, pe.Optional + 16) < pe.SizeOfHeaders)
            throw new InvalidDataException("PE入口位于文件头，不能使用节表尾部空间。");
        for (var directory = 0; directory < 16; directory++)
        {
            var directoryRva = U32(source, pe.Optional + 96 + directory * 8);
            if (directoryRva != 0 && directoryRva < pe.SizeOfHeaders)
                throw new InvalidDataException("PE数据目录引用文件头，不能使用节表尾部空间。");
        }
        var raw = Align((uint)source.Length, pe.FileAlignment);
        var rva = Align(pe.Sections.Max(s => checked(s.Rva + Math.Max(s.VirtualSize, s.RawSize))), pe.SectionAlignment);
        var adapterVa = checked(pe.ImageBase + rva);
        var body = new Code(checked(adapterVa + CodeOffset));
        var hooks = new List<StoredHook>();
        foreach (var hook in profile.Hooks)
        {
            var target = body.Position;
            EmitMembership(body, hook, hpSelector, mpSelector);
            var replacement = Call(hook.Va, target);
            hooks.Add(new() { Offset = hook.Offset, Va = hook.Va, Target = hook.Target,
                AdapterTarget = target, Auxiliary = hook.Auxiliary, RequestOffset = hook.RequestOffset,
                Original = Convert.ToBase64String(source.AsSpan(hook.Offset, 5)), Patched = Convert.ToBase64String(replacement) });
        }
        if (ensureFfGuard)
        {
            var target = body.Position;
            // Same three stack arguments as 43C794. A non-FF call tail-jumps to the original.
            // On FF both value and independent hit marker are zero; the outer personal result
            // has already been saved by 41301E and is still aggregated normally.
            body.Bytes(0x80, 0x7c, 0x24, 0x04, 0xff); // cmp byte [esp+4],FF
            body.Jcc(0x85, "guard-original");
            body.Bytes(0x33, 0xc0, 0x33, 0xc9, 0xc2, 0x0c, 0x00);
            body.Label("guard-original"); body.Jump(profile.EquipmentTarget);
            hooks.Add(new() { Offset = profile.GuardOffset, Va = profile.GuardVa, Target = profile.EquipmentTarget,
                AdapterTarget = target, Original = Convert.ToBase64String(source.AsSpan(profile.GuardOffset, 5)),
                Patched = Convert.ToBase64String(Call(profile.GuardVa, target)) });
        }
        var code = body.Finish();
        var payloadLength = checked(CodeOffset + code.Length);
        var rawSize = Align((uint)payloadLength, pe.FileAlignment);
        var after = new byte[checked((int)(raw + rawSize))];
        source.CopyTo(after, 0);
        var metadata = new Stored
        {
            OriginalLength = source.Length, OriginalHeader = Convert.ToBase64String(source.AsSpan(0, (int)firstRaw)),
            HpBaseline = 37, MpBaseline = 38, Hp = hpSelector, Mp = mpSelector, Guard = ensureFfGuard,
            CodeLength = code.Length, CodeHash = Convert.ToHexString(SHA256.HashData(code)),
            SectionRva = rva, SectionRaw = raw, SectionRawSize = rawSize, Hooks = hooks
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(metadata);
        if (json.Length + 56 > CodeOffset) throw new InvalidDataException("原生适配恢复元数据超过保留区域。");
        Magic.CopyTo(after, (int)raw);
        Put32(after, (int)raw + 16, Version); Put32(after, (int)raw + 20, json.Length);
        SHA256.HashData(json).CopyTo(after, (int)raw + 24);
        json.CopyTo(after, (int)raw + 56);
        code.CopyTo(after, (int)raw + CodeOffset);
        after.AsSpan(sectionHeader, 40).Clear();
        Encoding.ASCII.GetBytes(SectionName).CopyTo(after, sectionHeader);
        Put32(after, sectionHeader + 8, payloadLength); Put32(after, sectionHeader + 12, rva);
        Put32(after, sectionHeader + 16, rawSize); Put32(after, sectionHeader + 20, raw);
        Put32(after, sectionHeader + 36, 0x60000020u); // code, execute, read; selectors are immediate constants
        Put16(after, pe.PeOffset + 6, checked((ushort)(pe.Sections.Count + 1)));
        Put32(after, pe.Optional + 4, checked(U32(source, pe.Optional + 4) + rawSize));
        Put32(after, pe.Optional + 56, Align(checked(rva + (uint)payloadLength), pe.SectionAlignment));
        Put32(after, pe.Optional + 64, 0); // checksum is optional for this user-mode executable
        foreach (var hook in hooks) Convert.FromBase64String(hook.Patched).CopyTo(after, hook.Offset);
        var verified = Read(after);
        if (!verified.IsSupported || !verified.IsInstalled || verified.HpCurrentSelector != hpSelector ||
            verified.MpCurrentSelector != mpSelector || verified.HasFfGuard != ensureFfGuard)
            throw new InvalidDataException("原生适配候选复读失败：" + string.Join("\n", verified.Diagnostics));
        return new((byte[])exe.Clone(), after, verified, verified.Diagnostics);
    }

    public static NativeEffectAdapterBuildResult Remove(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        var pe = new Pe(exe);
        var section = pe.Sections.SingleOrDefault(s => s.Name == SectionName);
        if (section is null)
        {
            var unchanged = Read(exe);
            if (!unchanged.IsSupported) throw new InvalidDataException(string.Join("\n", unchanged.Diagnostics));
            return new((byte[])exe.Clone(), (byte[])exe.Clone(), unchanged, unchanged.Diagnostics);
        }
        var data = ReadInstalled(pe, section);
        var restored = Restore(exe, data);
        var state = Read(restored);
        if (!state.IsSupported || state.IsInstalled) throw new InvalidDataException("原生适配卸载复读失败。");
        return new((byte[])exe.Clone(), restored, state, ["已按节内记录恢复全部原始 E8、原 PE 头及原文件长度。"]);
    }

    private static NativeEffectAdapterState State(Profile p, bool installed, byte hp, byte mp, bool guard, IReadOnlyList<NativeEffectHook> hooks)
        => new(true, installed, 37, 38, hp, mp, guard,
            [$"已验证 {p.Hooks.Count} 处原生能力消费者：武器/防具计算、数值显示及预览；辅助计算、预览及基础能力分派。",
             "能力匹配适配点：" + string.Join("、", p.Hooks.Select(h => $"0x{h.Va:X8}")),
             "保留原基础攻击/精神判定及基础能力文字显示：" + string.Join("、", p.PreservedAuxCalls.Select(a => $"0x{a:X8}")),
             installed ? "专用 .dmefx 节签名、元数据、代码摘要与全部调用点复读通过。" : "原分类器、调用者上下文及栈清理方式均通过机器码签名校验。",
             "HP/MP各自使用装备原值与原等级解释；同号可同时归属HP、MP。FF代表该能力未使用装备号。"], hooks);

    private static byte[] Restore(byte[] bytes, Stored data)
    {
        var result = bytes.AsSpan(0, data.OriginalLength).ToArray();
        Convert.FromBase64String(data.OriginalHeader).CopyTo(result, 0);
        foreach (var hook in data.Hooks) Convert.FromBase64String(hook.Original).CopyTo(result, hook.Offset);
        return result;
    }

    private static Stored ReadInstalled(Pe pe, Section s)
    {
        var b = pe.Bytes;
        if (s != pe.Sections[^1] || s.Raw + s.RawSize != b.Length || s.RawSize < CodeOffset ||
            !b.AsSpan((int)s.Raw, 16).SequenceEqual(Magic) || U32(b, (int)s.Raw + 16) != Version)
            throw new InvalidDataException(".dmefx 节版本、签名、位置或长度不符，不能视为本工具安装的适配器。");
        var length = checked((int)U32(b, (int)s.Raw + 20));
        if (length <= 0 || length > CodeOffset - 56) throw new InvalidDataException("原生适配元数据长度无效。");
        var json = b.AsSpan((int)s.Raw + 56, length);
        if (!SHA256.HashData(json).AsSpan().SequenceEqual(b.AsSpan((int)s.Raw + 24, 32)))
            throw new InvalidDataException("原生适配恢复元数据校验失败。");
        var data = JsonSerializer.Deserialize<Stored>(json) ?? throw new InvalidDataException("缺少适配恢复元数据。");
        if (data.SectionRva != s.Rva || data.SectionRaw != s.Raw || data.SectionRawSize != s.RawSize ||
            data.CodeLength <= 0 || data.CodeLength > s.RawSize - CodeOffset ||
            data.OriginalLength <= 0 || data.OriginalLength > s.Raw || data.Hooks.Count != (data.Guard ? 7 : 6) ||
            !Convert.ToHexString(SHA256.HashData(b.AsSpan((int)s.Raw + CodeOffset, data.CodeLength))).Equals(data.CodeHash, StringComparison.Ordinal))
            throw new InvalidDataException("原生适配代码、节布局或恢复长度校验失败。");
        foreach (var h in data.Hooks)
        {
            if (h.Offset < pe.SizeOfHeaders || h.Offset > data.OriginalLength - 5 ||
                Convert.FromBase64String(h.Original).Length != 5 || Convert.FromBase64String(h.Patched).Length != 5 ||
                !b.AsSpan(h.Offset, 5).SequenceEqual(Convert.FromBase64String(h.Patched)) ||
                !Call(h.Va, h.AdapterTarget).AsSpan().SequenceEqual(Convert.FromBase64String(h.Patched)))
                throw new InvalidDataException("原生适配调用点已被其他修改覆盖，拒绝继续改写或卸载。");
        }
        // Reconstruct the header we own, and reject subsequent header/section changes rather
        // than silently dropping them when restoring the original header.
        var header = Convert.FromBase64String(data.OriginalHeader);
        if (header.Length < pe.SizeOfHeaders || header.Length > data.OriginalLength)
            throw new InvalidDataException("原始PE头备份范围无效。");
        var originalPe = new Pe(Restore(b, data));
        if (originalPe.Sections.Count + 1 != pe.Sections.Count) throw new InvalidDataException("原生适配原始节数不符。");
        var expected = b.AsSpan(0, header.Length).ToArray();
        header.CopyTo(expected, 0);
        var sh = originalPe.SectionTable + originalPe.Sections.Count * 40;
        b.AsSpan(sh, 40).CopyTo(expected.AsSpan(sh));
        Put16(expected, pe.PeOffset + 6, (ushort)pe.Sections.Count);
        Put32(expected, pe.Optional + 4, checked(U32(header, pe.Optional + 4) + s.RawSize));
        Put32(expected, pe.Optional + 56, Align(checked(s.Rva + s.VirtualSize), pe.SectionAlignment));
        Put32(expected, pe.Optional + 64, 0);
        if (!expected.AsSpan().SequenceEqual(b.AsSpan(0, header.Length)))
            throw new InvalidDataException("原生适配安装后PE头另有修改，需先解决变更后才能更新或卸载。");
        return data;
    }

    private static void EmitMembership(Code c, Site h, byte hp, byte mp)
    {
        // No EBP prologue: EBP deliberately stays the consumer's frame. Preserve the original
        // classifier's stack cleanup (auxiliary is cdecl in 6.4/6.5, stdcall in 6.6).
        c.Bytes(0x52, 0x51, 0xff, 0x74, 0x24, 0x0c); // push edx;push ecx;push original item argument
        c.Call(h.Target);
        if (!h.CalleeCleanup) c.Bytes(0x83, 0xc4, 0x04);
        // +11 below 1A is an ordinary weapon/armour type, not an auxiliary effect code.
        // A low selector must never turn its base attack/defence into an HP/MP bonus.
        if (h.Auxiliary) { c.Bytes(0x80, 0x79, 0x11, 0x1a); c.Jcc(0x82, "done"); }
        c.Bytes(0x80, 0x7d, (byte)h.RequestOffset, 0x05); c.Jcc(0x84, "hp");
        c.Bytes(0x80, 0x7d, (byte)h.RequestOffset, 0x06); c.Jcc(0x84, "mp");
        c.Jump("done");
        c.Label("hp");
        if (hp == 255) c.Jump("miss");
        else { c.Bytes(0x80, 0x79, h.Auxiliary ? (byte)0x11 : (byte)0x14, hp); c.Jcc(0x84, "hit"); c.Jump("miss"); }
        c.Label("mp");
        if (mp == 255) c.Jump("miss");
        else { c.Bytes(0x80, 0x79, h.Auxiliary ? (byte)0x11 : (byte)0x14, mp); c.Jcc(0x84, "hit"); c.Jump("miss"); }
        c.Label("hit"); c.Bytes(0x0f, 0xb6, 0x45, (byte)h.RequestOffset); c.Jump("done");
        c.Label("miss"); c.Bytes(0xb8, 0xff, 0xff, 0xff, 0xff);
        c.Label("done"); c.Bytes(0x59, 0x5a);
        if (h.CalleeCleanup) c.Bytes(0xc2, 0x04, 0x00); else c.Bytes(0xc3);
        c.EndLabels();
    }

    private static Profile Discover(Pe pe)
    {
        // Signatures intentionally include consumers and their ABI, not a version label or a
        // guessed address. Relative CALLs and table pointers are resolved through this PE.
        var classifier = pe.FindUnique("55 8B EC 8B 4D 08 E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 C0 3C 5C 75 04 B0 09 EB 18 3C FE 75 04 B0 0A EB 10 2C 20 3C 08 77 08 8A 80 ?? ?? ?? ?? EB 02 0C FF 5D C2 04 00");
        var aux = pe.FindUnique("55 8B EC 8B 4D 08 E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 C0 3C 5C 75 04 B0 09 EB ?? 3C 1A");
        // The leading auxiliary signature alone does not prove HP=0x25 / MP=0x26:
        // those are immediate comparisons later in the function. Verify its complete
        // decision tree, both source CALLs, base-type table and exact return convention.
        if (!pe.Matches(aux, "55 8B EC 8B 4D 08 E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 C0 " +
            "3C 5C 75 04 B0 09 EB 60 3C 1A 73 0A D1 E8 8A 80 ?? ?? ?? ?? EB 52 " +
            "3C 20 75 04 33 C0 EB 4A 3C 21 75 04 B0 01 EB 42 3C 22 75 04 B0 02 EB 3A " +
            "3C 23 75 04 B0 03 EB 32 3C 24 75 04 B0 04 EB 2A 3C 25 75 04 B0 05 EB 22 " +
            "3C 26 75 04 B0 06 EB 1A 3C 27 75 04 B0 08 EB 12 3C 28 75 04 B0 07 EB 0A " +
            "3C FE 75 04 B0 0A EB 02 0C FF 8B E5 5D"))
            throw new InvalidDataException("辅助分类器完整分派规则与已证HP/MP基准不符。");
        var tableVa = U32(pe.Bytes, classifier + 43);
        if (!pe.ReadVa(tableVa, 9).SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 5, 6, 8, 7 }))
            throw new InvalidDataException("原生能力分类表不是已证实的0..8映射，拒绝推测HP/MP基准。");
        var itemReader = pe.Target(classifier + 6);
        var getter = pe.Target(classifier + 11);
        var auxGetter = pe.Target(aux + 11);
        if (pe.Target(aux + 6) != itemReader ||
            !pe.ReadVa(getter, 9).SequenceEqual(new byte[] { 0x55, 0x8b, 0xec, 0x0f, 0xb6, 0x41, 0x14, 0x5d, 0xc3 }) ||
            !pe.ReadVa(auxGetter, 9).SequenceEqual(new byte[] { 0x55, 0x8b, 0xec, 0x0f, 0xb6, 0x41, 0x11, 0x5d, 0xc3 }) ||
            !pe.ReadVa(itemReader, 6).SequenceEqual(new byte[] { 0x55, 0x8b, 0xec, 0x6b, 0xc9, 0x19 }))
            throw new InvalidDataException("原生物品记录定位/字段读取ABI不符。");
        var auxTable = pe.ReadVa(U32(pe.Bytes, aux + 35), 13);
        if (!auxTable.SequenceEqual(new byte[] { 10, 0, 0, 0, 0, 0, 0, 2, 2, 0, 1, 1, 1 }) &&
            !auxTable.SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 2, 2, 10, 1, 1, 1 }))
            throw new InvalidDataException("基础物品分类表存在未知映射，不能保证保留原分类语义。");
        var auxEnd = pe.Bytes.AsSpan(aux + 0x7b, 4);
        var auxCleanup = auxEnd.SequenceEqual(new byte[] { 0x8b, 0xe5, 0x5d, 0xc2 });
        if (!auxCleanup && !auxEnd.SequenceEqual(new byte[] { 0x8b, 0xe5, 0x5d, 0xc3 }))
            throw new InvalidDataException("辅助能力分类器栈清理指令未知。");
        if (auxCleanup && !pe.Bytes.AsSpan(aux + 0x7e, 3).SequenceEqual(new byte[] { 0xc2, 4, 0 }))
            throw new InvalidDataException("辅助分类器stdcall参数清理宽度不等于4。");
        var sites = new List<Site>();
        foreach (var call in pe.CallsTo(pe.Va(classifier)))
        {
            var tail = pe.Bytes.AsSpan(call + 5, Math.Min(40, pe.Bytes.Length - call - 5));
            int request;
            if (tail.StartsWith(new byte[] { 0x38, 0x45, 0x08, 0x75 })) request = 8;
            else if (tail.StartsWith(new byte[] { 0x90, 0x90, 0x90, 0x38, 0x45, 0x08, 0x75 })) request = 8;
            else if (tail.StartsWith(new byte[] { 0x90, 0x90, 0x90, 0x89, 0x45, 0xf8, 0x3c, 0x09, 0x75, 0x08, 0x83, 0x7d, 0x10, 0x05 })) request = 16;
            else throw new InvalidDataException("发现未验证的武器/防具原生能力消费者。");
            sites.Add(new(call, pe.Va(call), pe.Va(classifier), false, request, true));
        }
        if (sites.Count != 3) throw new InvalidDataException("武器/防具能力消费者数量不等于已证实的3处。");
        var preservedAuxCalls = new List<uint>();
        foreach (var call in pe.CallsTo(pe.Va(aux)))
        {
            var tail = pe.Bytes.AsSpan(call + 5, Math.Min(20, pe.Bytes.Length - call - 5));
            var skip = tail.StartsWith(new byte[] { 0x83, 0xc4, 0x04 }) || tail.StartsWith(new byte[] { 0x90, 0x90, 0x90 }) ? 3 : 0;
            if (tail[skip..].StartsWith(new byte[] { 0x38, 0x45, 0x08, 0x75 }) ||
                tail.StartsWith(new byte[] { 0x8b, 0x4d, 0x08, 0x3c, 0x0a, 0x75 }))
                sites.Add(new(call, pe.Va(call), pe.Va(aux), true, 8, auxCleanup));
            else if (IsPreservedAuxConsumer(tail, skip)) preservedAuxCalls.Add(pe.Va(call));
            else throw new InvalidDataException($"辅助分类器存在未审查消费者0x{pe.Va(call):X8}。");
        }
        if (sites.Count != 6 || preservedAuxCalls.Count != 4)
            throw new InvalidDataException("辅助能力消费者清单与已证实的3处匹配、4处基础分类不符。");
        var equipment = pe.FindUnique("55 8B EC 51 FF 75 08 E8 ?? ?? ?? ?? 85 C0 74 1B B0 64 83 7D 0C 00 75 0E FF 75 10 FF 75 08 8B 4D FC E8 ?? ?? ?? ?? 33 C9 41 EB 02 33 C9 8B E5 5D C2 0C 00");
        var guardCalls = pe.CallsTo(pe.Va(equipment)).Where(call => call >= 12 &&
            pe.Bytes.AsSpan(call - 12, 12).SequenceEqual(new byte[] { 0xff, 0x75, 0x10, 0xff, 0x75, 0x14, 0xff, 0x75, 0x0c, 0x8b, 0x4d, 0xfc }) &&
            pe.Bytes.AsSpan(call + 5, 6).SequenceEqual(new byte[] { 0x5a, 0x50, 0x8b, 0x4d, 0x10, 0xe8 })).ToArray();
        if (guardCalls.Length != 1) throw new InvalidDataException("装备与个人结果汇合调用点未唯一识别。");
        return new(sites, preservedAuxCalls, guardCalls[0], pe.Va(guardCalls[0]), pe.Va(equipment));
    }

    private static bool IsPreservedAuxConsumer(ReadOnlySpan<byte> tail, int skip)
    {
        // Four callers have no requested-HP/MP parameter. They classify an ordinary weapon's
        // base attack/spirit (0/2/0A), or print its base attack/spirit/defence label. Feeding
        // their unrelated EBP+8 into membership would corrupt combat predicates/UI arguments.
        if (tail.Length >= 8 && tail[0] == 0x84 && tail[1] == 0xc0 && tail[2] is 0x74 or 0xeb &&
            tail.Slice(3, 5).SequenceEqual(new byte[] { 4, 0x3c, 0x0a, 0x75, 0x4e })) return true;
        if (tail.Length >= 12 && tail[0] == 0x3c && tail[1] == 2 && tail[2] is 0x74 or 0xeb &&
            tail.Slice(3, 9).SequenceEqual(new byte[] { 8, 0x3c, 0x0a, 0x74, 4, 0x33, 0xc0, 0xeb, 0x57 })) return true;
        var content = tail[skip..];
        if (content.Length >= 11 && content[..6].SequenceEqual(new byte[] { 0x88, 0x45, 0xf0, 0x3c, 0x0a, 0x75 }) &&
            content.Slice(7, 4).SequenceEqual(new byte[] { 0xc6, 0x45, 0xf0, 0 })) return true;
        if (content.StartsWith(new byte[] { 0x3c, 0x0a, 0x75, 0x10, 0xff, 0x75, 0xf8, 0xff, 0x75, 0x14, 0xff, 0x75, 0x08, 0xe8 })) return true;
        // 6.4 equivalent text formatter uses separate 0,1,2 branches instead of 0A formatting.
        return content.StartsWith(new byte[] { 0x3c, 0, 0x75, 0x0c, 0x68 });
    }

    private sealed record Profile(List<Site> Hooks, List<uint> PreservedAuxCalls, int GuardOffset, uint GuardVa, uint EquipmentTarget);
    private sealed record Site(int Offset, uint Va, uint Target, bool Auxiliary, int RequestOffset, bool CalleeCleanup);
    private sealed class Stored
    {
        public Stored() { }
        public int OriginalLength { get; set; }
        public string OriginalHeader { get; set; } = "";
        public byte HpBaseline { get; set; }
        public byte MpBaseline { get; set; }
        public byte Hp { get; set; }
        public byte Mp { get; set; }
        public bool Guard { get; set; }
        public int CodeLength { get; set; }
        public string CodeHash { get; set; } = "";
        public uint SectionRva { get; set; }
        public uint SectionRaw { get; set; }
        public uint SectionRawSize { get; set; }
        public List<StoredHook> Hooks { get; set; } = [];
    }
    private sealed class StoredHook
    {
        public StoredHook() { }
        public int Offset { get; set; }
        public uint Va { get; set; }
        public uint Target { get; set; }
        public uint AdapterTarget { get; set; }
        public bool Auxiliary { get; set; }
        public int RequestOffset { get; set; }
        public string Original { get; set; } = "";
        public string Patched { get; set; } = "";
    }

    private sealed class Code(uint start)
    {
        private readonly List<byte> bytes = [];
        private readonly Dictionary<string, uint> labels = [];
        private readonly List<(int Offset, string Label)> branches = [];
        public uint Position => checked(start + (uint)bytes.Count);
        public void Bytes(params byte[] values) => bytes.AddRange(values);
        public void Call(uint target) { var va = Position; Bytes(NativeEffectAdapter.Call(va, target)); }
        public void Jump(uint target) { var va = Position; Bytes(0xe9); Bytes(BitConverter.GetBytes(unchecked((int)(target - va - 5)))); }
        public void Jump(string label) { Bytes(0xe9); branches.Add((bytes.Count, label)); Bytes(0, 0, 0, 0); }
        public void Jcc(byte opcode, string label) { Bytes(0x0f, opcode); branches.Add((bytes.Count, label)); Bytes(0, 0, 0, 0); }
        public void Label(string name) => labels.Add(name, Position);
        public void EndLabels()
        {
            foreach (var (offset, label) in branches)
            {
                var rel = unchecked((int)(labels[label] - start - (uint)offset - 4));
                var value = BitConverter.GetBytes(rel);
                for (var i = 0; i < 4; i++) bytes[offset + i] = value[i];
            }
            branches.Clear(); labels.Clear();
        }
        public byte[] Finish() { EndLabels(); return bytes.ToArray(); }
    }

    private sealed record Section(string Name, uint Rva, uint VirtualSize, uint Raw, uint RawSize, bool Executable);
    private sealed class Pe
    {
        public byte[] Bytes { get; }
        public int PeOffset { get; }
        public int Optional { get; }
        public int SectionTable { get; }
        public uint ImageBase { get; }
        public uint FileAlignment { get; }
        public uint SectionAlignment { get; }
        public uint SizeOfHeaders { get; }
        public uint SecuritySize { get; }
        public List<Section> Sections { get; } = [];
        public Pe(byte[] bytes)
        {
            Bytes = bytes;
            if (bytes.Length < 64 || U16(bytes, 0) != 0x5a4d) throw new InvalidDataException("缺少MZ头。");
            PeOffset = checked((int)U32(bytes, 0x3c)); Optional = checked(PeOffset + 24);
            if (PeOffset < 64 || Optional + 224 > bytes.Length || U32(bytes, PeOffset) != 0x4550 ||
                U16(bytes, PeOffset + 4) != 0x14c || U16(bytes, Optional) != 0x10b)
                throw new InvalidDataException("只支持完整PE32/x86映像。");
            ImageBase = U32(bytes, Optional + 28); SectionAlignment = U32(bytes, Optional + 32);
            FileAlignment = U32(bytes, Optional + 36); SizeOfHeaders = U32(bytes, Optional + 60);
            SecuritySize = U32(bytes, Optional + 128 + 4);
            if (FileAlignment == 0 || SectionAlignment < FileAlignment || (FileAlignment & (FileAlignment - 1)) != 0 ||
                (SectionAlignment & (SectionAlignment - 1)) != 0 || SizeOfHeaders > bytes.Length)
                throw new InvalidDataException("PE对齐或文件头范围无效。");
            SectionTable = checked(Optional + U16(bytes, PeOffset + 20));
            var count = U16(bytes, PeOffset + 6);
            if (count == 0 || SectionTable + count * 40 > bytes.Length) throw new InvalidDataException("PE节表范围无效。");
            for (var i = 0; i < count; i++)
            {
                var h = SectionTable + i * 40;
                var s = new Section(Encoding.ASCII.GetString(bytes, h, 8).TrimEnd('\0'), U32(bytes, h + 12),
                    U32(bytes, h + 8), U32(bytes, h + 20), U32(bytes, h + 16), (U32(bytes, h + 36) & 0x20000000) != 0);
                if ((ulong)s.Raw + s.RawSize > (ulong)bytes.Length) throw new InvalidDataException("PE节原始范围越界。");
                Sections.Add(s);
            }
        }
        public uint Va(int offset)
        {
            var s = Sections.SingleOrDefault(s => offset >= s.Raw && (long)offset < (long)s.Raw + s.RawSize)
                ?? throw new InvalidDataException("文件位置不属于唯一PE节。");
            return checked(ImageBase + s.Rva + (uint)offset - s.Raw);
        }
        public int Offset(uint va)
        {
            if (va < ImageBase) throw new InvalidDataException("VA在映像基址之前。");
            var rva = va - ImageBase;
            var s = Sections.SingleOrDefault(s => rva >= s.Rva && rva < (ulong)s.Rva + s.RawSize)
                ?? throw new InvalidDataException("VA没有唯一磁盘映射。");
            return checked((int)(s.Raw + rva - s.Rva));
        }
        public ReadOnlySpan<byte> ReadVa(uint va, int length) => Bytes.AsSpan(Offset(va), length);
        public uint Target(int call)
        {
            if (Bytes[call] != 0xe8) throw new InvalidDataException("预期E8调用指令。");
            return unchecked(Va(call) + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(call + 1, 4)));
        }
        public IEnumerable<int> CallsTo(uint target)
        {
            foreach (var s in Sections.Where(s => s.Executable))
                for (var i = checked((int)s.Raw); i <= (long)s.Raw + s.RawSize - 5; i++)
                    if (Bytes[i] == 0xe8 && Target(i) == target) yield return i;
        }
        public int FindUnique(string text)
        {
            var pattern = text.Split(' ').Select(x => x == "??" ? (byte?)null : Convert.ToByte(x, 16)).ToArray();
            var hits = new List<int>();
            foreach (var s in Sections.Where(s => s.Executable))
                for (var i = checked((int)s.Raw); i <= (long)s.Raw + s.RawSize - pattern.Length; i++)
                {
                    var same = true;
                    for (var j = 0; j < pattern.Length; j++) if (pattern[j] is byte value && Bytes[i + j] != value) { same = false; break; }
                    if (same) hits.Add(i);
                }
            if (hits.Count != 1) throw new InvalidDataException($"机器码签名未唯一匹配（{hits.Count}处）。");
            return hits[0];
        }
        public bool Matches(int offset, string text)
        {
            var pattern = text.Split(' ').Select(x => x == "??" ? (byte?)null : Convert.ToByte(x, 16)).ToArray();
            if (offset < 0 || offset > Bytes.Length - pattern.Length) return false;
            for (var j = 0; j < pattern.Length; j++)
                if (pattern[j] is byte value && Bytes[offset + j] != value) return false;
            return true;
        }
    }
    private static byte[] Call(uint from, uint target) => [0xe8, .. BitConverter.GetBytes(unchecked((int)(target - from - 5)))];
    private static uint Align(uint value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
    private static void Put16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);
    private static void Put32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
    private static void Put32(byte[] b, int o, int v) => Put32(b, o, checked((uint)v));
}
