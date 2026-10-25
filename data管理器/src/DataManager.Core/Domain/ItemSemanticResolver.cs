using DataManager.Core.Models;
using DataManager.Core.Binary;

namespace DataManager.Core.Domain;

public sealed record ItemSemanticField(
    string DisplayName,
    int PhysicalOffset,
    int? CurrentValue,
    bool IsEditable,
    string SemanticSource,
    string Evidence);

public sealed record ItemSemanticSnapshot(
    ItemCategory Category,
    int TypeValue,
    byte OriginalCategoryMarker,
    int ActualEffectNumber,
    int? InitialAttribute,
    int? EffectValue,
    int? GrowthValue,
    int? SuitableJob,
    int? ReservedValue,
    int GalleryValue,
    string RawSummary,
    IReadOnlyList<ItemSemanticField> Fields,
    IReadOnlyList<string> Diagnostics,
    ItemTypeSemantics Semantics);

/// <summary>
/// Converts the physical 25-byte item row into category-aware business
/// semantics without changing the Profile codec or the original byte array.
/// Draft values are overlaid through the active codec. Writeback continues
/// through the existing physical field transaction and preserves unknown bytes.
/// </summary>
public static class ItemSemanticResolver
{
    public static ItemSemanticSnapshot Resolve(TableDocument document, int rowIndex)
    {
        var semantics = ItemTypeService.Resolve(document, rowIndex);
        var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        var raw = document.OriginalFileBytes.AsSpan(offset, document.Definition.RowSize).ToArray();
        var codec = new RecordCodecRegistry().Get(document.Definition.RecordCodecKey);
        var row = document.Data.Rows[rowIndex];
        foreach (var field in document.Definition.Fields)
        {
            if (!document.Data.Columns.Contains(field.Name) || field.Policy != FieldPolicy.Editable) continue;
            var value = row[field.Name];
            if (value == DBNull.Value || Equals(codec.Decode(raw, field), value)) continue;
            // Address selection must use the original marker for the complete
            // draft, just as BinaryTableWriter does for a saved transaction.
            foreach (var patch in codec.EncodeChangedField(document.OriginalFileBytes.AsSpan(offset, document.Definition.RowSize), field, value))
                patch.Bytes.CopyTo(raw, patch.Offset);
        }
        var layout = semantics.Layout;
        var marker = document.OriginalFileBytes.Length > offset + 0x12 ? document.OriginalFileBytes[offset + 0x12] : (byte)0;
        var actualEffect = raw.Length > 0x12
            ? semantics.Category is ItemCategory.Accessory or ItemCategory.Consumable ? raw[0x11] : raw[0x12]
            : 0;
        int? ByteAt(int? relative) => relative is { } value && value >= 0 && value < raw.Length ? raw[value] : null;
        var initial = ByteAt(layout.InitialAttributeOffset);
        var effect = ByteAt(layout.EffectValueOffset);
        var growth = ByteAt(layout.GrowthOffset);
        var suitable = ByteAt(layout.SuitableJobOffset);
        var reserved = semantics.Category == ItemCategory.Accessory ? ByteAt(0x16) :
            semantics.Category == ItemCategory.Consumable ? ByteAt(0x17) : null;
        var gallery = ByteAt(layout.GalleryOffset) ?? 0;
        var fields = new List<ItemSemanticField>();
        void Add(string name, int physicalOffset, int? value, bool editable, string source)
            => fields.Add(new(name, physicalOffset, value, editable, source, layout.Evidence));
        if (initial.HasValue) Add("初始属性", layout.InitialAttributeOffset!.Value, initial, true, "Profile 物理字段");
        if (effect.HasValue) Add("特殊效果值", layout.EffectValueOffset!.Value, effect, semantics.Category != ItemCategory.Unknown, "Profile 物理字段");
        if (growth.HasValue) Add("能力成长", layout.GrowthOffset!.Value, growth, true, "Profile 物理字段");
        if (suitable.HasValue) Add("适用兵种", layout.SuitableJobOffset!.Value, suitable, true, "原始字节与山贼国庆五例核对；255=所有兵种");
        if (reserved.HasValue) Add("空/保留", semantics.Category == ItemCategory.Accessory ? 0x16 : 0x17, reserved, true, "原始字段页编辑，未修改则保留");
        Add("图鉴", layout.GalleryOffset, gallery, true, "Profile 物理字段；勾选框写入0/1");
        var diagnostics = new List<string>();
        if (semantics.Category == ItemCategory.Unknown) diagnostics.Add("当前类别未确认；仍允许编辑物理字段，保留未修改字节。");
        if (semantics.IsCategoryMarker) diagnostics.Add("6.6 类别标记与实际特效号分离；实际特效号来自 row+0x11。");
        return new(
            semantics.Category,
            semantics.TypeValue,
            marker,
            actualEffect,
            initial,
            effect,
            growth,
            suitable,
            reserved,
            gallery,
            Convert.ToHexString(raw),
            fields,
            diagnostics,
            semantics);
    }
}
