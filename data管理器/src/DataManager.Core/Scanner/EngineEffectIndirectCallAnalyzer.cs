using System.Buffers.Binary;
using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

internal sealed record EngineEffectIndirectAudit(
    IReadOnlyList<EngineEffectIndirectCall> Calls,
    int EntryPointerCount,
    int EntryJumpCount);

/// <summary>
/// Audits every <c>call r/m32</c> in the executable sections so the call chain is
/// proven complete: an effect entry reached through a register, a function pointer
/// or a jump table would be invisible to the E8 scan. Each candidate must decode
/// as an instruction boundary inside a known function; absolute pointer loads and
/// register values set by <c>mov</c> in the same function are reduced to a target.
/// </summary>
internal static class EngineEffectIndirectCallAnalyzer
{
    private const int FunctionDecodeBytes = 0x2000;
    private const int MaximumTableCandidates = 64;

    public static EngineEffectIndirectAudit Analyze(EngineEffectBinaryImage image,
        EngineEffectCallGraph callGraph, IEnumerable<uint> effectEntries)
    {
        var entries = effectEntries.ToHashSet();
        var decodedByEntry = new Dictionary<uint, IReadOnlyList<DecodedInstruction>>();
        var seen = new HashSet<uint>();
        var calls = new List<EngineEffectIndirectCall>();
        var jumpCount = 0;
        foreach (var section in image.ExecutableSections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, image.Bytes.LongLength - start));
            if (length < 2) continue;
            var span = image.Bytes.AsSpan(start, length);
            for (var index = 0; index <= length - 2; index++)
            {
                if (span[index] == 0xFF)
                {
                    if (((span[index + 1] >> 3) & 7) != 2) continue;
                    var address = section.VirtualAddress + (uint)index;
                    if (!seen.Add(address)) continue;
                    var decoded = Boundary(image, callGraph, decodedByEntry, address);
                    if (decoded is not { } item || item.Instruction.Mnemonic != Mnemonic.Call) continue;
                    calls.Add(Classify(image, callGraph, decodedByEntry, address, item, entries));
                }
                else if (span[index] is 0xE9 or 0xEB)
                {
                    var address = section.VirtualAddress + (uint)index;
                    if (!seen.Add(address)) continue;
                    var decoded = Boundary(image, callGraph, decodedByEntry, address);
                    if (decoded is not { } item || item.Instruction.Mnemonic != Mnemonic.Jmp) continue;
                    var target = unchecked((uint)item.Instruction.NearBranchTarget);
                    if (entries.Contains(target)) jumpCount++;
                }
            }
        }

        var pointerCount = 0;
        foreach (var section in image.Sections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, image.Bytes.LongLength - start));
            if (length < 4) continue;
            var span = image.Bytes.AsSpan(start, length);
            for (var index = 0; index <= length - 4; index += 4)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(index, 4));
                if (entries.Contains(value)) pointerCount++;
            }
        }
        return new EngineEffectIndirectAudit(calls.OrderBy(call => call.Address).ToArray(), pointerCount, jumpCount);
    }

    private static DecodedInstruction? Boundary(EngineEffectBinaryImage image, EngineEffectCallGraph callGraph,
        Dictionary<uint, IReadOnlyList<DecodedInstruction>> decodedByEntry, uint address)
    {
        var entry = callGraph.CallerEntryOf(address);
        if (entry is not { } caller) return null;
        if (!decodedByEntry.TryGetValue(caller, out var decoded))
        {
            decoded = EngineEffectDecoder.DecodeForward(image, caller, FunctionDecodeBytes);
            decodedByEntry[caller] = decoded;
        }
        return decoded.FirstOrDefault(item => item.Address == address);
    }

    private static EngineEffectIndirectCall Classify(EngineEffectBinaryImage image,
        EngineEffectCallGraph callGraph,
        Dictionary<uint, IReadOnlyList<DecodedInstruction>> decodedByEntry,
        uint address, DecodedInstruction decoded, IReadOnlySet<uint> entries)
    {
        var instruction = decoded.Instruction;
        var caller = callGraph.CallerEntryOf(address);
        var kind = instruction.GetOpKind(0);
        if (kind == OpKind.Register)
        {
            var register = instruction.GetOpRegister(0);
            var target = ResolveRegister(image, decodedByEntry, callGraph, caller, address, register);
            var hit = target is { } value && entries.Contains(value);
            return new EngineEffectIndirectCall
            {
                Address = address,
                CallerEntry = caller,
                Instruction = decoded.Text,
                Kind = hit ? "特效入口" : target is { } resolved && image.SectionOf(resolved) is { IsExecutable: true }
                    ? "代码目标"
                    : "寄存器",
                ResolvedTarget = target,
                TargetsEffectEntry = hit,
                Evidence = target is { } final
                    ? $"寄存器 {RegisterName(register)} 由函数内最近一次写入确定为 0x{final:X8}" +
                      (hit ? "（特效入口）" : image.SectionOf(final) is { IsExecutable: true } ? "（代码目标）" : "（镜像外）")
                    : $"寄存器 {RegisterName(register)} 在函数内没有静态可定的值"
            };
        }

        if (kind != OpKind.Memory)
        {
            return new EngineEffectIndirectCall
            {
                Address = address,
                CallerEntry = caller,
                Instruction = decoded.Text,
                Kind = "寄存器",
                Evidence = "调用目标不是寄存器或内存，未解析"
            };
        }

        var hasBase = instruction.MemoryBase != Register.None;
        var hasIndex = instruction.MemoryIndex != Register.None;
        if (hasIndex)
        {
            var operand = MemoryOperand(instruction);
            var tableBase = hasBase ? (uint?)null : unchecked((uint)instruction.MemoryDisplacement64);
            var candidates = tableBase is { } baseAddress
                ? ReadTableCandidates(image, baseAddress, MaximumTableCandidates)
                : [];
            var evidence = hasBase
                ? $"带索引的内存调用 {operand}（基址 + 索引×{instruction.MemoryIndexScale}，基址运行期决定），未解析"
                : $"偏移表 0x{tableBase:X8} 的 {operand} 调用（索引运行期决定），" +
                  (candidates.Count > 0
                      ? $"前 {MaximumTableCandidates} 项中有 {candidates.Count} 个可执行候选目标：" +
                        string.Join("/", candidates.Take(8).Select(target => $"0x{target:X8}")) +
                        (candidates.Count > 8 ? "…" : string.Empty)
                      : "前 " + MaximumTableCandidates + " 项没有可执行目标");
            return new EngineEffectIndirectCall
            {
                Address = address,
                CallerEntry = caller,
                Instruction = decoded.Text,
                Kind = "表",
                MemberExpression = operand,
                TableAddress = tableBase,
                CandidateTargets = candidates,
                Evidence = evidence
            };
        }
        if (hasBase)
        {
            var operand = MemoryOperand(instruction);
            var register = instruction.MemoryBase;
            var baseAddress = ResolveRegister(image, decodedByEntry, callGraph, caller, address, register);
            var displacement = unchecked((int)instruction.MemoryDisplacement64);
            uint? memberTarget = null;
            if (baseAddress is { } resolvedBase)
            {
                var memberAddress = unchecked(resolvedBase + (uint)displacement);
                if (image.SectionOf(memberAddress) is { IsExecutable: true }) memberTarget = memberAddress;
            }
            var baseText = baseAddress is { } value
                ? $"基址 {RegisterName(register)} 在函数内定为 0x{value:X8}"
                : $"基址 {RegisterName(register)} 在函数内没有静态可定的值";
            return new EngineEffectIndirectCall
            {
                Address = address,
                CallerEntry = caller,
                Instruction = decoded.Text,
                Kind = "成员",
                MemberExpression = operand,
                CandidateTargets = memberTarget is { } target ? [target] : [],
                Evidence = $"成员调用 {operand}（对象成员/虚表槽），{baseText}" +
                           (memberTarget is { } executable
                               ? $"，按该基址读出的成员为可执行地址 0x{executable:X8}"
                               : "，成员槽运行期决定")
            };
        }

        var slot = unchecked((uint)instruction.MemoryDisplacement64);
        var pointer = ReadDword(image, slot);
        if (pointer is not { } targetAddress)
        {
            return new EngineEffectIndirectCall
            {
                Address = address,
                CallerEntry = caller,
                Instruction = decoded.Text,
                Kind = "导入表",
                Evidence = $"绝对地址 0x{slot:X8} 不在文件内，调用目标未知"
            };
        }
        var targetSection = image.SectionOf(targetAddress);
        var slotSection = image.SectionOf(slot);
        var hitEntry = entries.Contains(targetAddress);
        var targetKind = hitEntry ? "特效入口" : targetSection is { IsExecutable: true } ? "代码目标" : "导入表";
        return new EngineEffectIndirectCall
        {
            Address = address,
            CallerEntry = caller,
            Instruction = decoded.Text,
            Kind = targetKind,
            ResolvedTarget = targetAddress,
            TargetsEffectEntry = hitEntry,
            Evidence = $"指针槽 0x{slot:X8}（{slotSection?.Name ?? "镜像外"}）→ 0x{targetAddress:X8}" +
                       (hitEntry ? "（特效入口）"
                           : targetSection is { IsExecutable: true } ? $"（{targetSection.Name} 代码目标）"
                           : "（镜像外，导入表/外部地址）")
        };
    }

    private static uint? ResolveRegister(EngineEffectBinaryImage image,
        Dictionary<uint, IReadOnlyList<DecodedInstruction>> decodedByEntry, EngineEffectCallGraph callGraph,
        uint? caller, uint callAddress, Register register)
    {
        if (caller is not { } entry) return null;
        if (!decodedByEntry.TryGetValue(entry, out var decoded))
        {
            decoded = EngineEffectDecoder.DecodeForward(image, entry, FunctionDecodeBytes);
            decodedByEntry[entry] = decoded;
        }
        uint? value = null;
        foreach (var item in decoded)
        {
            if (item.Address >= callAddress) break;
            var instruction = item.Instruction;
            if (instruction.OpCount == 0) continue;
            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch
                or FlowControl.Return or FlowControl.IndirectBranch or FlowControl.IndirectCall
                or FlowControl.Interrupt or FlowControl.Exception)
                value = null;
            if (instruction.GetOpKind(0) != OpKind.Register ||
                instruction.GetOpRegister(0) != register) continue;
            var source = instruction.GetOpKind(1);
            if (instruction.Mnemonic != Mnemonic.Mov)
            {
                value = null;
                continue;
            }
            if (source == OpKind.Immediate32)
                value = instruction.Immediate32;
            else if (source == OpKind.Immediate8)
                value = instruction.Immediate8;
            else if (source == OpKind.Immediate8to32)
                value = unchecked((uint)instruction.Immediate8to32);
            else if (source == OpKind.Memory && instruction.MemoryBase == Register.None &&
                     instruction.MemoryIndex == Register.None)
                value = ReadDword(image, unchecked((uint)instruction.MemoryDisplacement64));
            else
                value = null;
        }
        return value;
    }

    /// <summary>Reconstructs the memory operand as <c>[base+index*scale+disp]</c> for evidence.</summary>
    private static string MemoryOperand(Instruction instruction)
    {
        var text = new System.Text.StringBuilder("[");
        if (instruction.MemoryBase != Register.None) text.Append(RegisterName(instruction.MemoryBase));
        if (instruction.MemoryIndex != Register.None)
        {
            if (text.Length > 1) text.Append('+');
            text.Append(RegisterName(instruction.MemoryIndex));
            if (instruction.MemoryIndexScale != 1) text.Append('*').Append(instruction.MemoryIndexScale);
        }
        var displacement = unchecked((int)instruction.MemoryDisplacement64);
        if (displacement != 0 || text.Length == 1)
        {
            if (text.Length > 1) text.Append(displacement < 0 ? '-' : '+');
            text.Append("0x").Append(Math.Abs((long)displacement).ToString("X",
                System.Globalization.CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    /// <summary>
    /// Reads the first executable dwords of a static offset table so an indexed
    /// indirect call lists its candidate targets instead of staying "unresolved".
    /// </summary>
    private static IReadOnlyList<uint> ReadTableCandidates(EngineEffectBinaryImage image, uint table, int limit)
    {
        if (image.VirtualAddressToFileOffset(table) == null) return [];
        var result = new List<uint>();
        for (var index = 0; index < limit; index++)
        {
            var address = unchecked(table + (uint)(index * 4));
            if (ReadDword(image, address) is not { } target) break;
            if (image.SectionOf(target) is { IsExecutable: true }) result.Add(target);
        }
        return result;
    }

    private static uint? ReadDword(EngineEffectBinaryImage image, uint virtualAddress)
    {
        var offset = image.VirtualAddressToFileOffset(virtualAddress);
        if (offset is not { } position) return null;
        var blob = image.ReadBytes(position, 4);
        return blob == null ? null : BinaryPrimitives.ReadUInt32LittleEndian(blob);
    }

    private static string RegisterName(Register register) => register switch
    {
        Register.EAX => "eax",
        Register.EBX => "ebx",
        Register.ECX => "ecx",
        Register.EDX => "edx",
        Register.ESI => "esi",
        Register.EDI => "edi",
        Register.EBP => "ebp",
        Register.ESP => "esp",
        _ => register.ToString().ToLowerInvariant()
    };
}
