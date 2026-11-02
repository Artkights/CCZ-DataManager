using System.Buffers.Binary;
using Iced.Intel;
using IcedDecoder = Iced.Intel.Decoder;

namespace DataManager.Core.Scanner;

internal readonly record struct EngineEffectBranchCandidate(uint Address, uint Target, string Kind, int Length);

/// <summary>
/// Instruction boundaries recovered from provable code starts: the PE entry
/// point, every E8 target, and function prologues that do not fall inside an
/// already decoded instruction.  A byte strictly inside one of these spans
/// cannot start an instruction, so an opcode-looking immediate byte (for
/// example the second byte of an E8 rel32 call) must not create a jump edge.
/// </summary>
internal sealed class EngineEffectInstructionMap
{
    private const int AnchorSpanBytes = 0x800;
    private const int PaddingRunBytes = 8;
    private const int ClosurePasses = 8;
    private const int MaximumIndexedJumpEntries = 64;
    private const int MaximumIndexedJumpTargets = 1024;

    private readonly Dictionary<uint, int> _lengths;
    private readonly uint[] _starts;
    private readonly int _indexedJumpAnchorCount;

    private EngineEffectInstructionMap(Dictionary<uint, int> lengths, int indexedJumpAnchorCount)
    {
        _lengths = lengths;
        _starts = lengths.Keys.OrderBy(value => value).ToArray();
        _indexedJumpAnchorCount = indexedJumpAnchorCount;
    }

    internal int InstructionCount => _lengths.Count;
    internal int IndexedJumpAnchorCount => _indexedJumpAnchorCount;

    public static EngineEffectInstructionMap Build(EngineEffectBinaryImage image)
    {
        var callTargets = new HashSet<uint>(image.CallTargets);
        var prologues = new HashSet<uint>();
        foreach (var section in image.ExecutableSections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, image.Bytes.LongLength - start));
            if (length < 3) continue;
            var bytes = image.Bytes.AsSpan(start, length);
            for (var index = 0; index + 3 <= length; index++)
            {
                if ((index & 0xFFFF) == 0) image.Context.CheckCancellation();
                if (IsPrologue(bytes, index)) prologues.Add(section.VirtualAddress + (uint)index);
            }
        }
        if (IsExecutable(image, image.EntryPoint)) callTargets.Add(image.EntryPoint);
        var lengths = new Dictionary<uint, int>();
        DecodeAll(image, lengths, callTargets);
        var starts = lengths.Keys.OrderBy(value => value).ToArray();
        DecodeAll(image, lengths, prologues.Where(address => FindCovering(lengths, starts, address) == null));
        starts = lengths.Keys.OrderBy(value => value).ToArray();
        var indexed = EnumerateIndexedJumpTargets(image);
        DecodeAll(image, lengths, indexed.Where(address => FindCovering(lengths, starts, address) == null));
        CloseUnderBranches(image, lengths);
        return new EngineEffectInstructionMap(lengths, indexed.Count);
    }

    /// <summary>
    /// Accepted function prologues: push ebp; mov ebp,esp (55 8B EC / 55 89 E5),
    /// or the MSVC variant push ebp; mov reg,[esp+disp8]; mov ebp,esp
    /// (55 8B 4C 24 xx 8B EC) that reads a stack argument before the frame exists.
    /// </summary>
    internal static bool IsPrologue(ReadOnlySpan<byte> bytes, int index)
    {
        if (index + 3 > bytes.Length || bytes[index] != 0x55) return false;
        if ((bytes[index + 1] == 0x8B && bytes[index + 2] == 0xEC) ||
            (bytes[index + 1] == 0x89 && bytes[index + 2] == 0xE5)) return true;
        return index + 7 <= bytes.Length &&
               bytes[index + 1] == 0x8B && bytes[index + 2] == 0x4C && bytes[index + 3] == 0x24 &&
               bytes[index + 4] is >= 0x04 and <= 0x40 &&
               bytes[index + 5] == 0x8B && bytes[index + 6] == 0xEC;
    }

    /// <summary>
    /// A direct branch that starts at a recovered instruction boundary proves
    /// its target is also an instruction boundary. Closing the map this way
    /// reaches blocks that follow a ret and are entered only by an inbound
    /// jump; without them the immediate bytes of those blocks would keep
    /// inventing phantom jumps.
    /// </summary>
    private static void CloseUnderBranches(EngineEffectBinaryImage image, Dictionary<uint, int> lengths)
    {
        var candidates = EnumerateBranches(image);
        for (var pass = 0; pass < ClosurePasses; pass++)
        {
            var starts = lengths.Keys.OrderBy(value => value).ToArray();
            var pending = new HashSet<uint>();
            foreach (var candidate in candidates)
            {
                if (!lengths.ContainsKey(candidate.Address)) continue;
                if (!IsExecutable(image, candidate.Target)) continue;
                if (lengths.ContainsKey(candidate.Target)) continue;
                if (FindCovering(lengths, starts, candidate.Target) != null) continue;
                if (!IsBranchAt(image, candidate.Address, candidate.Target, candidate.Length)) continue;
                pending.Add(candidate.Target);
            }
            if (pending.Count == 0) return;
            var before = lengths.Count;
            DecodeAll(image, lengths, pending);
            if (lengths.Count == before) return;
        }
    }

    /// <summary>Raw byte candidates for direct branches, exactly as the jump index scans them.</summary>
    public static List<EngineEffectBranchCandidate> EnumerateBranches(EngineEffectBinaryImage image)
        => image.Context.GetOrBuild(EngineEffectAnalysisKind.BranchCandidates, 0, 0,
            () => EnumerateBranchesCore(image));

    private static List<EngineEffectBranchCandidate> EnumerateBranchesCore(EngineEffectBinaryImage image)
    {
        var result = new List<EngineEffectBranchCandidate>();
        foreach (var section in image.ExecutableSections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, image.Bytes.LongLength - start));
            if (length < 2) continue;
            var span = image.Bytes.AsSpan(start, length);
            for (var index = 0; index < length - 1; index++)
            {
                if ((index & 0xFFFF) == 0) image.Context.CheckCancellation();
                var opcode = span[index];
                uint? target = null;
                string? kind = null;
                var instructionLength = 0;
                if (opcode == 0xE9 && index + 5 <= length)
                {
                    var relative = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(index + 1, 4));
                    var address = section.VirtualAddress + (uint)index;
                    target = unchecked((uint)(address + 5 + relative));
                    kind = "jmp rel32";
                    instructionLength = 5;
                }
                else if (opcode == 0xEB)
                {
                    var address = section.VirtualAddress + (uint)index;
                    target = unchecked((uint)(address + 2 + (sbyte)span[index + 1]));
                    kind = "jmp rel8";
                    instructionLength = 2;
                }
                else if (opcode == 0x0F && index + 6 <= length && span[index + 1] is >= 0x80 and <= 0x8F)
                {
                    var relative = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(index + 2, 4));
                    var address = section.VirtualAddress + (uint)index;
                    target = unchecked((uint)(address + 6 + relative));
                    kind = "jcc rel32";
                    instructionLength = 6;
                }
                else if (opcode is >= 0x70 and <= 0x7F)
                {
                    var address = section.VirtualAddress + (uint)index;
                    target = unchecked((uint)(address + 2 + (sbyte)span[index + 1]));
                    kind = "jcc rel8";
                    instructionLength = 2;
                }
                if (target is not { } value || kind == null) continue;
                if (image.SectionOf(value) == null) continue;
                result.Add(new EngineEffectBranchCandidate(section.VirtualAddress + (uint)index, value, kind,
                    instructionLength));
            }
        }
        return result;
    }

    /// <summary>
    /// Confirms a raw candidate really decodes as a direct branch of the scanned
    /// length whose target matches. Immediate bytes that mimic an opcode fail
    /// here because the decode does not start at the candidate address.
    /// </summary>
    public static bool IsBranchAt(EngineEffectBinaryImage image, uint address, uint target, int length)
    {
        var decoded = EngineEffectDecoder.DecodeForward(image, address, length);
        if (decoded.Count == 0) return false;
        var item = decoded[0];
        if (item.Address != address || item.Length != length) return false;
        var instruction = item.Instruction;
        if (instruction.FlowControl is not (FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch))
            return false;
        if (instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
            return false;
        return unchecked((uint)instruction.NearBranchTarget) == target;
    }

    /// <summary>True when <paramref name="address"/> lies strictly inside a known instruction.</summary>
    public bool IsInterior(uint address)
        => FindCovering(_lengths, _starts, address) != null;

    private static (uint Start, int Length)? FindCovering(Dictionary<uint, int> lengths, uint[] starts,
        uint address)
    {
        var index = Array.BinarySearch(starts, address);
        if (index >= 0) return null;
        index = ~index - 1;
        for (var step = 0; step < 16 && index >= 0; step++, index--)
        {
            var start = starts[index];
            if (address - start > 15) break;
            var length = lengths[start];
            if (start + (uint)length > address) return (start, length);
        }
        return null;
    }

    /// <summary>
    /// Indexed jump tables (<c>FF 24 &lt;SIB&gt; disp32</c> = jmp [table+index*4]) prove
    /// their entries are code: the displacement must be a readable data address and
    /// the first entries must all point into executable sections. Entries become
    /// anchors only (never jump edges), so they cannot invent cross-block branches.
    /// </summary>
    internal static HashSet<uint> EnumerateIndexedJumpTargets(EngineEffectBinaryImage image)
    {
        var targets = new HashSet<uint>();
        foreach (var section in image.ExecutableSections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, image.Bytes.LongLength - start));
            if (length < 8) continue;
            var bytes = image.Bytes.AsSpan(start, length);
            for (var index = 0; index + 8 <= length; index++)
            {
                if ((index & 0xFFFF) == 0) image.Context.CheckCancellation();
                if (targets.Count >= MaximumIndexedJumpTargets) return targets;
                if (bytes[index] != 0xFF || (bytes[index + 1] & 0xC7) != 0x04) continue;
                var sib = bytes[index + 2];
                if (((sib >> 3) & 7) == 4) continue;      // index=esp is not an index register
                if ((sib & 7) != 5) continue;             // base must be disp32, not a runtime base
                var table = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index + 3, 4));
                if (image.VirtualAddressToFileOffset(table) == null) continue;
                var blob = image.ReadBytes(section.RawPointer + index, Math.Min(12, length - index));
                if (blob == null) continue;
                var decoder = IcedDecoder.Create(32, blob, section.VirtualAddress + (uint)index, DecoderOptions.None);
                var instruction = decoder.Decode();
                if (instruction.Length == 0 || instruction.Mnemonic != Mnemonic.Jmp ||
                    instruction.MemoryBase != Register.None || instruction.MemoryIndex == Register.None ||
                    instruction.MemoryIndexScale != 4 || instruction.MemoryDisplacement64 != table) continue;
                var entries = new List<uint>();
                for (var slot = 0; slot < MaximumIndexedJumpEntries; slot++)
                {
                    var entryAddress = unchecked(table + (uint)(slot * 4));
                    if (image.VirtualAddressToFileOffset(entryAddress) is not { } position) break;
                    var entry = image.ReadBytes(position, 4);
                    if (entry == null) break;
                    var target = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    if (image.SectionOf(target) is not { IsExecutable: true }) break;
                    entries.Add(target);
                }
                if (entries.Count < 2) continue;
                foreach (var entry in entries) targets.Add(entry);
            }
        }
        return targets;
    }

    private static void DecodeAll(EngineEffectBinaryImage image, Dictionary<uint, int> lengths,
        IEnumerable<uint> anchors)
    {
        foreach (var anchor in anchors)
        {
            image.Context.CheckCancellation();
            var section = image.SectionOf(anchor);
            if (section == null) continue;
            var offset = checked((int)(anchor - section.VirtualAddress));
            var length = Math.Min(AnchorSpanBytes, checked((int)(section.RawSize - offset)));
            if (length < 1) continue;
            var blob = image.ReadBytes(section.RawPointer + offset, length);
            if (blob == null) break;
            var decoder = IcedDecoder.Create(32, blob, anchor, DecoderOptions.None);
            while (decoder.IP < anchor + (uint)blob.Length)
            {
                if (StartsPadding(blob, checked((int)(decoder.IP - anchor)))) break;
                var instruction = decoder.Decode();
                if (instruction.Length == 0) break;
                lengths.TryAdd(unchecked((uint)instruction.IP), instruction.Length);
                if (instruction.FlowControl == FlowControl.Return) break;
            }
        }
    }

    private static bool StartsPadding(byte[] blob, int offset)
    {
        if (offset < 0 || offset >= blob.Length) return false;
        var limit = Math.Min(blob.Length, offset + PaddingRunBytes);
        for (var index = offset; index < limit; index++)
            if (blob[index] is not (0x90 or 0xCC)) return false;
        return limit - offset >= PaddingRunBytes;
    }

    private static bool IsExecutable(EngineEffectBinaryImage image, uint address)
        => image.SectionOf(address) is { IsExecutable: true };
}
