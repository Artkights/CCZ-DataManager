using System.Data;
using System.Globalization;
using DataManager.Core.Editing;

namespace DataManager.Core.Domain;

public static class SetEquipmentValidator
{
    public static ItemCategory? CategoryForField(string tableKey, string fieldName)
        => tableKey == "personal-effects" ? fieldName switch
        {
            "装备3-1" or "装备4-1" => ItemCategory.Weapon,
            "装备3-2" or "装备4-2" => ItemCategory.Armor,
            "装备3-3" or "装备4-3" => ItemCategory.Accessory,
            _ => null
        } : null;

    public static IReadOnlyList<int> GetAllowedIds(ProjectWorkspace workspace, ItemCategory category)
    {
        var boundary = ItemCategoryBoundary.Read(workspace.RootPath);
        return new[] { "items-low", "items-high", "items-extended" }.Where(workspace.Documents.ContainsKey)
            .SelectMany(key =>
            {
                var document = workspace.GetDocument(key);
                return document.Data.Rows.Cast<DataRow>().Select((row, index) => (Row: row, Index: index, Document: document));
            })
            .Where(item => Convert.ToInt32(item.Row["ID"], CultureInfo.InvariantCulture) is >= 0 and < 255 &&
                ItemTypeService.Resolve(item.Document, item.Index, boundary).Category == category)
            .Select(item => Convert.ToInt32(item.Row["ID"], CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
    }

    public static void Validate(ProjectWorkspace workspace, string tableKey, string fieldName, int id)
    {
        var category = CategoryForField(tableKey, fieldName);
        if (category == null || id == 255) return;
        if (!GetAllowedIds(workspace, category.Value).Contains(id))
        {
            var label = category switch { ItemCategory.Weapon => "武器", ItemCategory.Armor => "防具", _ => "辅助装备" };
            throw new InvalidDataException($"{fieldName} 只能选择{label}（0..254）或空槽 255；物品 {id} 不符合该槽类别。");
        }
    }
}
