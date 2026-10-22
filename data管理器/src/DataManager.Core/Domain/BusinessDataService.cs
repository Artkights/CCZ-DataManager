using System.Data;
using System.Globalization;
using DataManager.Core.Csv;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

/// <summary>
/// Adapts the physical-table workspace to the business pages used by the
/// integrated editor.  It intentionally accepts optional tables: a 6.5
/// project may lack a 6.6-only table and the page still exposes the rows that
/// are present.  New physical tables can therefore be added to profiles
/// without changing this service.
/// </summary>
public sealed class BusinessDataService
{
    private static readonly IReadOnlyDictionary<BusinessPageKind, string[]> PageTables =
        new Dictionary<BusinessPageKind, string[]>
        {
            [BusinessPageKind.Persons] = ["persons", "person-biography", "person-critical-quotes", "person-retreat-quotes", "person-critical-special-map", "person-r-assign", "person-s-assign", "person-r-images", "person-s-images"],
            [BusinessPageKind.JobSeries] = ["job-series", "job-series-terrain", "job-series-move-cost", "job-series-counter", "job-series-attributes", "job-series-accessory-groups"],
            [BusinessPageKind.DetailedJobs] = ["detailed-jobs", "detailed-job-description", "detailed-job-growth", "detailed-job-penetration", "strategies"],
            [BusinessPageKind.Items] = ["items-low", "items-high", "items-extended", "item-description-low", "item-description-high", "item-description-extended", "item-effect-names-low", "item-effect-names-high", "item-effect-names-66", "detailed-job-growth", "equipment-type-names", "equipment-type-display"],
            [BusinessPageKind.Effects] = ["class-effects", "personal-effects", "effect-names", "effect-descriptions"],
            [BusinessPageKind.Shop] = ["scenario-names", "shops"],
            [BusinessPageKind.Strategies] = ["strategies", "strategy-description", "strategy-extension", "strategy-damage-type", "strategy-damage-ratio", "strategy-hit-rate", "strategy-learning", "strategy-battle-ai", "strategy-training-ai", "strategy-terrain-availability", "strategy-big-animation", "strategy-small-animation"],
            [BusinessPageKind.ExtendData] = ["effect-archive", "effect-messages", "strategy-study-extended", "forge"],
            [BusinessPageKind.PhysicalTables] = []
        };

    private readonly ProjectWorkspace _workspace;

    public BusinessDataService(ProjectWorkspace workspace)
        => _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public ProjectWorkspace Workspace => _workspace;

    public BusinessPage LoadPersons() => LoadPage(BusinessPageKind.Persons);
    public BusinessPage LoadJobSeries() => LoadPage(BusinessPageKind.JobSeries);
    public BusinessPage LoadDetailedJobs() => LoadPage(BusinessPageKind.DetailedJobs);
    public BusinessPage LoadItems() => LoadPage(BusinessPageKind.Items);
    public BusinessPage LoadEffects() => LoadPage(BusinessPageKind.Effects);
    public BusinessPage LoadShop() => LoadPage(BusinessPageKind.Shop);
    public BusinessPage LoadStrategies() => LoadPage(BusinessPageKind.Strategies);
    public BusinessPage LoadExtendData() => LoadPage(BusinessPageKind.ExtendData);

    public IReadOnlyList<string> GetAvailableTableKeys(BusinessPageKind kind)
    {
        if (kind == BusinessPageKind.PhysicalTables) return _workspace.Documents.Keys.OrderBy(key => key).ToArray();
        return PageTables.TryGetValue(kind, out var keys)
            ? keys.Where(key => _workspace.Documents.ContainsKey(key) || key == "job-series-accessory-groups" && CanReadAccessoryGroups()).ToArray()
            : [];
    }

    private bool CanReadAccessoryGroups()
    {
        try
        {
            var result = new AccessoryJobGroupService().Read(_workspace);
            return result.Groups.Count > 0 && !result.Diagnostics.Any(message => message.StartsWith("错误：", StringComparison.Ordinal));
        }
        catch { return false; }
    }

    public BusinessPage LoadPage(BusinessPageKind kind)
    {
        var tableKeys = GetAvailableTableKeys(kind);
        if (tableKeys.Count == 0) return new BusinessPage(kind, tableKeys, []);
        var physicalKeys = tableKeys.Where(_workspace.Documents.ContainsKey).ToArray();
        var primaryTables = physicalKeys.Where(key => IsPrimaryTableForPage(kind, key)).ToArray();
        var ids = (primaryTables.Length == 0 ? physicalKeys : primaryTables)
            .SelectMany(GetIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var records = ids.Select(id => BuildRecord(kind, id, tableKeys)).ToArray();
        return new BusinessPage(kind, tableKeys, records);
    }

    public IReadOnlyList<IdNameOption> GetIdNameOptions(BusinessPageKind kind)
    {
        if (kind == BusinessPageKind.ExtendData)
            return new ExtendDataService(_workspace).GetEntityOptions(ExtendDataCategory.Effects);
        var keys = GetAvailableTableKeys(kind).Where(_workspace.Documents.ContainsKey).ToArray();
        var primary = keys.Where(key => IsPrimaryTableForPage(kind, key)).ToArray();
        if (primary.Length == 0) primary = keys.Take(1).ToArray();
        var names = new Dictionary<int, string>();
        foreach (var key in primary.Concat(keys.Where(key => key is "scenario-names" or "effect-names")).Distinct())
        {
            var table = _workspace.GetDocument(key).Data;
            var nameColumn = table.Columns.Cast<DataColumn>().FirstOrDefault(column => IsNameField(column.ColumnName));
            foreach (DataRow row in table.Rows)
            {
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                if (!names.ContainsKey(id) && !primary.Contains(key)) continue;
                var name = nameColumn == null ? string.Empty : Convert.ToString(row[nameColumn], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!names.TryGetValue(id, out var current) || string.IsNullOrWhiteSpace(current)) names[id] = name;
            }
        }
        return names.OrderBy(pair => pair.Key).Select(pair => new IdNameOption(pair.Key, pair.Value)).ToArray();
    }

    public CompositePageTransaction BeginTransaction(BusinessPageKind kind, string? description = null)
        => new(_workspace, description ?? $"编辑{GetDisplayName(kind)}", GetAvailableTableKeys(kind).Where(_workspace.Documents.ContainsKey).ToArray());

    public IReadOnlyList<PageCsvExport> ExportPageCsv(BusinessPageKind kind, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("CSV 目录不能为空。", nameof(directory));
        var keys = GetAvailableTableKeys(kind).Where(_workspace.Documents.ContainsKey).ToArray();
        var duplicateName = keys.GroupBy(key => CsvDisplayNameRegistry.GetFileName(_workspace.Documents[key].Definition), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName != null) throw new InvalidDataException($"物理表的中文 CSV 文件名重复：{duplicateName.Key}；请先为表配置唯一显示名。");
        Directory.CreateDirectory(directory);
        var result = new List<PageCsvExport>();
        foreach (var key in keys)
        {
            var document = _workspace.Documents[key];
            var fileName = CsvDisplayNameRegistry.GetFileName(document.Definition);
            var path = Path.Combine(directory, fileName);
            _workspace.ExportCsv(document, path);
            result.Add(new PageCsvExport(key, path, CsvDisplayNameRegistry.GetTableDisplayName(document.Definition)));
        }
        return result;
    }

    public IReadOnlyList<CsvImportResult> ImportPageCsv(BusinessPageKind kind, IReadOnlyDictionary<string, string> files)
    {
        return ImportPageCsvAtomic(kind, files).Tables.Values.ToArray();
    }

    /// <summary>Imports all supplied page CSVs through one undoable transaction.</summary>
    public CsvBundleImportResult ImportPageCsvAtomic(BusinessPageKind kind, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var pageKeys = new HashSet<string>(GetAvailableTableKeys(kind).Where(_workspace.Documents.ContainsKey), StringComparer.OrdinalIgnoreCase);
        var unknown = files.Keys.Where(key => key == AccessoryJobGroupCsvService.TableKey).ToArray();
        if (unknown.Length > 0)
            throw new InvalidDataException($"CSV 包含不属于当前业务页的物理表：{string.Join("、", unknown)}。辅助分组 CSV 请在辅助分组子页导入，再校验并保存。");
        var definitions = pageKeys.Select(key => _workspace.Documents[key].Definition).ToArray();
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in files)
        {
            var key = CsvDisplayNameRegistry.ResolveTableKey(pair.Key, definitions);
            if (!selected.TryAdd(key, pair.Value)) throw new InvalidDataException($"CSV 重复指定同一物理表：{key}。");
        }
        return _workspace.ImportCsvBundle(selected);
    }

    public static IReadOnlyList<IdNameOption> FilterIdNameOptions(IEnumerable<IdNameOption> options, string? query, bool includeUnknown = true)
        => IdNameSearch.Filter(options, query, includeUnknown);

    private DomainRecord BuildRecord(BusinessPageKind kind, int id, IReadOnlyList<string> tableKeys)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var physical = new List<PhysicalFieldRef>();
        string? name = null;
        foreach (var key in tableKeys)
        {
            if ((kind == BusinessPageKind.DetailedJobs && key == "strategies") ||
                (kind == BusinessPageKind.Items && key is "detailed-job-growth" or "equipment-type-names" or "equipment-type-display")) continue;
            // These are lookup pools, global masks or transposed matrices;
            // their row IDs are not foreign keys to the selected entity.
            // Their dedicated editors resolve the actual association.
            if (key is "person-critical-quotes" or "person-retreat-quotes" or "person-critical-special-map" or "strategy-terrain-availability" or "job-series-attributes" or "job-series-accessory-groups" || key.StartsWith("item-effect-names", StringComparison.Ordinal)) continue;
            if (!_workspace.Documents.TryGetValue(key, out var document)) continue;
            var rowMatch = document.Data.Rows.Cast<DataRow>()
                .Select((candidate, index) => (candidate, index))
                .FirstOrDefault(item => Convert.ToInt32(item.candidate["ID"], CultureInfo.InvariantCulture) == id);
            if (rowMatch.candidate == null) continue;
            var rowIndex = rowMatch.index;
            var row = rowMatch.candidate;
            foreach (DataColumn column in document.Data.Columns)
            {
                if (column.ColumnName == "ID" || column.ExtendedProperties.Contains("Derived")) continue;
                var value = row[column] == DBNull.Value ? null : row[column];
                var fieldKey = column.ColumnName;
                if (values.ContainsKey(fieldKey))
                {
                    fieldKey = $"{key}.{column.ColumnName}";
                    var suffix = 2;
                    while (values.ContainsKey(fieldKey)) fieldKey = $"{key}.{column.ColumnName}#{suffix++}";
                }
                values[fieldKey] = value;
                var field = column.ExtendedProperties["Field"] as TableFieldDefinition;
                if (field != null) physical.Add(new PhysicalFieldRef(key, rowIndex, column.ColumnName, value));
                if (name == null && IsNameField(column.ColumnName) && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                    name = Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }
        return new DomainRecord(id, name ?? $"ID={id.ToString(CultureInfo.InvariantCulture)}", values, physical);
    }

    private int[] GetIds(string tableKey)
    {
        if (!_workspace.Documents.TryGetValue(tableKey, out var document)) return [];
        return document.Data.Rows.Cast<DataRow>().Select(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture)).ToArray();
    }

    private static bool IsPrimaryTable(string key)
        => key switch
        {
            "persons" or "job-series" or "detailed-jobs" or "strategies" or "items-low" or "items-high" or "items-extended" or "shops" or "class-effects" or "effect-archive" or "forge" => true,
            _ => false
        };

    private static bool IsPrimaryTableForPage(BusinessPageKind kind, string key) => kind switch
    {
        BusinessPageKind.Persons => key == "persons",
        BusinessPageKind.JobSeries => key == "job-series",
        BusinessPageKind.DetailedJobs => key == "detailed-jobs",
        BusinessPageKind.Items => key is "items-low" or "items-high" or "items-extended",
        BusinessPageKind.Effects => key == "class-effects",
        BusinessPageKind.Shop => key == "shops",
        BusinessPageKind.Strategies => key == "strategies",
        BusinessPageKind.ExtendData => key == "effect-archive",
        _ => IsPrimaryTable(key)
    };

    private static bool IsNameField(string name)
        => name.Contains("名称", StringComparison.OrdinalIgnoreCase) || name is "战役名称" or "策略名称";

    private static string GetDisplayName(BusinessPageKind kind) => kind switch
    {
        BusinessPageKind.Persons => "人物",
        BusinessPageKind.JobSeries => "兵种系",
        BusinessPageKind.DetailedJobs => "兵种",
        BusinessPageKind.Items => "宝物",
        BusinessPageKind.Effects => "特效",
        BusinessPageKind.Shop => "商店",
        BusinessPageKind.Strategies => "策略",
        BusinessPageKind.ExtendData => "拓展数据",
        _ => "物理表"
    };

}
