using System.Data;
using System.Globalization;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Migration;

/// <summary>
/// Fills a genuinely unused 6.6 item slot from a proven accessory/consumable
/// record. Category, effect and all ordinary values are one atomic logical row;
/// an existing item or an unknown source category is never reinterpreted.
/// </summary>
internal static class AtomicItemMigrationMapper
{
    public const string TableKey = "item-empty-slot-fill";
    internal sealed record Payload(string PhysicalTableKey, int Id, string TargetRowSha, Dictionary<string, string> Values);

    public static bool TryMap(ProjectWorkspace sourceWorkspace, ProjectWorkspace targetWorkspace,
        TableDocument source, TableDocument target, DataRow sourceRow, DataRow targetRow, List<MigrationChange> changes)
    {
        if (source.Definition.Key is not ("items-low" or "items-high") ||
            !target.Definition.RecordCodecKey.Equals("item-66-15-v1", StringComparison.OrdinalIgnoreCase) ||
            source.Definition.RecordCodecKey.Equals(target.Definition.RecordCodecKey, StringComparison.OrdinalIgnoreCase)) return false;
        var sourceIndex = source.Data.Rows.IndexOf(sourceRow);
        var targetIndex = target.Data.Rows.IndexOf(targetRow);
        if (source.Definition.Capability != TableCapability.Editable || target.Definition.Capability != TableCapability.Editable ||
            !IsUnusedSlot(target, targetIndex)) return false;
        var semantics = ItemTypeService.Resolve(source, sourceIndex, ItemCategoryBoundary.Read(sourceWorkspace.RootPath));
        if (semantics.Category is not (ItemCategory.Accessory or ItemCategory.Consumable)) return false;
        var sourceRaw = RawRow(source, sourceIndex);
        if (sourceRaw[0x12] is not (2 or 3)) return false;
        var id = Convert.ToInt32(sourceRow["ID"], CultureInfo.InvariantCulture);
        try
        {
            var values = target.Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable)
                .ToDictionary(field => field.Name, field =>
                {
                    if (!source.Data.Columns.Contains(field.Name) || source.Definition.Fields.All(f => f.Name != field.Name || f.Policy != FieldPolicy.Editable))
                        throw new InvalidDataException("源缺少可写同名字段：" + field.Name);
                    return Convert.ToString(sourceRow[field.Name], CultureInfo.InvariantCulture) ?? string.Empty;
                }, StringComparer.Ordinal);
            values["宝物类型"] = sourceRaw[0x12].ToString(CultureInfo.InvariantCulture);
            values["装备特效号"] = sourceRaw[0x11].ToString(CultureInfo.InvariantCulture);
            var payload = new Payload(target.Definition.Key, id, HashService.Sha256(RawRow(target, targetIndex).ToArray()), values);
            _ = BuildRow(target, targetIndex, payload);
            changes.Add(new MigrationChange(TableKey, $"{source.Definition.DisplayName}·填充空闲槽", "完整宝物记录", id,
                JsonSerializer.Serialize(payload), Convert.ToString(targetRow["名称"], CultureInfo.InvariantCulture), MigrationChangeKind.NativeStructureChanged,
                "目标为未启用空闲槽；以一次原子记录写入建立辅助/道具类别，再按目标 Codec 映射名称、图标、效果和数值，保留目标 +0x10/+0x14 及其他保留字节。"));
        }
        catch (Exception ex)
        {
            changes.Add(new MigrationChange(source.Definition.Key, source.Definition.DisplayName, "物品类别/整行", id,
                Convert.ToString(sourceRow["名称"], CultureInfo.InvariantCulture), Convert.ToString(targetRow["名称"], CultureInfo.InvariantCulture),
                MigrationChangeKind.Incompatible, "空闲槽完整记录不能无损映射：" + ex.Message));
        }
        return true;
    }

    public static bool Apply(ProjectWorkspace workspace, MigrationChange change, MigrationChoice choice)
        => ApplyBatch(workspace, [(change, choice)]) == 1;

    public static int ApplyBatch(ProjectWorkspace workspace, IReadOnlyList<(MigrationChange Change, MigrationChoice Choice)> changes)
    {
        var pending = new List<(TableDocument Document, int Index, Payload Payload, byte[] Row)>();
        foreach (var (change, choice) in changes)
        {
            if (choice == MigrationChoice.Skip) continue;
            if (change.TableKey != TableKey || change.Kind != MigrationChangeKind.NativeStructureChanged)
                throw new InvalidOperationException("不是空闲宝物槽迁移项。");
            var payload = JsonSerializer.Deserialize<Payload>(Convert.ToString(change.SourceValue, CultureInfo.InvariantCulture) ?? string.Empty)
                ?? throw new InvalidDataException("缺少空闲宝物槽逻辑记录。");
            if (payload.Id != change.Id || !workspace.Documents.TryGetValue(payload.PhysicalTableKey, out var document) ||
                payload.PhysicalTableKey is not ("items-low" or "items-high") ||
                document.Definition.RecordCodecKey != "item-66-15-v1" || document.Definition.Capability != TableCapability.Editable)
                throw new InvalidDataException("空闲宝物槽的目标记录/Codec不匹配。");
            var index = payload.Id - document.Definition.BeginId;
            if (!IsUnusedSlot(document, index))
            {
                if (choice == MigrationChoice.Merge) continue;
                throw new InvalidOperationException($"宝物 {payload.Id} 已有启用记录，不能通过空闲槽迁移覆盖。");
            }
            if (HashService.Sha256(RawRow(document, index).ToArray()) != payload.TargetRowSha)
                throw new IOException($"宝物 {payload.Id} 的目标原始行已在预览后变化。");
            pending.Add((document, index, payload, BuildRow(document, index, payload)));
        }
        if (pending.Count == 0) return 0;
        var candidates = new List<WorkspaceBinaryCandidate>();
        foreach (var group in pending.GroupBy(item => item.Document.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var before = File.ReadAllBytes(group.Key); var after = before.ToArray();
            foreach (var item in group)
            {
                var offset = checked((int)(item.Document.Definition.DataOffset + (long)item.Index * item.Document.Definition.RowSize));
                item.Row.CopyTo(after, offset);
            }
            candidates.Add(new WorkspaceBinaryCandidate(group.Key, before, after, "宝物设定"));
        }
        var result = new WorkspaceBinaryTransaction().Commit(workspace, $"迁移 {pending.Count} 个空闲宝物槽", candidates, () =>
        {
            var files = candidates.ToDictionary(item => item.FilePath, item => File.ReadAllBytes(item.FilePath), StringComparer.OrdinalIgnoreCase);
            foreach (var item in pending)
            {
                var offset = checked((int)(item.Document.Definition.DataOffset + (long)item.Index * item.Document.Definition.RowSize));
                var actual = files[item.Document.FilePath].AsSpan(offset, item.Document.Definition.RowSize);
                if (!actual.SequenceEqual(item.Row)) throw new InvalidDataException("宝物完整记录写后字节复读失败：" + item.Payload.Id);
                VerifyValues(item.Document, actual, item.Payload);
            }
        });
        if (!result.Saved) throw new InvalidOperationException(string.Join("；", result.Messages));
        return pending.Count;
    }

    private static bool IsUnusedSlot(TableDocument document, int index)
    {
        if (index < 0 || index >= document.Data.Rows.Count || document.Definition.RowSize != 25) return false;
        var raw = RawRow(document, index);
        var name = EncodingService.DecodeFixedString(raw[..15]);
        if (name.Length != 0 && name != "空闲") return false;
        // Only the unused display name and preserved +10/+14 may be nonzero.
        // This does not treat a named or partially configured item as an empty slot.
        if (raw[0x0F] != 0) return false;
        for (var offset = 0x11; offset < raw.Length; offset++)
            if (offset != 0x14 && raw[offset] != 0) return false;
        return true;
    }

    private static byte[] BuildRow(TableDocument document, int index, Payload payload)
    {
        var fields = document.Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable).ToArray();
        if (fields.Length != payload.Values.Count || fields.Any(field => !payload.Values.ContainsKey(field.Name)))
            throw new InvalidDataException("完整宝物记录的可写字段集合不匹配。");
        var original = RawRow(document, index); var output = original.ToArray();
        var marker = BinaryValueCodec.ParseInteger(payload.Values["宝物类型"], 2, 3, "宝物类别标记");
        output[0x12] = checked((byte)marker);
        var codec = new Item66RecordCodec();
        foreach (var field in fields)
            foreach (var patch in codec.EncodeChangedField(output, field, payload.Values[field.Name]))
                patch.Bytes.CopyTo(output, patch.Offset);
        if (output[0x10] != original[0x10] || output[0x14] != original[0x14])
            throw new InvalidDataException("空闲宝物槽映射试图覆盖目标保留字节。");
        VerifyValues(document, output, payload);
        return output;
    }

    private static void VerifyValues(TableDocument document, ReadOnlySpan<byte> row, Payload payload)
    {
        var codec = new Item66RecordCodec();
        foreach (var field in document.Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable))
            if (Convert.ToString(codec.Decode(row, field), CultureInfo.InvariantCulture) != payload.Values[field.Name])
                throw new InvalidDataException($"宝物 {payload.Id}/{field.Name} 不能按目标 Codec 无损复读。");
    }

    private static ReadOnlySpan<byte> RawRow(TableDocument document, int index)
    {
        var offset = checked((int)(document.Definition.DataOffset + (long)index * document.Definition.RowSize));
        return document.OriginalFileBytes.AsSpan(offset, document.Definition.RowSize);
    }
}
