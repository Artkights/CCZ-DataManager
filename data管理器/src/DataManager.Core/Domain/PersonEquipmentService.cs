using System.Data;
using System.Globalization;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

/// <summary>One category rule for person equipment pickers, edits, imports and migration.</summary>
public static class PersonEquipmentService
{
    public const int NoneId = 255;
    private static readonly string[] ItemTableKeys = ["items-low", "items-high", "items-extended"];

    public static ItemCategory? CategoryForField(string fieldName) => fieldName switch
    {
        "默认武器" => ItemCategory.Weapon,
        "默认防具" => ItemCategory.Armor,
        "默认辅助" => ItemCategory.Accessory,
        _ => null
    };

    public static IReadOnlyList<IdNameOption> GetOptions(IReadOnlyDictionary<string, TableDocument> documents,
        string fieldName, long minimum = 0, long maximum = 255)
    {
        if (CategoryForField(fieldName) is not { } category) return [];
        var options = new List<IdNameOption>();
        var seen = new HashSet<int>();
        foreach (var (document, rowIndex, row, id) in ItemRows(documents))
        {
            // Person defaults are UInt8 IDs. This is their physical capacity,
            // not an item-category boundary; extended IDs cannot fit this slot.
            if (id == NoneId || id < minimum || id > maximum || id > byte.MaxValue || !seen.Add(id) ||
                ItemTypeService.Resolve(document, rowIndex).Category != category) continue;
            options.Add(new(id, row.Table.Columns.Contains("名称")
                ? Convert.ToString(row["名称"], CultureInfo.InvariantCulture) ?? string.Empty : string.Empty));
        }
        if (NoneId >= minimum && NoneId <= maximum) options.Add(new(NoneId, "无装备"));
        return options.OrderBy(option => option.Id).ToArray();
    }

    public static void Validate(ProjectWorkspace workspace, string fieldName, int itemId,
        IReadOnlyList<CompositeCellEdit>? pendingEdits = null)
    {
        if (CategoryForField(fieldName) is not { } expected || itemId == NoneId) return;
        var item = ItemRows(workspace.Documents).FirstOrDefault(item => item.Id == itemId);
        ItemTypeSemantics? semantics = null;
        if (item.Document != null)
        {
            var type = Convert.ToInt32(item.Row["宝物类型"], CultureInfo.InvariantCulture);
            var effect = Convert.ToInt32(item.Row["装备特效号"], CultureInfo.InvariantCulture);
            foreach (var edit in pendingEdits ?? [])
            {
                if (edit.TableKey != item.Document.Definition.Key || edit.RowIndex != item.RowIndex) continue;
                if (edit.FieldName == "宝物类型") type = Convert.ToInt32(edit.NewValue, CultureInfo.InvariantCulture);
                if (edit.FieldName == "装备特效号") effect = Convert.ToInt32(edit.NewValue, CultureInfo.InvariantCulture);
            }
            semantics = ItemTypeService.ResolveValues(item.Document, item.RowIndex, type, effect);
        }
        var actual = semantics?.Category ?? ItemCategory.Unknown;
        if (item.Document != null && actual == expected) return;
        var required = expected switch { ItemCategory.Weapon => "武器", ItemCategory.Armor => "防具", _ => "辅助装备" };
        throw new InvalidDataException($"{fieldName}只能选择物品类型为{required}的宝物或 255（无装备）；物品 {itemId} 的当前类别为" +
            (semantics == null ? "未定义。" : $"{semantics.CategoryName}。"));
    }

    private static IEnumerable<(TableDocument Document, int RowIndex, DataRow Row, int Id)> ItemRows(
        IReadOnlyDictionary<string, TableDocument> documents)
    {
        foreach (var key in ItemTableKeys)
        {
            if (!documents.TryGetValue(key, out var document)) continue;
            for (var index = 0; index < document.Data.Rows.Count; index++)
            {
                var row = document.Data.Rows[index];
                if (row.RowState == DataRowState.Deleted) continue;
                yield return (document, index, row, Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture));
            }
        }
    }
}
