using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class ExtendDataEditorPanel
{
    private bool _queryResultAvailable = true;
    private readonly Dictionary<Control, bool> _queryContentEnabled = [];
    public event EventHandler? QueryScopeChanged;
    public string CurrentQueryScopeKey => $"extend:{Category}";

    private readonly Dictionary<ExtendDataCategory, (string Version, RecordQueryScope Scope)> _queryScopes = [];
    private readonly Dictionary<ExtendDataCategory,
        (IReadOnlyList<IdNameOption> Source, IReadOnlyList<RecordPresentation> Records)> _queryNavigation = [];

    public IReadOnlyList<RecordPresentation> GetQueryNavigationRecords()
    {
        var options = GetEntityOptions();
        if (_queryNavigation.TryGetValue(Category, out var cached) && ReferenceEquals(options, cached.Source))
            return cached.Records;
        // Stable identity lets navigation measurements survive a visit to another category.
        // A changed source directory replaces only this category, including renamed records.
        var records = Array.AsReadOnly(options.Select(option => new RecordPresentation(option.Id, option.Name, string.Empty)).ToArray());
        _queryNavigation[Category] = (options, records);
        return records;
    }

    public RecordQueryScope CurrentQueryScope
    {
        get
        {
            var category = Category;
            var key = ExtendDataService.TableKey(category);
            var cache = WorkspaceReadCache.For(_workspace);
            var dependencies = category switch
            {
                ExtendDataCategory.Effects => new[] { new RecordQueryDependency("$external") },
                ExtendDataCategory.Strategies => cache.GetNavigationDependencies(["strategies"]).ToArray(),
                ExtendDataCategory.Forge => cache.GetNavigationDependencies(["items-low", "items-high", "items-extended"]).ToArray(),
                _ => Array.Empty<RecordQueryDependency>()
            };
            var version = cache.GetSchemaVersion([new(key)]) + "|" + cache.GetVersion(dependencies);
            if (_queryScopes.TryGetValue(category, out var cached) && cached.Version == version) return cached.Scope;
            var service = new RecordQueryService(_workspace);
            var fields = new List<RecordQueryField> { service.IdentityField() };
            if (_workspace.Documents.TryGetValue(key, out var document))
            {
                var referenceNames = category switch
                {
                    ExtendDataCategory.Effects => _service.GetEffectOptions().ToDictionary(option => option.Id, option => option.Name),
                    ExtendDataCategory.Strategies => _service.GetStrategyOptions().ToDictionary(option => option.Id, option => option.Name),
                    ExtendDataCategory.Forge => _service.GetItemOptions().ToDictionary(option => option.Id, option => option.Name),
                    _ => new Dictionary<int, string>()
                };
                foreach (var definition in document.Definition.Fields.Where(field => field.Kind != FieldKind.RawBytes && field.Policy != FieldPolicy.Preserve))
                {
                    var name = definition.Name;
                    var physical = service.PhysicalField([key], name, ExtendQueryLabel(category, name));
                    if (physical == null) continue;
                    fields.Add(physical with
                    {
                        ReadValues = id => ReadExtendQueryValues(physical, category, name, id, referenceNames),
                        Dependencies = (physical.Dependencies ?? []).Concat(dependencies).Distinct().ToArray()
                    });
                }
            }
            var scope = new RecordQueryScope(CurrentQueryScopeKey, fields)
            { Context = new(RecordQueryContextKind.ExtendData, CurrentQueryScopeKey,
                BusinessPage: BusinessPageKind.ExtendData, TableKey: key, ExtendCategory: category.ToString()) };
            _queryScopes[category] = (version, scope);
            return scope;
        }
    }

    public void SetQueryResultAvailable(bool available)
    {
        _queryResultAvailable = available;
        ApplyQueryAvailability();
    }

    private void ApplyQueryAvailability()
    {
        foreach (TabPage page in _tabs.TabPages)
            foreach (Control content in page.Controls)
            {
                if (!_queryResultAvailable)
                {
                    if (!_queryContentEnabled.ContainsKey(content))
                    {
                        _queryContentEnabled[content] = content.Enabled;
                        content.Disposed += (_, _) => _queryContentEnabled.Remove(content);
                    }
                    content.Enabled = false;
                }
                else if (_queryContentEnabled.Remove(content, out var enabled)) content.Enabled = enabled;
            }
    }

    private static string ExtendQueryLabel(ExtendDataCategory category, string name)
        => category == ExtendDataCategory.Growth ? name switch
        {
            "Enabled" => "允许成长", "Param1" => "吃书初始效果值", "Param2" => "效果值成长上限",
            "Param3" => "临时书持续回合", "Param4" => "成长书经验加成", "Param5" => "攻击经验",
            "Param6" => "受击经验", "Param7" => "击杀经验", _ => name
        } : category == ExtendDataCategory.Forge && name == "标记" ? "启用" : name;

    private static IReadOnlyList<string> ReadExtendQueryValues(RecordQueryField physical, ExtendDataCategory category, string name, int id,
        IReadOnlyDictionary<int, string> referenceNames)
    {
        var values = new List<string>(physical.ReadValues(id));
        if (values.Count > 0 && int.TryParse(values[0], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            var isReference = category switch
            {
                ExtendDataCategory.Effects => name.EndsWith("特效号", StringComparison.Ordinal),
                ExtendDataCategory.Strategies => name.EndsWith("ID", StringComparison.Ordinal),
                ExtendDataCategory.Forge => name.StartsWith("材料", StringComparison.Ordinal) && name.EndsWith("ID", StringComparison.Ordinal),
                _ => false
            };
            if (isReference && referenceNames.TryGetValue(number, out var found)) values.Add(found);
            if (category == ExtendDataCategory.Growth && name == "Enabled" || category == ExtendDataCategory.Forge && name == "标记")
                values.Add(number == 0 ? "0 否 未启用" : "1 是 已启用");
        }
        return values;
    }
}
