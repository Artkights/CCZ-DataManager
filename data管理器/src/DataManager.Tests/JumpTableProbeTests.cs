using System.Buffers.Binary;
using System.Reflection;
using DataManager.Core.Scanner;
using Iced.Intel;
using IcedDecoder = Iced.Intel.Decoder;

namespace DataManager.Tests;

/// <summary>Diagnoses the indexed jump-table anchor enumeration stage by stage (test-only probe).</summary>
internal static class JumpTableProbeTests
{
    public static void Run(string root)
    {
        var exe = Path.Combine(root, "Ekd5.exe");
        var assembly = typeof(EngineEffectScanService).Assembly;
        var imageType = assembly.GetType("DataManager.Core.Scanner.EngineEffectBinaryImage", throwOnError: true)!;
        var mapType = assembly.GetType("DataManager.Core.Scanner.EngineEffectInstructionMap", throwOnError: true)!;
        var image = imageType.GetMethod("Read")!
            .Invoke(null, [File.ReadAllBytes(exe), CancellationToken.None])!;
        var method = mapType.GetMethod("EnumerateIndexedJumpTargets",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var targets = new List<uint>();
        foreach (var item in (System.Collections.IEnumerable)method.Invoke(null, [image])!) targets.Add((uint)item);
        Console.WriteLine($"JUMP_TABLE_PROBE {Path.GetFileName(root)} targets={targets.Count} " +
                          $"distinct={targets.Distinct().Count()}");

        // Stage mirror with real Iced to find the failing filter.
        var bytes = (byte[])imageType.GetProperty("Bytes")!.GetValue(image)!;
        var sections = ((System.Collections.IEnumerable)imageType.GetProperty("ExecutableSections")!.GetValue(image)!)
            .Cast<object>().ToArray();
        int sites = 0, sibOk = 0, tableOk = 0, decoded = 0, entriesOk = 0;
        string? firstDecode = null;
        uint? firstTable = null;
        foreach (var section in sections)
        {
            var type = section.GetType();
            var rawPointer = (uint)type.GetProperty("RawPointer")!.GetValue(section)!;
            var rawSize = (uint)type.GetProperty("RawSize")!.GetValue(section)!;
            var virtualAddress = (uint)type.GetProperty("VirtualAddress")!.GetValue(section)!;
            var length = (int)Math.Min(rawSize, bytes.LongLength - rawPointer);
            for (var index = 0; index + 8 <= length; index++)
            {
                var offset = (int)rawPointer + index;
                if (bytes[offset] != 0xFF || (bytes[offset + 1] & 0xC7) != 0x04) continue;
                sites++;
                var sib = bytes[offset + 2];
                if (((sib >> 3) & 7) == 4 || (sib & 7) != 5) continue;
                sibOk++;
                var table = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 3, 4));
                if (firstTable == null) firstTable = table;
                var mapped = imageType.GetMethod("VirtualAddressToFileOffset")!.Invoke(image, [table]);
                if (mapped == null) continue;
                tableOk++;
                var blob = bytes.AsSpan(offset, Math.Min(12, length - index)).ToArray();
                var decoder = IcedDecoder.Create(32, blob, virtualAddress + (uint)index, DecoderOptions.None);
                var instruction = decoder.Decode();
                if (instruction.Length == 0 || instruction.Mnemonic != Mnemonic.Jmp ||
                    instruction.MemoryBase != Register.None || instruction.MemoryIndex == Register.None ||
                    instruction.MemoryIndexScale != 4 || instruction.MemoryDisplacement64 != table)
                {
                    if (firstDecode == null)
                        firstDecode = $"{instruction.Mnemonic} len={instruction.Length} base={instruction.MemoryBase} " +
                                      $"index={instruction.MemoryIndex} scale={instruction.MemoryIndexScale} " +
                                      $"disp=0x{instruction.MemoryDisplacement64:X} table=0x{table:X8}";
                    continue;
                }
                decoded++;
                var count = 0;
                for (var slot = 0; slot < 64; slot++)
                {
                    var entryAddress = unchecked(table + (uint)(slot * 4));
                    var entryOffset = imageType.GetMethod("VirtualAddressToFileOffset")!.Invoke(image, [entryAddress]);
                    if (entryOffset == null) break;
                    var read = imageType.GetMethod("ReadBytes")!.Invoke(image, [(long)entryOffset, 4]) as byte[];
                    if (read == null) break;
                    var target = BinaryPrimitives.ReadUInt32LittleEndian(read);
                    var sectionOf = imageType.GetMethod("SectionOf")!.Invoke(image, [target]);
                    if (sectionOf == null ||
                        (bool)sectionOf.GetType().GetProperty("IsExecutable")!.GetValue(sectionOf)! == false) break;
                    count++;
                }
                if (count >= 2) entriesOk++;
            }
        }
        Console.WriteLine($"JUMP_TABLE_STAGES {Path.GetFileName(root)} sites={sites} sib={sibOk} table={tableOk} " +
                          $"decoded={decoded} entries={entriesOk} firstTable=0x{firstTable:X8}");
        if (firstDecode != null) Console.WriteLine("  first-reject: " + firstDecode);
    }
}
