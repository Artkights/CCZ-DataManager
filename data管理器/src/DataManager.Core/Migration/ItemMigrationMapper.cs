using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Migration;

/// <summary>Normalizes accessory/consumable category markers before mapping different item codecs.</summary>
internal static class ItemMigrationMapper
{
    public static bool Prepare(ProjectWorkspace sourceWorkspace, ProjectWorkspace targetWorkspace,
        TableDocument source, TableDocument target, DataRow sourceRow, DataRow targetRow,
        List<MigrationChange> changes, out IReadOnlyDictionary<string, object?> mappedValues)
    {
        mappedValues = new Dictionary<string, object?>();
        if (source.Definition.Key is not ("items-low" or "items-high")) return false;
        var sourceDynamic = source.Definition.RecordCodecKey.Equals("item-66-15-v1", StringComparison.OrdinalIgnoreCase);
        var targetDynamic = target.Definition.RecordCodecKey.Equals("item-66-15-v1", StringComparison.OrdinalIgnoreCase);
        if (sourceDynamic == targetDynamic) return false;
        var sourceIndex = source.Data.Rows.IndexOf(sourceRow);
        var targetIndex = target.Data.Rows.IndexOf(targetRow);
        var sourceSemantics = ItemTypeService.Resolve(source, sourceIndex);
        var targetSemantics = ItemTypeService.Resolve(target, targetIndex);
        var sourceCategory = sourceSemantics.Category is ItemCategory.Accessory or ItemCategory.Consumable;
        var targetCategory = targetSemantics.Category is ItemCategory.Accessory or ItemCategory.Consumable;
        if (!sourceCategory && !targetCategory) return false;
        var id = Convert.ToInt32(sourceRow["ID"], CultureInfo.InvariantCulture);
        if (!sourceCategory || !targetCategory)
        {
            changes.Add(new MigrationChange(source.Definition.Key, source.Definition.DisplayName, "物品类别/整行", id,
                sourceSemantics.CategoryName, targetSemantics.CategoryName, MigrationChangeKind.Incompatible,
                "普通装备/未知行与辅助装备/道具类别之间没有已验证的自动转换；本行保持目标原值，避免交换 +0x11/+0x12 的不同语义。"));
            return true;
        }
        // Fixed 6.5 uses +11 as effect and +12 as category in the verified
        // accessory/consumable rows. Dynamic 6.6 exposes those bytes as the opposite
        // named columns. Both sides must already be proven marker-category rows.
        var effect = sourceRow[sourceDynamic ? "装备特效号" : "宝物类型"];
        var marker = sourceRow[sourceDynamic ? "宝物类型" : "装备特效号"];
        mappedValues = new Dictionary<string, object?>
        {
            ["宝物类型"] = targetDynamic ? marker : effect,
            ["装备特效号"] = targetDynamic ? effect : marker
        };
        return false;
    }

    public const string Reason = "辅助/道具逻辑映射：类别标记 +0x12 与实际效果 +0x11 按目标 Codec 转换，保留其他原始字节";
}
