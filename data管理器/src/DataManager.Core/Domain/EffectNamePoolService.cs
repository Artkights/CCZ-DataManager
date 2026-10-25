using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Editing;

namespace DataManager.Core.Domain;

public enum EffectNameChannel
{
    PersonalJob,
    Item
}

public enum EffectNameStorageKind
{
    FixedExeSlotPool,
    LegacyNullSeparatedExePool
}

/// <summary>
/// Reads the native effect-name pools without treating their bytes as an
/// ordinary fixed DataTable.  The 6.5 item pool is a packed, null-separated
/// GBK segment while the 6.6 item pool and the personal/job pool are fixed
/// 16-byte slots.  The raw bytes and evidence are returned with every slot so
/// callers can show why a value is read-only.
/// </summary>
public sealed class EffectNamePoolService
{
    public const long PersonalJobOffset = 0x9E800;
    public const long ItemFixedOffset66 = 0x9D9A0;
    public const int FixedSlotSize = 16;
    public const int FixedMaxNameBytes = 15;

    public IReadOnlyList<EffectNameSlot> Read(ProjectWorkspace workspace, EffectNameChannel channel)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.RootPath)) return Array.Empty<EffectNameSlot>();
        if (channel == EffectNameChannel.PersonalJob)
        {
            var definition = workspace.Definitions.FirstOrDefault(table => table.Key == "effect-names");
            if (workspace.ActiveProfile.ProfileId == "ccz-6.4" && definition?.Capability == Models.TableCapability.Unavailable)
                return Array.Empty<EffectNameSlot>();
            var slots = ReadFixed(workspace, 0, definition?.RowCount ?? 255, definition?.DataOffset ?? PersonalJobOffset, "人物/兵种特效名称",
                definition?.CapabilityEvidence ?? "6.5/6.6 Ekd5.exe 固定 16 字节名称池");
            if (workspace.Documents.TryGetValue("effect-names", out var document))
                return slots.Select(slot => slot with { IsWritable = definition?.IsWriteSupported == true,
                    Name = slot.SlotIndex < document.Data.Rows.Count ? Convert.ToString(document.Data.Rows[slot.SlotIndex]["名称"], CultureInfo.InvariantCulture) ?? slot.Name : slot.Name }).ToArray();
            return slots;
        }
        return Is66(workspace)
                ? ReadFixed(workspace, 0x1A, 0xE6, ItemFixedOffset66, "装备特效名称", "6.6 Ekd5.exe +0x9D9A0，230 个 16 字节槽")
                : ReadLegacyItems(workspace);
    }

    public IReadOnlyDictionary<int, string> BuildLookup(ProjectWorkspace workspace, EffectNameChannel channel)
        => Read(workspace, channel)
            .Where(slot => !string.IsNullOrWhiteSpace(slot.Name))
            .GroupBy(slot => slot.EffectId)
            .ToDictionary(group => group.Key,
                group => string.Join(" / ", group.Select(slot => slot.Name).Distinct(StringComparer.CurrentCulture)),
                EqualityComparer<int>.Default);

    public bool TryGetName(ProjectWorkspace workspace, EffectNameChannel channel, int effectId, out string name)
    {
        name = BuildLookup(workspace, channel).GetValueOrDefault(effectId, string.Empty);
        return !string.IsNullOrWhiteSpace(name);
    }

    private static IReadOnlyList<EffectNameSlot> ReadFixed(ProjectWorkspace workspace, int beginId, int count,
        long offset, string displayName, string evidence)
    {
        var path = ProjectFileLocator.Resolve(workspace.RootPath, "Ekd5.exe");
        if (!File.Exists(path)) return Array.Empty<EffectNameSlot>();
        var bytes = File.ReadAllBytes(path);
        if (offset < 0 || offset + (long)count * FixedSlotSize > bytes.LongLength) return Array.Empty<EffectNameSlot>();
        var result = new List<EffectNameSlot>(count);
        for (var index = 0; index < count; index++)
        {
            var fileOffset = checked(offset + index * (long)FixedSlotSize);
            var raw = bytes.AsSpan(checked((int)fileOffset), FixedSlotSize).ToArray();
            result.Add(new EffectNameSlot(
                beginId + index,
                index,
                displayName,
                EffectNameStorageKind.FixedExeSlotPool,
                path,
                fileOffset,
                raw,
                EncodingService.DecodeFixedStringPreservingInvalidBytes(raw),
                false,
                evidence));
        }
        return result;
    }

    private static IReadOnlyList<EffectNameSlot> ReadLegacyItems(ProjectWorkspace workspace)
    {
        var result = new List<EffectNameSlot>();
        foreach ((string key, int beginId) in new[] { ("item-effect-names-low", 0x1A), ("item-effect-names-high", 0x58) })
        {
            var definition = workspace.Definitions.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (definition == null) continue;
            var path = ProjectFileLocator.Resolve(workspace.RootPath, definition.FileName);
            if (!File.Exists(path) || definition.DataOffset < 0 || definition.EndOffsetExclusive > new FileInfo(path).Length) continue;
            var bytes = File.ReadAllBytes(path);
            var segment = bytes.AsSpan(checked((int)definition.DataOffset), checked(definition.RowSize * definition.RowCount)).ToArray();
            var parsed = SplitLegacySlots(segment, key.EndsWith("low", StringComparison.OrdinalIgnoreCase) ? 62 : 40);
            for (var slot = 0; slot < parsed.Count; slot++)
            {
                var native = parsed[slot];
                result.Add(new EffectNameSlot(
                    beginId + slot,
                    slot,
                    "装备特效名称",
                    EffectNameStorageKind.LegacyNullSeparatedExePool,
                    path,
                    definition.DataOffset + native.Offset,
                    native.Raw,
                    EncodingService.DecodeFixedStringPreservingInvalidBytes(native.Raw.AsSpan(0, native.NameLength)),
                    false,
                    "6.5 Ekd5.exe GBK 变长名称池；物理表只读，保留槽尾/分隔符"));
            }
        }
        return result;
    }

    /// <summary>Validates a logical rename against the target native slot without writing any file.</summary>
    public EffectNameWritePreview PreviewItemName(ProjectWorkspace workspace, int effectId, string? newName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (effectId < 0) throw new ArgumentOutOfRangeException(nameof(effectId));
        if (newName == null || newName.Contains('\0'))
            throw new InvalidOperationException("名称不能为 null 或包含零结束字符。");
        var slot = Read(workspace, EffectNameChannel.Item).FirstOrDefault(item => item.EffectId == effectId)
            ?? throw new InvalidOperationException($"没有找到装备特效 {effectId} 的原生名称槽。");
        var current = File.ReadAllBytes(slot.FilePath);
        var output = (byte[])current.Clone();
        if (slot.StorageKind == EffectNameStorageKind.FixedExeSlotPool)
        {
            ValidateFixedItemPool66(workspace, current);
            var next = EncodeFixedPreservingTail(slot.OriginalBytes, newName);
            next.CopyTo(output, checked((int)slot.FileOffset));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new InvalidOperationException("6.5 变长名称池不能写入空名称，否则后续名称的逻辑 ID 会前移。");
            var encoded = StrictGbk.GetBytes(newName);
            if (encoded.Length >= slot.OriginalBytes.Length)
                throw new InvalidOperationException($"名称超过本槽含零填充的容量 {slot.OriginalBytes.Length - 1} 字节；不能移动后续名称或非零保留区。");
            // Native code can reference individual strings by address. Keep every
            // other slot and the suffix at its original offset, consuming only
            // this slot's own trailing zero padding when the name grows.
            var next = new byte[slot.OriginalBytes.Length];
            encoded.CopyTo(next, 0);
            next.CopyTo(output, checked((int)slot.FileOffset));
        }

        // Use the same lossless decoder as the physical table before writing.
        var nativeBytes = output.AsSpan(checked((int)slot.FileOffset), slot.OriginalBytes.Length);
        if (!EncodingService.DecodeFixedString(nativeBytes).Equals(newName, StringComparison.Ordinal))
            throw new InvalidOperationException("名称不能按原始 GBK 字节无损复读。");

        return new EffectNameWritePreview(slot, newName, current, output,
            HashService.Sha256(current), HashService.Sha256(output));
    }

    public EffectNameWriteResult WriteItemName(ProjectWorkspace workspace, int effectId, string? newName)
    {
        var preview = PreviewItemName(workspace, effectId, newName);
        var (slot, normalizedName, current, output, beforeHash, afterHash) = preview;
        if (beforeHash.Equals(afterHash, StringComparison.OrdinalIgnoreCase))
            return new EffectNameWriteResult(slot.FilePath, effectId, slot.Name, normalizedName, 0, beforeHash, afterHash);
        var result = new WorkspaceBinaryTransaction().Commit(workspace, $"修改装备特效 {effectId} 名称",
            [new WorkspaceBinaryCandidate(slot.FilePath, current, output, "宝物设定")], () =>
        {
            var reread = Read(workspace, EffectNameChannel.Item).FirstOrDefault(item => item.EffectId == effectId);
            if (reread == null || !string.Equals(reread.Name, normalizedName, StringComparison.Ordinal))
                throw new InvalidDataException("装备特效名称写入后复读不一致。");
        });
        if (!result.Saved) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Messages));
        return new EffectNameWriteResult(slot.FilePath, effectId, slot.Name, normalizedName,
            CountChangedBytes(current, output), beforeHash, afterHash);
    }

    private static Encoding StrictGbk => Encoding.GetEncoding(EncodingService.GameEncoding.CodePage,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static byte[] EncodeFixedPreservingTail(byte[] original, string? value)
        => EncodingService.EncodeFixedStringPreservingTail(value, original,
            new(original.Length, Math.Min(FixedMaxNameBytes, original.Length - 1), true,
                "装备特效固定槽保留 NUL 及非零尾部参数。"));

    private static List<(int Offset, int NameLength, byte[] Raw)> SplitLegacySlots(byte[] segment, int maximumCount)
    {
        var result = new List<(int Offset, int NameLength, byte[] Raw)>();
        var start = 0;
        while (start < segment.Length && result.Count < maximumCount)
        {
            while (start < segment.Length && segment[start] == 0) start++;
            if (start == segment.Length) break;
            var end = Array.IndexOf(segment, (byte)0, start);
            if (end < 0) break;
            var nextStart = end + 1;
            while (nextStart < segment.Length && segment[nextStart] == 0) nextStart++;
            result.Add((start, end - start, segment.AsSpan(start, nextStart - start).ToArray()));
            start = nextStart;
        }
        return result;
    }

    private static void ValidateFixedItemPool66(ProjectWorkspace workspace, byte[] current)
    {
        if (!Is66(workspace)) throw new InvalidOperationException("固定装备特效名称槽仅适用于 6.6。");
        var end = checked((int)ItemFixedOffset66 + 230 * FixedSlotSize);
        if (current.Length < end) throw new InvalidDataException("6.6 装备特效名称池越界。");
        var snapshots = new List<byte[]> { current };
        snapshots.AddRange(workspace.Documents.Values.Where(document => Path.GetFileName(document.FilePath).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase))
            .Select(document => document.OriginalFileBytes).Take(1));
        if (snapshots.Any(HasFixedItemSentinels)) return;

        // Names are user editable. Reconstruct a prior checked snapshot from our
        // own saved deltas so renaming the sentinel slots never locks the pool.
        var prior = current.ToArray();
        foreach (var entry in workspace.GetHistory().Where(entry => entry.Status == DataManager.Core.Models.HistoryStatus.Active)
                     .OrderByDescending(entry => entry.CreatedAt))
        {
            foreach (var file in entry.Files.Where(file => Path.GetFileName(file.RelativePath).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)))
            {
                if (!HashService.Sha256(prior).Equals(file.AfterHash, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var chunk in file.Chunks) chunk.OldBytes.CopyTo(prior, checked((int)chunk.Offset));
                if (!HashService.Sha256(prior).Equals(file.BeforeHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("名称池历史快照 SHA-256 不一致。");
                if (HasFixedItemSentinels(prior)) return;
            }
        }

        // A mod may already have custom sentinel names before first opening.
        // Require structural proof across the entire fixed-slot pool in that case.
        var nonEmpty = 0;
        for (var index = 0; index < 230; index++)
        {
            var bytes = current.AsSpan(checked((int)ItemFixedOffset66 + index * FixedSlotSize), FixedSlotSize);
            var terminator = bytes.IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException($"6.6 名称槽 {index} 缺少结束字节，哨兵与结构均未通过。");
            if (terminator == 0) continue;
            var name = StrictGbk.GetString(bytes[..terminator]);
            if (name.Any(char.IsControl)) throw new InvalidDataException($"6.6 名称槽 {index} 含控制字符，布局证据不足。");
            nonEmpty++;
        }
        if (nonEmpty < 40) throw new InvalidDataException("6.6 装备特效名称池哨兵不匹配，且有效名称槽不足以确认结构。");
    }

    private static bool HasFixedItemSentinels(byte[] bytes)
        => new[] { (0, "恢复HP和状态"), (28, "引导攻击"), (56, "回复周围HP状态"), (72, "策略连击") }
            .All(item => bytes.Length >= ItemFixedOffset66 + (long)(item.Item1 + 1) * FixedSlotSize &&
                EncodingService.DecodeFixedString(bytes.AsSpan(checked((int)ItemFixedOffset66 + item.Item1 * FixedSlotSize), FixedSlotSize)) == item.Item2);

    private static int CountChangedBytes(byte[] before, byte[] after)
        => before.Zip(after).Count(pair => pair.First != pair.Second);

    private static bool Is66(ProjectWorkspace workspace)
        => workspace.ActiveProfile.ProfileId.Equals("ccz-6.6", StringComparison.OrdinalIgnoreCase);
}

public sealed record EffectNameSlot(
    int EffectId,
    int SlotIndex,
    string DisplayName,
    EffectNameStorageKind StorageKind,
    string FilePath,
    long FileOffset,
    byte[] OriginalBytes,
    string Name,
    bool IsWritable,
    string Evidence)
{
    public string Source => $"{Path.GetFileName(FilePath)}+0x{FileOffset:X}";
    public int GbkByteLength => OriginalBytes.AsSpan().IndexOf((byte)0) is var end && end >= 0 ? end : OriginalBytes.Length;
    public string IdDisplay => $"{EffectId.ToString(CultureInfo.InvariantCulture)}：{Name}";
}

public sealed record EffectNameWriteResult(
    string FilePath,
    int EffectId,
    string OldName,
    string NewName,
    int ChangedBytes,
    string BeforeSha256,
    string AfterSha256);

public sealed record EffectNameWritePreview(
    EffectNameSlot Slot,
    string NewName,
    byte[] BeforeBytes,
    byte[] AfterBytes,
    string BeforeSha256,
    string AfterSha256);
