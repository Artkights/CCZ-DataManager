using System.Buffers.Binary;
using Iced.Intel;
using IcedDecoder = Iced.Intel.Decoder;

namespace DataManager.Core.Scanner;

internal sealed record EngineEffectSection(string Name, uint VirtualAddress, uint VirtualSize,
    uint RawPointer, uint RawSize, bool IsExecutable);

/// <summary>Shared PE32 image used by the scan, argument and write-plan analyzers.</summary>
internal sealed class EngineEffectBinaryImage
{
    private readonly byte[] _bytes;
    private readonly IReadOnlyList<EngineEffectSection> _sections;
    private readonly uint _entryPoint;
    private readonly uint _imageBase;
    private readonly IReadOnlySet<uint> _exportedFunctions;
    private readonly IReadOnlyList<EngineEffectSection> _executableSections;
    private HashSet<uint>? _callTargets;

    private EngineEffectBinaryImage(byte[] bytes, IReadOnlyList<EngineEffectSection> sections, uint entryPoint,
        uint imageBase, IReadOnlySet<uint> exportedFunctions, CancellationToken cancellationToken)
    {
        _bytes = bytes;
        _sections = sections;
        _entryPoint = entryPoint;
        _imageBase = imageBase;
        _exportedFunctions = exportedFunctions;
        _executableSections = sections.Where(section => section.IsExecutable).ToArray();
        Context = new EngineEffectScanContext(this, cancellationToken);
    }

    public byte[] Bytes => _bytes;
    public IReadOnlyList<EngineEffectSection> Sections => _sections;
    public IReadOnlyList<EngineEffectSection> ExecutableSections => _executableSections;
    internal EngineEffectScanContext Context { get; }
    /// <summary>Absolute VA of OptionalHeader.AddressOfEntryPoint (image base already applied).</summary>
    public uint EntryPoint => _entryPoint;
    public uint ImageBase => _imageBase;
    public IReadOnlySet<uint> ExportedFunctions => _exportedFunctions;

    /// <summary>
    /// Targets of every E8 rel32 call in the executable sections. A prologue-less
    /// function (for example one that begins with <c>push ebp; mov ecx,[esp+0xc]</c>)
    /// is still a function start when something calls it, so entry attribution
    /// must know these addresses.
    /// </summary>
    public IReadOnlySet<uint> CallTargets => _callTargets ??= ReadCallTargets();

    private HashSet<uint> ReadCallTargets()
    {
        var targets = new HashSet<uint>();
        foreach (var call in Context.RawCalls)
            if (SectionOf(call.Target) is { IsExecutable: true }) targets.Add(call.Target);
        return targets;
    }

    public static EngineEffectBinaryImage Read(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        // The cache owns its snapshot. A caller retaining its input buffer must not be
        // able to change instructions after their decode/CFG results have been cached.
        bytes = (byte[])bytes.Clone();
        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidDataException("Ekd5.exe 缺少有效的 MZ 文件头。");
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
        if (peOffset < 0 || peOffset > bytes.Length - 24 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset, 4)) != 0x00004550)
            throw new InvalidDataException("Ekd5.exe 缺少有效的 PE 文件头。");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 4, 2)) != 0x014C ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 24, 2)) != 0x010B)
            throw new InvalidDataException("Ekd5.exe 不是 PE32/x86 可执行文件。");
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 20, 2));
        var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset + 24 + 28, 4));
        var entryRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset + 24 + 16, 4));
        var table = peOffset + 24 + optionalSize;
        if (table + sectionCount * 40 > bytes.Length)
            throw new InvalidDataException("Ekd5.exe 的节表超出文件范围。");
        var sections = new List<EngineEffectSection>(sectionCount);
        for (var index = 0; index < sectionCount; index++)
        {
            var header = table + index * 40;
            var name = System.Text.Encoding.ASCII.GetString(bytes, header, 8).TrimEnd('\0');
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 8, 4));
            var virtualAddress = imageBase + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 12, 4));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 16, 4));
            var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 20, 4));
            var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 36, 4));
            sections.Add(new EngineEffectSection(name, virtualAddress, Math.Max(virtualSize, rawSize),
                rawPointer, rawSize, (characteristics & 0x20000000) != 0));
        }
        return new EngineEffectBinaryImage(bytes, sections, entryRva == 0 ? 0 : imageBase + entryRva,
            imageBase, ReadExports(bytes, peOffset, optionalSize, imageBase, sections), cancellationToken);
    }

    private static IReadOnlySet<uint> ReadExports(byte[] bytes, int peOffset, ushort optionalSize,
        uint imageBase, IReadOnlyList<EngineEffectSection> sections)
    {
        var result = new HashSet<uint>();
        const int dataDirectoryOffset = 96;
        if (optionalSize < dataDirectoryOffset + 8) return result;
        var directory = peOffset + 24 + dataDirectoryOffset;
        if (directory + 8 > bytes.Length) return result;
        var exportRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directory, 4));
        if (exportRva == 0) return result;
        var exportOffset = VirtualAddressToFileOffset(sections, imageBase + exportRva);
        if (exportOffset is not { } offset || offset + 40 > bytes.Length) return result;
        var index = checked((int)offset);
        var functionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 20, 4));
        var functionTableRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 28, 4));
        if (functionCount == 0 || functionCount > 0x10000) return result;
        var tableOffset = VirtualAddressToFileOffset(sections, imageBase + functionTableRva);
        if (tableOffset is not { } table) return result;
        for (var item = 0u; item < functionCount; item++)
        {
            var entryOffset = table + item * 4;
            if (entryOffset + 4 > bytes.LongLength) break;
            var rva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int)entryOffset), 4));
            if (rva == 0) continue;
            var address = imageBase + rva;
            var section = sections.FirstOrDefault(candidate =>
                address >= candidate.VirtualAddress &&
                address < candidate.VirtualAddress + candidate.VirtualSize &&
                address - candidate.VirtualAddress < candidate.RawSize);
            if (section is { IsExecutable: true }) result.Add(address);
        }
        return result;
    }

    private static long? VirtualAddressToFileOffset(IReadOnlyList<EngineEffectSection> sections, uint virtualAddress)
    {
        foreach (var section in sections)
        {
            if (virtualAddress < section.VirtualAddress ||
                virtualAddress >= section.VirtualAddress + section.VirtualSize) continue;
            var offset = section.RawPointer + (virtualAddress - section.VirtualAddress);
            return offset;
        }
        return null;
    }

    public EngineEffectSection? SectionOf(uint virtualAddress)
        => _sections.FirstOrDefault(section =>
            virtualAddress >= section.VirtualAddress &&
            virtualAddress < section.VirtualAddress + section.VirtualSize &&
            virtualAddress - section.VirtualAddress < section.RawSize);

    public long? VirtualAddressToFileOffset(uint virtualAddress)
    {
        foreach (var section in _sections)
        {
            if (virtualAddress < section.VirtualAddress ||
                virtualAddress >= section.VirtualAddress + section.VirtualSize) continue;
            var offset = section.RawPointer + (virtualAddress - section.VirtualAddress);
            return offset < _bytes.LongLength ? offset : null;
        }
        return null;
    }

    public uint? FileOffsetToVirtualAddress(long fileOffset)
    {
        foreach (var section in _sections)
        {
            if (fileOffset < section.RawPointer || fileOffset >= section.RawPointer + section.RawSize) continue;
            return section.VirtualAddress + (uint)(fileOffset - section.RawPointer);
        }
        return null;
    }

    public byte[]? ReadBytes(long fileOffset, int length)
    {
        if (length < 0 || fileOffset < 0 || fileOffset + length > _bytes.LongLength) return null;
        return _bytes.AsSpan(checked((int)fileOffset), length).ToArray();
    }
}

internal sealed class DecodedInstruction
{
    private byte[]? _rawBytes;
    private string? _text;
    public Instruction Instruction { get; init; }
    public uint Address { get; init; }
    public long FileOffset { get; init; }
    public int Length { get; init; }
    internal ReadOnlyMemory<byte> SourceBytes { get; init; }
    public byte[] RawBytes { get => _rawBytes ??= SourceBytes.ToArray(); init => _rawBytes = value; }
    public int? ImmediateOffset { get; init; }
    public int? ImmediateSize { get; init; }
    public string Text { get => _text ??= EngineEffectDecoder.Format(Instruction); init => _text = value; }
    public bool IsPush => Instruction.Mnemonic == Mnemonic.Push;
    public bool IsControlFlow => Instruction.FlowControl != FlowControl.Next;
    public byte OpCode => SourceBytes.Length > 0 ? SourceBytes.Span[0] : RawBytes.Length > 0 ? RawBytes[0] : (byte)0;
}

/// <summary>Aligned linear decoding shared by argument, stub and evidence analysis.</summary>
internal static class EngineEffectDecoder
{
    [ThreadStatic] private static Formatter? _formatter;
    public static IReadOnlyList<DecodedInstruction>? DecodeBacktrack(EngineEffectBinaryImage image,
        uint callAddress, int maximumLookbackBytes = 96)
    {
        image.Context.CheckCancellation();
        var key = (callAddress, maximumLookbackBytes);
        if (image.Context.Backtrack.TryGetValue(key, out var cached)) return cached;
        var result = DecodeBacktrackCore(image, callAddress, maximumLookbackBytes);
        image.Context.Backtrack.Add(key, result);
        return result;
    }

    private static IReadOnlyList<DecodedInstruction>? DecodeBacktrackCore(EngineEffectBinaryImage image,
        uint callAddress, int maximumLookbackBytes)
    {
        var section = image.SectionOf(callAddress);
        if (section == null) return null;
        var callOffset = checked((int)(callAddress - section.VirtualAddress));
        var maximum = Math.Min(maximumLookbackBytes, callOffset);
        IReadOnlyList<DecodedInstruction>? best = null;
        var bestScore = (Prologue: 0, Pushes: 0, Window: int.MinValue);
        for (var window = maximum; window >= 2; window--)
        {
            if ((window & 15) == 0) image.Context.CheckCancellation();
            var start = callOffset - window;
            var blob = image.ReadBytes(section.RawPointer + start, window);
            if (blob == null) continue;
            var startAddress = callAddress - (uint)window;
            var decoded = DecodeExact(blob, startAddress, callAddress, section.RawPointer + start);
            if (decoded == null) continue;
            var prologue = blob.Length >= 3 && ((blob[0] == 0x55 && blob[1] == 0x8B && blob[2] == 0xEC) ||
                                                  (blob[0] == 0x55 && blob[1] == 0x89 && blob[2] == 0xE5));
            var pushCount = decoded.Count(item => item.IsPush);
            var score = (Prologue: prologue ? 1 : 0, Pushes: pushCount, Window: -window);
            if (score.CompareTo(bestScore) <= 0) continue;
            bestScore = score;
            best = decoded;
        }
        return best;
    }

    /// <summary>Instructions of the basic block that contains <paramref name="endAddress"/>,
    /// starting after the last preceding control-flow instruction.  This avoids
    /// misaligned windows that happen to end on the same address.</summary>
    public static IReadOnlyList<DecodedInstruction>? DecodeBasicBlock(EngineEffectBinaryImage image,
        uint endAddress, int maximumFunctionBytes = 0x400)
        => DecodeBasicBlockContext(image, endAddress, maximumFunctionBytes)?.Block;

    /// <summary>
    /// Basic block ending at <paramref name="endAddress"/> plus every instruction
    /// decoded before its head. The prefix keeps the guard (<c>cmp</c>/<c>jcc</c>)
    /// that bounds a stack parameter, which lives just before the block head and
    /// would otherwise be dropped when the block is trimmed at the branch.
    /// </summary>
    public static (IReadOnlyList<DecodedInstruction> Block, IReadOnlyList<DecodedInstruction> Prefix)?
        DecodeBasicBlockContext(EngineEffectBinaryImage image, uint endAddress, int maximumFunctionBytes = 0x400)
    {
        var entry = EngineEffectFunctionAnalyzer.FindFunctionEntry(image, endAddress);
        if (entry is not { } functionEntry || functionEntry >= endAddress) return null;
        var length = Math.Min(maximumFunctionBytes, checked((int)(endAddress - functionEntry) + 1));
        var forward = DecodeForward(image, functionEntry, length);
        if (forward.Count == 0) return null;
        var last = forward[^1];
        if (last.Address + (uint)last.Length < endAddress) return null;
        var before = forward.Where(item => item.Address < endAddress).ToArray();
        if (before.Length == 0) return null;
        var blockStart = 0;
        for (var index = 0; index < before.Length; index++)
            if (before[index].IsControlFlow) blockStart = index + 1;
        if (blockStart >= before.Length) return null;
        return (before[blockStart..], before[..blockStart]);
    }

    /// <summary>Follows an unconditional jmp that targets the call site (code-cave stubs).</summary>
    public static IReadOnlyList<DecodedInstruction>? DecodeJumpSource(EngineEffectBinaryImage image,
        uint callAddress, out uint? jumpAddress, int maximumDistance = 0x400)
    {
        jumpAddress = null;
        var section = image.SectionOf(callAddress);
        if (section == null) return null;
        var sectionStart = section.VirtualAddress;
        var sectionEnd = section.VirtualAddress + Math.Min(section.VirtualSize, section.RawSize);
        var start = callAddress > (uint)maximumDistance ? callAddress - (uint)maximumDistance : sectionStart;
        var end = Math.Min(sectionEnd, callAddress + (uint)maximumDistance);
        IReadOnlyList<DecodedInstruction>? best = null;
        var bestDistance = int.MaxValue;
        for (var address = Math.Max(start, sectionStart); address + 2 <= end; address++)
        {
            var fileOffset = image.VirtualAddressToFileOffset(address);
            if (fileOffset is not { } offset) continue;
            var head = image.ReadBytes(offset, 5);
            if (head == null || head.Length < 2) continue;
            uint target;
            if (head[0] == 0xEB)
                target = unchecked((uint)(address + 2 + (sbyte)head[1]));
            else if (head[0] == 0xE9 && head.Length >= 5)
                target = unchecked((uint)(address + 5 + BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(1))));
            else continue;
            if (target != callAddress) continue;
            var distance = checked((int)Math.Abs((long)address - callAddress));
            if (distance >= bestDistance) continue;
            var decoded = DecodeBacktrack(image, address, 96);
            if (decoded == null) continue;
            var last = decoded.Where(item => item.IsPush).TakeLast(4).ToArray();
            if (last.Length < 4 || !last.All(IsArgumentPush)) continue;
            best = decoded;
            bestDistance = distance;
            jumpAddress = address;
        }
        return best;
    }

    public static bool IsArgumentPush(DecodedInstruction item)
    {
        var instruction = item.Instruction;
        if (instruction.OpCount == 0) return false;
        if (ImmediateOf(instruction) != null) return true;
        return instruction.GetOpKind(0) == OpKind.Memory &&
               instruction.MemoryBase is Register.EBP or Register.RBP;
    }

    public static IReadOnlyList<DecodedInstruction> DecodeForward(EngineEffectBinaryImage image,
        uint startAddress, int maximumBytes, bool stopAtCall = false)
    {
        image.Context.CheckCancellation();
        var key = (startAddress, maximumBytes, stopAtCall);
        if (image.Context.Forward.TryGetValue(key, out var cached)) return cached;
        var result = DecodeForwardCore(image, startAddress, maximumBytes, stopAtCall);
        image.Context.Forward.Add(key, result);
        return result;
    }

    /// <summary>Exactly the first instruction of DecodeForward(address,15), without decoding its tail.</summary>
    public static DecodedInstruction? DecodeOne(EngineEffectBinaryImage image, uint address)
    {
        image.Context.CheckCancellation();
        if (image.Context.Instructions.TryGetValue(address, out var cached)) return cached;
        var section = image.SectionOf(address);
        if (section == null) { image.Context.Instructions.Add(address, null); return null; }
        var offset = checked((int)(address - section.VirtualAddress));
        var length = Math.Min(15, checked((int)(section.RawSize - offset)));
        var blob = image.ReadBytes(section.RawPointer + offset, length);
        if (blob == null || blob.Length == 0) { image.Context.Instructions.Add(address, null); return null; }
        var decoder = IcedDecoder.Create(32, blob, address, DecoderOptions.None);
        var instruction = decoder.Decode();
        var result = instruction.Length == 0 ? null :
            Create(decoder, instruction, blob, section.RawPointer + offset, address);
        image.Context.Instructions.Add(address, result);
        return result;
    }

    private static IReadOnlyList<DecodedInstruction> DecodeForwardCore(EngineEffectBinaryImage image,
        uint startAddress, int maximumBytes, bool stopAtCall)
    {
        var section = image.SectionOf(startAddress);
        if (section == null || maximumBytes <= 0) return [];
        var startOffset = checked((int)(startAddress - section.VirtualAddress));
        var length = Math.Min(maximumBytes, checked((int)(section.RawSize - startOffset)));
        var blob = image.ReadBytes(section.RawPointer + startOffset, length);
        if (blob == null) return [];
        var decoder = IcedDecoder.Create(32, blob, startAddress, DecoderOptions.None);
        var result = new List<DecodedInstruction>();
        while (decoder.IP < startAddress + (uint)blob.Length)
        {
            if ((result.Count & 0xFF) == 0) image.Context.CheckCancellation();
            var instruction = decoder.Decode();
            if (instruction.Length == 0) break;
            var decoded = Create(decoder, instruction, blob, section.RawPointer + startOffset, startAddress);
            result.Add(decoded);
            if (stopAtCall && instruction.FlowControl != FlowControl.Next) break;
        }
        return result;
    }

    private static IReadOnlyList<DecodedInstruction>? DecodeExact(byte[] blob, uint startAddress, uint endAddress,
        long blobFileOffset)
    {
        var decoder = IcedDecoder.Create(32, blob, startAddress, DecoderOptions.None);
        var result = new List<DecodedInstruction>();
        while (decoder.IP < endAddress)
        {
            var instruction = decoder.Decode();
            if (instruction.Length == 0 || decoder.IP > endAddress) return null;
            if (instruction.FlowControl != FlowControl.Next && instruction.Mnemonic != Mnemonic.Push) return null;
            result.Add(Create(decoder, instruction, blob, blobFileOffset, startAddress));
        }
        return decoder.IP == endAddress && result.Count > 0 ? result : null;
    }

    private static DecodedInstruction Create(IcedDecoder decoder,
        Instruction instruction, byte[] blob, long blobFileOffset, uint startAddress)
    {
        var offsetInBlob = checked((int)(instruction.IP - startAddress));
        var raw = offsetInBlob >= 0 && offsetInBlob + instruction.Length <= blob.Length
            ? blob.AsMemory(offsetInBlob, instruction.Length)
            : ReadOnlyMemory<byte>.Empty;
        ConstantOffsets constants = default;
        try { constants = decoder.GetConstantOffsets(instruction); }
        catch (Exception) { }
        return new DecodedInstruction
        {
            Instruction = instruction,
            Address = unchecked((uint)instruction.IP),
            FileOffset = blobFileOffset + offsetInBlob,
            Length = instruction.Length,
            SourceBytes = raw,
            ImmediateOffset = constants.HasImmediate ? constants.ImmediateOffset : null,
            ImmediateSize = constants.HasImmediate ? constants.ImmediateSize : null
        };
    }

    internal static string Format(Instruction instruction)
    {
        _formatter ??= new IntelFormatter();
        var output = new StringOutput();
        _formatter.Format(instruction, output);
        return output.ToString();
    }

    public static uint? ImmediateOf(Instruction instruction)
    {
        if (instruction.OpCount == 0) return null;
        return instruction.GetOpKind(0) switch
        {
            OpKind.Immediate8 or OpKind.Immediate8_2nd => unchecked((uint)instruction.GetImmediate(0)),
            OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate8to16 or OpKind.Immediate8to32 =>
                unchecked((uint)instruction.GetImmediate(0)),
            OpKind.Immediate64 or OpKind.Immediate8to64 or OpKind.Immediate32to64 =>
                unchecked((uint)instruction.GetImmediate(0)),
            _ => null
        };
    }

    public static string SourceOf(OpKind kind) => kind switch
    {
        OpKind.Immediate8 or OpKind.Immediate8_2nd => "imm8",
        OpKind.Immediate16 => "imm16",
        OpKind.Immediate32 => "imm32",
        OpKind.Immediate64 => "imm64",
        OpKind.Memory => "mem",
        _ => "reg"
    };

    public static string Hex(ReadOnlySpan<byte> bytes)
        => string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2",
            System.Globalization.CultureInfo.InvariantCulture)));
}
