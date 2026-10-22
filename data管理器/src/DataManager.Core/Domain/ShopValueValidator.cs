using System.Data;
using System.Globalization;
using DataManager.Core.Editing;

namespace DataManager.Core.Domain;

public static class ShopValueValidator
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
        { "空闲", "空槽", "未使用", "保留", "FF-End", "End" };

    public static void ValidateItem(ProjectWorkspace workspace, int id)
    {
        if (id == 255) return;
        var name = new[] { "items-low", "items-high", "items-extended" }.Where(workspace.Documents.ContainsKey)
            .SelectMany(key => workspace.GetDocument(key).Data.Rows.Cast<DataRow>())
            .Where(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture) == id)
            .Select(row => Convert.ToString(row["名称"], CultureInfo.InvariantCulture)?.Trim()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name) || Placeholders.Contains(name))
            throw new InvalidDataException($"物品 {id} 为未定义或占位物品，不能加入商店；空槽使用 255。");
    }
}
