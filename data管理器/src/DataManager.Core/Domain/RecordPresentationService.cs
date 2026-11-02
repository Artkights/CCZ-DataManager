using System.Data;
using System.Globalization;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

/// <summary>A read-only snapshot of one real record, independent of a filtered display position.</summary>
public sealed record RecordPresentation(int Id, string Name, string SearchText)
{
    public string Display => $"{Id.ToString(CultureInfo.InvariantCulture)}：{(string.IsNullOrWhiteSpace(Name) ? "未命名" : Name)}";

    public bool Matches(string? query) => string.IsNullOrWhiteSpace(query) ||
        Display.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resolves the ID/name labels displayed by navigation using each table's actual row semantics.</summary>
public sealed class RecordPresentationService(ProjectWorkspace workspace)
{
    private static readonly string[] Attributes =
        ["移动声音", "移动速度", "攻击声音", "远程兵种", "攻击延迟", "兵种类型", "策略伤害", "参与围攻"];
    private static readonly string[] Terrains =
        ["平原", "草原", "树林", "荒地", "山地", "岩山", "山崖", "雪原", "桥梁", "浅滩",
         "沼泽", "池塘", "小河", "大河", "栅栏", "城墙", "城内", "城门", "城池", "关隘",
         "鹿砦", "村庄", "兵营", "民居", "宝物库", "水池", "火", "船", "祭坛", "地下"];
    public static IReadOnlyList<string> TerrainNames { get; } = Array.AsReadOnly(Terrains);
    private readonly Dictionary<string, Dictionary<int, DataRow>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, string>? _growthNames;

    public IReadOnlyList<RecordPresentation> GetTableRecords(TableDocument document)
        => Rows(document).Select(row => Present(document, row)).OrderBy(record => record.Id).ToArray();

    public IReadOnlyList<RecordPresentation> GetBusinessRecords(BusinessPageKind kind)
    {
        var primaryKeys = kind switch
        {
            BusinessPageKind.Persons => new[] { "persons" },
            BusinessPageKind.JobSeries => ["job-series"],
            BusinessPageKind.DetailedJobs => ["detailed-jobs"],
            BusinessPageKind.Items => ["items-low", "items-high", "items-extended"],
            BusinessPageKind.Strategies => ["strategies"],
            BusinessPageKind.Shop => ["shops"],
            BusinessPageKind.ExtendData => ["effect-archive"],
            _ => Array.Empty<string>()
        };
        var primary = primaryKeys.Where(workspace.Documents.ContainsKey).ToArray();
        // A missing main table must not make a matrix or shared text pool look like an entity list.
        if (primary.Length == 0) return [];
        return primary.Select(workspace.GetDocument).SelectMany(GetTableRecords)
            .DistinctBy(record => record.Id).OrderBy(record => record.Id).ToArray();
    }

    /// <summary>Unlike ParentKey, this also distinguishes transposed matrices and shared quote slots.</summary>
    public string GetTableRowName(TableDocument document, int id)
        => RowName(document, id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string RowName(TableDocument document, int id, HashSet<string> visiting)
    {
        var key = document.Definition.Key;
        if (!visiting.Add(key) || FindRow(document, id) is not { } row) return string.Empty;
        if (key == "job-series-attributes") return At(Attributes, id, "属性");
        if (key == "strategy-terrain-availability") return At(Terrains, id, "地形");
        if (key == "person-critical-quotes")
        {
            var summary = QuoteSummary(row);
            if (id >= 21) return $"普通暴击类型 {(id - 21) / 3} · 第 {(id - 21) % 3 + 1} 句" + summary;
            var personId = FindRow("person-critical-special-map", id) is { } map ? Integer(map, "人物ID") : null;
            var personName = personId is >= 0 and < 1024 ? NameIn("persons", personId.Value, visiting) : string.Empty;
            return $"特殊暴击槽 {id}" + (personName.Length == 0 ? string.Empty : $" · {personName}") + summary;
        }
        if (key == "person-critical-special-map")
        {
            var person = Integer(row, "人物ID");
            return person == 1024 ? "空槽" : person.HasValue ? NameIn("persons", person.Value, visiting) : string.Empty;
        }
        if (key == "person-retreat-quotes") return NameIn("persons", id, visiting) + QuoteSummary(row);
        if (key is "person-r-assign" or "person-s-assign" or "person-r-images" or "person-s-images" or
            "effect-archive" or "strategy-study-extended")
            return NameIn("persons", id, visiting);
        if (key == "shops") return NameIn("scenario-names", id, visiting);
        if (key is "strategy-big-animation" or "strategy-small-animation") return NameIn("strategies", id, visiting);
        if (key == "effect-messages")
        {
            _growthNames ??= new ExtendDataService(workspace).GetEntityOptions(ExtendDataCategory.Growth)
                .ToDictionary(option => option.Id, option => option.Name);
            return _growthNames.GetValueOrDefault(id, "未知特技");
        }
        if (key == "job-series-accessory-groups") return "辅助装备分组数据";
        if (key == EquipmentTypeCatalogService.DisplayTableKey)
            return new EquipmentTypeCatalogService().Read(workspace).FirstOrDefault(type => type.GroupIndex == id)?.Name ?? "装备类型显示槽";
        if (key.StartsWith("item-effect-names", StringComparison.Ordinal)) return "宝物特效名称池";
        // Only physical name fields take precedence. A previously derived 名称 can describe the wrong axis.
        var nameField = document.Definition.Fields.FirstOrDefault(field => field.Name is "名称" or "战役名称" or "策略名称");
        if (nameField != null && row.Table.Columns.Contains(nameField.Name)) return Text(row[nameField.Name]);
        if (document.Definition.ParentKey is { Length: > 0 } parent) return NameIn(parent, id, visiting);
        return "记录";
    }

    private string NameIn(string key, int id, HashSet<string>? visiting = null)
        => workspace.Documents.TryGetValue(key, out var document)
            ? RowName(document, id, visiting ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)) : string.Empty;

    private RecordPresentation Present(TableDocument document, DataRow row)
    {
        var id = Id(row);
        var name = GetTableRowName(document, id);
        var record = new RecordPresentation(id, name, string.Empty);
        return record with { SearchText = record.Display };
    }

    private DataRow? FindRow(string key, int id)
        => workspace.Documents.TryGetValue(key, out var document) ? FindRow(document, id) : null;

    private DataRow? FindRow(TableDocument document, int id)
    {
        if (!_rows.TryGetValue(document.Definition.Key, out var rows))
            _rows[document.Definition.Key] = rows = Rows(document).GroupBy(Id).ToDictionary(group => group.Key, group => group.First());
        return rows.GetValueOrDefault(id);
    }

    private static IEnumerable<DataRow> Rows(TableDocument document)
        => document.Data.Columns.Contains("ID") ? document.Data.Rows.Cast<DataRow>().Where(row => row.RowState != DataRowState.Deleted) : [];
    private static int Id(DataRow row) => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
    private static string Text(object? value) => value == DBNull.Value ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static int? Integer(DataRow row, string column) => row.Table.Columns.Contains(column) &&
        int.TryParse(Text(row[column]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string At(string[] names, int id, string fallback) => (uint)id < (uint)names.Length ? names[id] : $"{fallback} {id}";
    private static string QuoteSummary(DataRow row)
    {
        if (!row.Table.Columns.Contains("台词")) return string.Empty;
        var text = Text(row["台词"]).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length == 0 ? " · 空台词" : " · " + (text.Length <= 32 ? text : text[..32] + "…");
    }
}
