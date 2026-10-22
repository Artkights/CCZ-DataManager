using System.Buffers.Binary;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

public sealed record AccessoryJobGroup(int Index, IReadOnlyList<int> JobSeriesIds)
{
    public string Display => $"第 {Index + 1} 组：{string.Join(", ", JobSeriesIds)}";
}

public sealed record AccessoryJobGroupReadResult(
    string FilePath,
    long FileOffset,
    int WritableLength,
    IReadOnlyList<AccessoryJobGroup> Groups,
    IReadOnlyList<string> Diagnostics)
{
    public string SourceHash { get; init; } = string.Empty;
}

public sealed record AccessoryJobGroupPreview(
    string FilePath,
    long FileOffset,
    int WritableLength,
    IReadOnlyList<AccessoryJobGroup> Groups,
    byte[] PayloadBytes,
    byte[] PaddedBytes,
    bool CanWrite,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Dedicated codec for the 6.5 accessory job-series groups.  The data lives
/// behind a PE virtual address and is encoded as FF + IDs + FF with a 0x90
/// safety area, so it cannot be represented safely by a normal table row.
/// </summary>
public sealed class AccessoryJobGroupService
{
    public const uint StartVirtualAddress = 0x0044C341;
    public const int MaxJobSeriesId = 39;
    public const int MaxProbeLength = 0x90;
    public const byte Separator = 0xFF;
    public const byte Padding = 0x90;

    public AccessoryJobGroupReadResult Read(string projectRoot)
    {
        var path = ProjectFileLocator.Resolve(projectRoot, "Ekd5.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 Ekd5.exe。", path);
        var bytes = File.ReadAllBytes(path);
        return ReadBytes(path, bytes);
    }

    private static AccessoryJobGroupReadResult ReadBytes(string path, byte[] bytes)
    {
        var offset = MapVirtualAddress(bytes, StartVirtualAddress);
        var diagnostics = new List<string>();
        var writable = DetectWritableLength(bytes, offset, diagnostics, out var payloadLength);
        var groups = Parse(bytes.AsSpan(checked((int)offset), payloadLength), diagnostics);
        return new AccessoryJobGroupReadResult(path, offset, writable, groups, diagnostics) { SourceHash = HashService.Sha256(bytes) };
    }

    public AccessoryJobGroupReadResult Read(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var baseline = GetBaseline(workspace);
        return ReadBytes(baseline.Path, baseline.Bytes);
    }

    private static (string Path, byte[] Bytes) GetBaseline(ProjectWorkspace workspace)
    {
        var path = ProjectFileLocator.Resolve(workspace.RootPath, "Ekd5.exe");
        var documents = workspace.Documents.Values.Where(document => Path.GetFullPath(document.FilePath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (documents.Length == 0 || documents.Select(document => document.OriginalFileHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new InvalidOperationException("EXE 工作区基线不可用，请重新打开项目。");
        return (path, documents[0].OriginalFileBytes);
    }

    public PreparedBinaryEdit PrepareSave(ProjectWorkspace workspace, IReadOnlyList<IReadOnlyList<int>> groupIds, string? expectedFileHash = null)
    {
        var baseline = GetBaseline(workspace);
        var current = ReadBytes(baseline.Path, baseline.Bytes);
        if (expectedFileHash != null && !current.SourceHash.Equals(expectedFileHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("辅助分组绑定的 EXE 基线已变化，请重新载入分组。");
        if (current.Diagnostics.Any(item => item.StartsWith("错误：", StringComparison.Ordinal)))
            throw new InvalidDataException(string.Join(Environment.NewLine, current.Diagnostics));
        var payload = Serialize(groupIds);
        if (payload.Length >= current.WritableLength)
            throw new InvalidDataException($"分组需要 {payload.Length} 字节，安全区 {current.WritableLength} 字节；须保留至少 1 字节 0x90 供安全边界复读。");
        var padded = Enumerable.Repeat(Padding, current.WritableLength).ToArray();
        payload.CopyTo(padded, 0);
        var offset = checked((int)current.FileOffset);
        var expectedGroups = Parse(payload);
        return new PreparedBinaryEdit(Path.GetRelativePath(workspace.RootPath, baseline.Path), current.SourceHash,
            [new PreparedBytePatch(offset, baseline.Bytes.AsSpan(offset, padded.Length).ToArray(), padded)],
            "兵种系", "修改辅助装备兵种系分组", bytes =>
            {
                if (!bytes.AsSpan(offset, padded.Length).SequenceEqual(padded) || !GroupsEqual(Parse(bytes.AsSpan(offset, payload.Length)), expectedGroups))
                    throw new InvalidDataException("辅助分组候选或写后复读不一致。");
            });
    }

    public AccessoryJobGroupPreview Preview(string projectRoot, IReadOnlyList<IReadOnlyList<int>> groupIds)
        => Preview(Read(projectRoot), groupIds);

    public AccessoryJobGroupPreview Preview(ProjectWorkspace workspace, IReadOnlyList<IReadOnlyList<int>> groupIds)
        => Preview(Read(workspace), groupIds);

    private static AccessoryJobGroupPreview Preview(AccessoryJobGroupReadResult current, IReadOnlyList<IReadOnlyList<int>> groupIds)
    {
        var diagnostics = new List<string>(current.Diagnostics);
        var groups = Normalize(groupIds, diagnostics);
        var payload = Serialize(groups, diagnostics);
        var padded = new byte[current.WritableLength];
        Array.Fill(padded, Padding);
        if (payload.Length < padded.Length) Buffer.BlockCopy(payload, 0, padded, 0, payload.Length);
        else diagnostics.Add($"错误：序列化后 {payload.Length} 字节超过安全区可用容量 {Math.Max(0, padded.Length - 1)} 字节；物理安全区 {padded.Length} 字节须保留 1 字节 0x90 边界。");
        var canWrite = payload.Length < padded.Length && !diagnostics.Any(item => item.StartsWith("错误：", StringComparison.Ordinal));
        return new AccessoryJobGroupPreview(current.FilePath, current.FileOffset, current.WritableLength, groups, payload, padded, canWrite, diagnostics);
    }

    public AccessoryJobGroupReadResult Write(string projectRoot, IReadOnlyList<IReadOnlyList<int>> groupIds)
    {
        var preview = Preview(projectRoot, groupIds);
        if (!preview.CanWrite) throw new InvalidOperationException(string.Join(Environment.NewLine, preview.Diagnostics));
        var before = File.ReadAllBytes(preview.FilePath);
        var output = (byte[])before.Clone();
        Buffer.BlockCopy(preview.PaddedBytes, 0, output, checked((int)preview.FileOffset), preview.PaddedBytes.Length);
        var backup = preview.FilePath + ".data管理器.groups.before";
        File.Copy(preview.FilePath, backup, true);
        var temp = preview.FilePath + ".data管理器.groups.tmp";
        try
        {
            File.WriteAllBytes(temp, output);
            File.Move(temp, preview.FilePath, true);
            var verify = Read(projectRoot);
            if (!GroupsEqual(verify.Groups, preview.Groups))
                throw new InvalidDataException("辅助装备分组写入后复读结果不一致。");
            return verify;
        }
        catch
        {
            File.Copy(backup, preview.FilePath, true);
            throw;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    public AccessoryJobGroupReadResult Write(ProjectWorkspace workspace, IReadOnlyList<IReadOnlyList<int>> groupIds)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var result = workspace.Save([PrepareSave(workspace, groupIds)]);
        if (!result.Saved) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Messages));
        return Read(workspace);
    }

    public static IReadOnlyList<AccessoryJobGroup> Parse(ReadOnlySpan<byte> bytes)
    {
        var diagnostics = new List<string>();
        var result = Parse(bytes, diagnostics);
        if (diagnostics.Any(item => item.StartsWith("错误：", StringComparison.Ordinal))) throw new InvalidDataException(string.Join(Environment.NewLine, diagnostics));
        return result;
    }

    public static byte[] Serialize(IReadOnlyList<IReadOnlyList<int>> groups)
    {
        var diagnostics = new List<string>();
        var normalized = Normalize(groups, diagnostics);
        var bytes = Serialize(normalized, diagnostics);
        if (diagnostics.Any(item => item.StartsWith("错误：", StringComparison.Ordinal))) throw new InvalidDataException(string.Join(Environment.NewLine, diagnostics));
        return bytes;
    }

    private static IReadOnlyList<AccessoryJobGroup> Parse(ReadOnlySpan<byte> bytes, List<string> diagnostics)
    {
        if (bytes.Length == 0 || bytes[0] != Separator) { diagnostics.Add("错误：分组必须以 FF 开头。"); return []; }
        var groups = new List<AccessoryJobGroup>();
        var current = new List<int>();
        for (var index = 1; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (value == Separator)
            {
                if (current.Count == 0) { if (index != bytes.Length - 1) diagnostics.Add("错误：存在空分组。"); }
                else { groups.Add(new AccessoryJobGroup(groups.Count, current.ToArray())); current.Clear(); }
                continue;
            }
            if (value > MaxJobSeriesId) diagnostics.Add($"错误：兵种系 ID 越界：{value}。");
            current.Add(value);
        }
        if (current.Count > 0) diagnostics.Add("错误：最后一个分组没有 FF 结尾。");
        return groups;
    }

    private static IReadOnlyList<AccessoryJobGroup> Normalize(IReadOnlyList<IReadOnlyList<int>> groups, List<string> diagnostics)
    {
        var result = new List<AccessoryJobGroup>();
        foreach (var source in groups)
        {
            if (source.Count == 0) { diagnostics.Add("错误：分组不能为空。"); continue; }
            var ids = new List<int>();
            var local = new HashSet<int>();
            foreach (var id in source)
            {
                if (id < 0 || id > MaxJobSeriesId) { diagnostics.Add($"错误：兵种系 ID 越界：{id}。"); continue; }
                if (!local.Add(id)) { diagnostics.Add($"错误：同一分组重复兵种系 ID：{id}。"); continue; }
                ids.Add(id);
            }
            if (ids.Count > 0) result.Add(new AccessoryJobGroup(result.Count, ids));
        }
        if (result.Count == 0) diagnostics.Add("错误：至少需要一个分组。");
        return result;
    }

    private static byte[] Serialize(IReadOnlyList<AccessoryJobGroup> groups, List<string> diagnostics)
    {
        var bytes = new List<byte> { Separator };
        foreach (var group in groups)
        {
            if (group.JobSeriesIds.Count == 0) { diagnostics.Add("错误：分组不能为空。"); continue; }
            bytes.AddRange(group.JobSeriesIds.Select(id => checked((byte)id)));
            bytes.Add(Separator);
        }
        return bytes.ToArray();
    }

    private static int DetectWritableLength(byte[] bytes, long offset, List<string> diagnostics, out int payloadLength)
    {
        payloadLength = 0;
        if (offset < 0 || offset >= bytes.LongLength) { diagnostics.Add("错误：PE 映射偏移越界。"); return 0; }
        var start = checked((int)offset);
        var maxEnd = Math.Min(bytes.Length, start + MaxProbeLength);
        var firstPadding = -1;
        for (var index = start; index < maxEnd; index++)
        {
            var value = bytes[index];
            if (value == Padding) { firstPadding = index; break; }
            if (value != Separator && value > MaxJobSeriesId) { diagnostics.Add($"错误：安全区出现未知字节 0x{value:X2}。"); return 0; }
        }
        if (firstPadding < 0) { diagnostics.Add("错误：没有找到连续 0x90 安全区。"); return 0; }
        payloadLength = firstPadding - start;
        // Native samples reserve extra FF bytes immediately before the NOP area.
        // They are spare capacity, rather than empty logical groups.
        var trailingSeparators = 0;
        for (var index = firstPadding - 1; index >= start && bytes[index] == Separator; index--) trailingSeparators++;
        if (trailingSeparators > 1)
        {
            payloadLength -= trailingSeparators - 1;
            diagnostics.Add($"表尾 {trailingSeparators - 1} 个额外 FF 作为保留容量，解析时保留单个结束 FF。");
        }
        var end = firstPadding;
        while (end < bytes.Length && end - start < MaxProbeLength && bytes[end] == Padding) end++;
        return end - start;
    }

    private static long MapVirtualAddress(byte[] bytes, uint virtualAddress)
    {
        if (bytes.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)) != 0x5A4D)
            throw new InvalidDataException("Ekd5.exe 不是可解析的 PE 文件。");
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
        if (peOffset < 0 || peOffset + 0x18 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(peOffset, 4)) != 0x00004550)
            throw new InvalidDataException("Ekd5.exe PE 头无效。");
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(peOffset + 20, 2));
        var optional = peOffset + 24;
        var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(optional + 28, 4));
        var rva = checked(virtualAddress - imageBase);
        var sections = optional + optionalSize;
        for (var index = 0; index < sectionCount; index++)
        {
            var section = sections + index * 40;
            if (section + 40 > bytes.Length) break;
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 8, 4));
            var virtualStart = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 12, 4));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 16, 4));
            var rawStart = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(section + 20, 4));
            var size = Math.Max(virtualSize, rawSize);
            if (rva >= virtualStart && rva < virtualStart + size) return rawStart + (rva - virtualStart);
        }
        throw new InvalidDataException($"无法将 VA 0x{virtualAddress:X8} 映射到 PE 文件偏移。");
    }

    private static bool GroupsEqual(IReadOnlyList<AccessoryJobGroup> left, IReadOnlyList<AccessoryJobGroup> right)
    {
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.JobSeriesIds.SequenceEqual(pair.Second.JobSeriesIds));
    }
}
