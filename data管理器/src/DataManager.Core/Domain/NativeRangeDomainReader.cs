using System.Buffers.Binary;
using System.Security.Cryptography;
using DataManager.Core.Scanner;

namespace DataManager.Core.Domain;

/// <summary>
/// Reads the small native range domains that business editors need from the
/// active Ekd5.exe. Geometry decoding and writes remain in RangeManager.App;
/// this reader only exposes verified IDs, version evidence and diagnostics.
/// </summary>
public sealed record NativeRangeDomainSnapshot(
    string ProfileId,
    string VariantId,
    string EngineVersion,
    string ExecutableHash,
    int PierceMaximum,
    IReadOnlyList<int> JobPierceValues,
    IReadOnlyList<int> LogicalAttackIds,
    IReadOnlyList<int> LogicalAttackEditableIds,
    IReadOnlyList<string> Diagnostics)
{
    public bool HasVerifiedEngineVersion => EngineVersion is "6.4" or "6.5" or "6.6";
}

public static class NativeRangeDomainReader
{
    public const uint JobPierceTableVa = 0x00407881;
    public const uint LogicalShapeTableVa = 0x004D1400;
    public const int JobCount = 80;
    public const int LogicalShapeCount = 0x26;

    public static NativeRangeDomainSnapshot Read(string projectRoot, string profileId = "", string variantId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var exePath = Path.Combine(Path.GetFullPath(projectRoot), "Ekd5.exe");
        if (!File.Exists(exePath))
            return Unavailable(profileId, variantId, $"找不到 Ekd5.exe：{exePath}");

        byte[] bytes;
        try { bytes = File.ReadAllBytes(exePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable(profileId, variantId, $"读取 Ekd5.exe 失败：{ex.Message}");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            var image = EngineEffectBinaryImage.Read(bytes);
            var diagnostics = new List<string>();
            var version = DetectVersion(image, profileId, diagnostics);
            var maximum = version switch
            {
                "6.4" => 0x09,
                "6.5" => 0x0B,
                "6.6" => 0x0A,
                _ => -1
            };

            var jobValues = ReadJobPierceValues(image, diagnostics);
            var logical = ReadLogicalShapeSlots(image, diagnostics);
            if (maximum >= 0)
                diagnostics.Add($"原生穿透契约：{version} 0..0x{maximum:X2}");
            else
                diagnostics.Add("未确认 Ekd5 版本；不生成未经验证的原生穿透范围域");

            return new NativeRangeDomainSnapshot(profileId, variantId, version, hash, maximum,
                jobValues, logical, logical.Where(id => id is not (0x0A or 0x0B)).ToArray(), diagnostics);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return Unavailable(profileId, variantId, $"Ekd5 PE/原生范围表读取失败：{ex.Message}", hash);
        }
    }

    private static NativeRangeDomainSnapshot Unavailable(string profileId, string variantId, string reason,
        string hash = "")
        => new(profileId, variantId, "unknown", hash, -1, [],
            Enumerable.Range(0, LogicalShapeCount).ToArray(),
            NativeRangeDomain.LogicalAttackEditableIds,
            [reason]);

    private static string DetectVersion(EngineEffectBinaryImage image, string profileId, List<string> diagnostics)
    {
        // The version dispatch used by RangeManager is anchored at the native
        // 0x407881 reader. Keep the byte evidence here so callers can explain
        // why a custom executable did or did not receive a generated domain.
        if (Matches(image, 0x0040786D, [0x8A, 0x80, 0x81, 0x78, 0x40, 0x00]))
        {
            diagnostics.Add("版本证据：0x40786D 兵种穿透读取指令（6.6）");
            return "6.6";
        }

        if (Matches(image, 0x00407877, [0x8A, 0x80, 0x81, 0x78, 0x40, 0x00]))
        {
            var version = ReadByte(image, 0x00435D02) == 0x09 ? "6.4" : "6.5";
            diagnostics.Add($"版本证据：0x407877 兵种穿透读取指令（{version}）");
            return version;
        }

        if (profileId is "ccz-6.4" or "ccz-6.5" or "ccz-6.6")
            diagnostics.Add($"机器码未匹配，但工作区 profile 为 {profileId}；仅使用 profile 契约，不宣称 EXE 已验证");
        return "unknown";
    }

    private static IReadOnlyList<int> ReadJobPierceValues(EngineEffectBinaryImage image, List<string> diagnostics)
    {
        if (!TryOffset(image, JobPierceTableVa, JobCount, out var offset))
        {
            diagnostics.Add($"兵种穿透表 0x{JobPierceTableVa:X8} 不在 Ekd5 文件映射内");
            return [];
        }

        var values = new SortedSet<int>();
        for (var index = 0; index < JobCount; index++) values.Add(image.Bytes[offset + index]);
        diagnostics.Add($"兵种穿透表已读取：{JobCount} 行，实际编号 {string.Join(",", values)}");
        return values.ToArray();
    }

    private static IReadOnlyList<int> ReadLogicalShapeSlots(EngineEffectBinaryImage image, List<string> diagnostics)
    {
        if (!TryOffset(image, LogicalShapeTableVa, LogicalShapeCount * sizeof(uint), out var offset))
        {
            diagnostics.Add($"逻辑攻击形状表 0x{LogicalShapeTableVa:X8} 不在 Ekd5 文件映射内");
            return Enumerable.Range(0, LogicalShapeCount).ToArray();
        }

        var validCount = 0;
        for (var id = 0; id < LogicalShapeCount; id++)
        {
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(image.Bytes.AsSpan(offset + id * 4, 4));
            // 0x0A/0x0B are logical special/skip entries and intentionally do
            // not require a dereferenceable finite-shape pointer.
            if (id is 0x0A or 0x0B || pointer != 0) validCount++;
        }
        diagnostics.Add($"逻辑攻击形状槽已读取：{validCount}/{LogicalShapeCount} 个有指针或特殊编号；0x0A/0x0B 保留特殊语义");
        // The logical domain is the contract itself. A null pointer is useful
        // diagnostic evidence, but it must not make an ID disappear from the
        // range manager's 36-slot view.
        return Enumerable.Range(0, LogicalShapeCount).ToArray();
    }

    private static bool Matches(EngineEffectBinaryImage image, uint address, IReadOnlyList<byte> expected)
    {
        if (!TryOffset(image, address, expected.Count, out var offset)) return false;
        return image.Bytes.AsSpan(offset, expected.Count).SequenceEqual(expected.ToArray());
    }

    private static byte ReadByte(EngineEffectBinaryImage image, uint address)
        => TryOffset(image, address, 1, out var offset) ? image.Bytes[offset] : (byte)0;

    private static bool TryOffset(EngineEffectBinaryImage image, uint address, int length, out int offset)
    {
        var mapped = image.VirtualAddressToFileOffset(address);
        if (mapped is not { } value || value < 0 || value > int.MaxValue || length < 0 || value + length > image.Bytes.LongLength)
        {
            offset = 0;
            return false;
        }
        offset = (int)value;
        return true;
    }
}
