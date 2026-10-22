using System.Globalization;
using DataManager.Core.Models;
using DataManager.Core.Editing;

namespace DataManager.Core.Domain;

public enum ItemCategory { Weapon, Armor, Accessory, Consumable, Unknown }

public sealed record ItemCategoryBoundary(int DefenseStartId = 70, int AccessoryStartId = 109)
{
    public static ItemCategoryBoundary Read(string rootPath)
    {
        // These are the same project-side keys used by the image assigner.
        // Never borrow another project's INI while classifying this project.
        foreach (var relative in new[] { "System.ini", Path.Combine("RS", "System.ini"),
                     Path.Combine("B形象指定器", "6.6x形象指定器", "System.ini"),
                     Path.Combine("B形象指定器", "形象指定器6.5", "System.ini") })
        {
            var path = Path.Combine(rootPath, relative);
            if (!File.Exists(path)) continue;
            int? defense = null, accessory = null;
            // Relevant key/value bytes are ASCII even in a GBK INI.
            foreach (var raw in File.ReadLines(path))
            {
                var pair = raw.Split(';', 2)[0].Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) continue;
                if (pair[0].Equals("DefID", StringComparison.OrdinalIgnoreCase)) defense = value;
                if (pair[0].Equals("AssID", StringComparison.OrdinalIgnoreCase)) accessory = value;
            }
            if (defense is >= 0 and <= 255 && accessory is >= 0 and <= 255 && defense <= accessory)
                return new ItemCategoryBoundary(defense.Value, accessory.Value);
        }
        return new ItemCategoryBoundary();
    }
}

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

    public static ItemTypeSemantics Resolve(TableDocument document, int rowIndex, ItemCategoryBoundary? boundary = null)
    {
        boundary ??= new ItemCategoryBoundary();
        var row = document.Data.Rows[rowIndex];
        var type = Convert.ToInt32(row["宝物类型"], CultureInfo.InvariantCulture);
        var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
        var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        if (document.Definition.RowSize <= 0x12 || offset < 0 || offset + document.Definition.RowSize > document.OriginalFileBytes.Length)
            return new(type, ItemCategory.Unknown, false, null, "物品原始行边界无效，无法确定类型语义。");

        var rawMarker = document.OriginalFileBytes[offset + 0x12];
        var dynamicCodec = document.Definition.RecordCodecKey.Equals("item-66-15-v1", StringComparison.OrdinalIgnoreCase);
        // The original marker selects codec addresses throughout a draft. The
        // current logical value selects 2/3 after an unsaved category edit.
        if (dynamicCodec && rawMarker is 2 or 3)
        {
            var category = type switch { 2 => ItemCategory.Accessory, 3 => ItemCategory.Consumable, _ => ItemCategory.Unknown };
            return new(type, category, true, null,
                "6.6 类别标记位于 row+0x12：2=辅助装备，3=道具；实际特效号位于 row+0x11，不对应兵种装备许可槽。");
        }

        var marker = Convert.ToInt32(row["装备特效号"], CultureInfo.InvariantCulture);
        var kind = id < boundary.DefenseStartId ? ItemCategory.Weapon
            : id < boundary.AccessoryStartId ? ItemCategory.Armor
            : marker switch { 2 => ItemCategory.Accessory, 3 => ItemCategory.Consumable, _ => ItemCategory.Unknown };
        var ordinary = kind is ItemCategory.Weapon or ItemCategory.Armor;
        int? slot = ordinary && type is >= 0 and < 26 ? type : null;
        return new(type, kind, false, slot, ordinary
            ? "普通武器/防具类型码直接对应 26 类兵种许可槽，不按十位数分组；当前数值超出槽范围时不提供许可。"
            : "辅助段的类型字节是效果/子类型码，装备特效字节用于区分类别；不对应 26 类兵种装备许可。");
    }

    public static IReadOnlyList<IdNameOption> GetOptions(ItemTypeSemantics semantics, ProjectWorkspace? workspace = null)
    {
        if (semantics.IsCategoryMarker) return [new(2, "辅助装备"), new(3, "道具/消耗品")];
        if (semantics.Category is ItemCategory.Weapon or ItemCategory.Armor)
            return EquipmentColumns.Select((name, id) => new IdNameOption(id,
                workspace == null ? name : new EquipmentTypeCatalogService().GetTypeDisplayName(workspace, id))).ToArray();
        var prefix = semantics.Category switch { ItemCategory.Accessory => "辅助效果码", ItemCategory.Consumable => "道具效果码", _ => "辅助段类型码" };
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
