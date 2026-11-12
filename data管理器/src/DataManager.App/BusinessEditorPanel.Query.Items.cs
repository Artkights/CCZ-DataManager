using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private sealed record QueryItem(TableDocument Document, int RowIndex, long Revision, ItemSemanticSnapshot Snapshot);
    private readonly Dictionary<int, QueryItem> _queryItems = [];

    private static bool IsItemSemanticQueryField(string tableKey, string fieldName)
        => tableKey is "items-low" or "items-high" or "items-extended" &&
           fieldName is "宝物类型" or "装备特效号" or "初始能力" or "装备特效值" or "能力成长";

    private QueryItem? ReadQueryItem(int id)
    {
        foreach (var key in QueryTableKeys("items-low"))
        {
            if (!_workspace.Documents.TryGetValue(key, out var document) || FindRow(document, id) is not { } row) continue;
            if (_queryItems.TryGetValue(id, out var cached) && ReferenceEquals(document, cached.Document) &&
                row.Index == cached.RowIndex && document.Revision == cached.Revision) return cached;
            var value = new QueryItem(document, row.Index, document.Revision, ItemSemanticResolver.Resolve(document, row.Index));
            _queryItems[id] = value;
            return value;
        }
        _queryItems.Remove(id);
        return null;
    }

    private void DeclareItemSemanticQueryFields()
    {
        var documents = QueryTableKeys("items-low").Where(_workspace.Documents.ContainsKey).Select(_workspace.GetDocument).ToArray();
        var categories = documents.Where(document => document.Data.Columns.Contains("宝物类型") && document.Data.Columns.Contains("装备特效号"))
            .SelectMany(document => Enumerable.Range(0, document.Data.Rows.Count).Select(index => ItemTypeService.Resolve(document, index).Category)).ToHashSet();
        var hasEquipment = categories.Contains(ItemCategory.Weapon) || categories.Contains(ItemCategory.Armor);
        var hasAccessory = categories.Contains(ItemCategory.Accessory);
        var hasConsumable = categories.Contains(ItemCategory.Consumable);
        bool Has(string name) => documents.Any(document => document.Data.Rows.Count > 0 && document.Data.Columns.Contains(name) &&
            document.Definition.Fields.Any(field => field.Name == name && field.Kind != FieldKind.RawBytes));
        void Add(string key, string label, Func<QueryItem, IReadOnlyList<string>> read)
            => DeclareQueryField(new RecordQueryField("item:" + key, label, id => ReadQueryItem(id) is { } item ? read(item) : []));
        static IReadOnlyList<string> Number(int? number) => number.HasValue ? [number.Value.ToString(CultureInfo.InvariantCulture)] : [];
        if (Has("宝物类型") && Has("装备特效号"))
        {
            Add("category", "物品类别", item => item.Snapshot.Category switch
            {
                ItemCategory.Weapon => ["0", item.Snapshot.Semantics.CategoryName],
                ItemCategory.Armor => ["1", item.Snapshot.Semantics.CategoryName],
                ItemCategory.Accessory => ["2", item.Snapshot.Semantics.CategoryName],
                ItemCategory.Consumable => ["3", item.Snapshot.Semantics.CategoryName],
                _ => [item.Snapshot.Semantics.CategoryName]
            });
            if (hasEquipment) Add("type", "宝物类型", item =>
            {
                if (item.Snapshot.Category is not (ItemCategory.Weapon or ItemCategory.Armor)) return [];
                var number = item.Snapshot.TypeValue;
                return [number.ToString(CultureInfo.InvariantCulture),
                    new EquipmentTypeCatalogService().GetTypeDisplayName(_workspace, number)];
            });
            Add("effect-number", "实际特效号", item =>
            {
                var number = item.Snapshot.ActualEffectNumber;
                var sourceName = item.Snapshot.Semantics.IsCategoryMarker ||
                    item.Snapshot.Category is ItemCategory.Weapon or ItemCategory.Armor ? "装备特效号" : "宝物类型";
                var field = item.Document.Definition.Fields.FirstOrDefault(candidate => candidate.Name == sourceName);
                var option = field == null ? null : GetOptions(item.Document, item.RowIndex, field)?.FirstOrDefault(candidate => candidate.Id == number);
                return option == null ? [number.ToString(CultureInfo.InvariantCulture)] : [number.ToString(CultureInfo.InvariantCulture), option.Name];
            });
        }
        if (Has("初始能力") && hasEquipment) Add("initial", "初始属性", item => Number(item.Snapshot.InitialAttribute));
        if ((Has("初始能力") && hasAccessory) || (Has("装备特效值") && hasEquipment))
            Add("effect-value", "特殊效果值", item => Number(item.Snapshot.EffectValue));
        if (Has("能力成长"))
        {
            if (hasEquipment) Add("growth", "能力成长", item => Number(item.Snapshot.GrowthValue));
            if (hasAccessory) Add("suitable-job", "适用兵种", item => item.Snapshot.SuitableJob is { } job
                ? [job.ToString(CultureInfo.InvariantCulture), job == 255 ? "所有兵种" : NameOf("job-series", job)] : []);
        }
        if (Has("初始能力") && hasConsumable) Add("parameter-low", "效果参数（低字节）", item =>
            item.Snapshot.Category == ItemCategory.Consumable ? ReadItemParameter(item, false) : []);
        if (Has("装备特效值") && hasConsumable) Add("parameter-high", "效果参数（高字节）", item =>
            item.Snapshot.Category == ItemCategory.Consumable ? ReadItemParameter(item, true) : []);
    }

    private static IReadOnlyList<string> ReadItemParameter(QueryItem item, bool high)
    {
        var offset = high ? 0x16 : 0x15;
        var hex = item.Snapshot.RawSummary;
        return hex.Length >= offset * 2 + 2 && byte.TryParse(hex.AsSpan(offset * 2, 2), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var number) ? [number.ToString(CultureInfo.InvariantCulture)] : [];
    }
}
