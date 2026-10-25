using System.Globalization;
using DataManager.Core.Models;
using DataManager.Core.Editing;

namespace DataManager.Core.Domain;

public enum ItemCategory { Weapon, Armor, Accessory, Consumable, Unknown }

public sealed record ItemTypeSemantics(int TypeValue, ItemCategory Category, bool IsCategoryMarker,
    int? PermissionSlot, string Description)
{
    public string CategoryName => Category switch
    {
        ItemCategory.Weapon => "武器", ItemCategory.Armor => "防具",
        ItemCategory.Accessory => "辅助装备", ItemCategory.Consumable => "道具/消耗品", _ => "未知/预留"
    };
    public string? PermissionColumn => PermissionSlot is { } slot ? ItemTypeService.EquipmentColumns[slot] : null;

    /// <summary>Physical byte meanings used by the business editor.</summary>
    public ItemSemanticLayout Layout => ItemTypeService.GetLayout(Category);
}

public sealed record ItemSemanticLayout(
    ItemCategory Category,
    int? InitialAttributeOffset,
    int? EffectValueOffset,
    int? GrowthOffset,
    int? SuitableJobOffset,
    int? ConsumableParameterOffset,
    int GalleryOffset,
    string Evidence)
{
    public bool HasEquipmentGrowth => InitialAttributeOffset.HasValue && GrowthOffset.HasValue;
    public bool HasSuitableJob => SuitableJobOffset.HasValue;
}

/// <summary>Separates item category markers/effect codes from the 26 job permission slots.</summary>
public static class ItemTypeService
{
    public static IReadOnlyList<string> EquipmentColumns { get; } = Array.AsReadOnly(new[]
    {
        "普通剑", "特殊剑", "普通枪", "特殊枪", "普通弓", "特殊弓", "普通刀", "特殊刀",
        "普通炮车", "特殊炮车", "普通锤", "特殊锤", "普通斧", "特殊斧", "普通扇", "特殊扇",
        "普通宝剑", "特殊宝剑", "普通将剑", "特殊将剑", "普通铠甲", "特殊铠甲",
        "普通衣服", "特殊衣服", "普通袍服", "特殊袍服"
    });

    public static ItemTypeSemantics Resolve(TableDocument document, int rowIndex)
    {
        var row = document.Data.Rows[rowIndex];
        var type = Convert.ToInt32(row["宝物类型"], CultureInfo.InvariantCulture);
        var effect = Convert.ToInt32(row["装备特效号"], CultureInfo.InvariantCulture);
        return ResolveValues(document, rowIndex, type, effect);
    }

    internal static ItemTypeSemantics ResolveValues(TableDocument document, int rowIndex, int type, int effect)
    {
        var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        if (document.Definition.RowSize <= 0x12 || offset < 0 || offset + document.Definition.RowSize > document.OriginalFileBytes.Length)
            return new(type, ItemCategory.Unknown, false, null, "物品原始行边界无效，无法确定类型语义。");

        var rawMarker = document.OriginalFileBytes[offset + 0x12];
        var dynamicCodec = document.Definition.RecordCodecKey.Equals("item-66-15-v1", StringComparison.OrdinalIgnoreCase);
        // The original marker selects codec addresses throughout a draft. The
        // current logical value selects 2/3 after an unsaved category edit.
        if (dynamicCodec && document.OriginalFileBytes[offset + 0x11] >= 26 && rawMarker is 2 or 3)
        {
            var category = type switch { 2 => ItemCategory.Accessory, 3 => ItemCategory.Consumable, _ => ItemCategory.Unknown };
            return new(type, category, true, null,
                "6.6 类别标记位于 row+0x12：2=辅助装备，3=道具；实际特效号位于 row+0x11，不对应兵种装备许可槽。");
        }

        var marker = effect;
        // 40B444 reads runtime +11 first: <20 is weapon, <26 is armor.
        // Only larger type codes read runtime +14, which the loader rotates
        // from file +12. Item IDs and image-assigner INI boundaries do not
        // participate, including when an ordinary effect happens to be 2/3.
        var kind = type switch
        {
            >= 0 and <= 19 => ItemCategory.Weapon,
            >= 20 and <= 25 => ItemCategory.Armor,
            _ => marker switch
            {
                0 => ItemCategory.Weapon,
                1 => ItemCategory.Armor,
                2 => ItemCategory.Accessory,
                3 => ItemCategory.Consumable,
                _ => ItemCategory.Unknown
            }
        };
        var ordinary = kind is ItemCategory.Weapon or ItemCategory.Armor;
        int? slot = ordinary && type is >= 0 and < 26 ? type : null;
        return new(type, kind, false, slot,
            "按引擎类型码分类：0～19 为武器，20～25 为防具；超出时按 row+0x12 的 0/1/2/3 分为武器/防具/辅助/道具。仅 0～25 类型对应兵种许可槽，不使用宝物编号分段。");
    }

    public static IReadOnlyList<IdNameOption> GetOptions(ItemTypeSemantics semantics, ProjectWorkspace? workspace = null)
    {
        if (semantics.IsCategoryMarker) return [new(2, "辅助装备"), new(3, "道具/消耗品")];
        if (semantics.Category is ItemCategory.Weapon or ItemCategory.Armor)
            return EquipmentColumns.Select((name, id) => new IdNameOption(id,
                workspace == null ? name : new EquipmentTypeCatalogService().GetTypeDisplayName(workspace, id))).ToArray();
        var prefix = semantics.Category switch { ItemCategory.Accessory => "辅助效果码", ItemCategory.Consumable => "道具效果码", _ => "未确认类型码" };
        return Enumerable.Range(0, 256).Select(id => new IdNameOption(id, prefix + "（具体语义需实测）")).ToArray();
    }

    public static ItemSemanticLayout GetLayout(ItemCategory category) => category switch
    {
        ItemCategory.Weapon or ItemCategory.Armor => new(
            category, 0x15, 0x16, 0x17, null, null, 0x18,
            "旧 ExtendModifier 装备源码：+0x15 初始能力、+0x16 效果值、+0x17 成长值、+0x18 图鉴"),
        ItemCategory.Accessory => new(
            category, null, 0x15, null, 0x17, null, 0x18,
            "旧 ExtendModifier 装备源码：+0x15 辅助特效值、+0x16 空保留、+0x17 适合兵种、+0x18 图鉴"),
        ItemCategory.Consumable => new(
            category, null, null, null, null, 0x15, 0x18,
            "旧 ExtendModifier 道具源码：+0x15/+0x16 合并为 LE UInt16 参数，+0x17 不使用、+0x18 图鉴"),
        _ => new(category, null, null, null, null, null, 0x18,
            "类别未确认；仅保留原始字节")
    };
}
