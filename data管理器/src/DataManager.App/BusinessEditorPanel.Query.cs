using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private readonly Dictionary<TabPage, List<RecordQueryField>> _queryPages = [];
    private readonly Dictionary<BusinessPageKind, string> _rememberedQueryPages = [];
    private TabPage? _queryBuildPage;
    private bool _queryResultAvailable = true;
    private readonly Dictionary<Control, bool> _queryContentEnabled = [];
    private string? _notifiedQueryScope;

    public event EventHandler? QueryScopeChanged;
    public string CurrentQueryScopeKey => _kind == BusinessPageKind.ExtendData && _extendEditor != null
        ? _extendEditor.CurrentQueryScopeKey : _tabs.SelectedTab?.Name ?? $"business:{_kind}:identity";

    /// <summary>The schema is declared while the detail page is built; value readers operate on workspace rows.</summary>
    public RecordQueryScope CurrentQueryScope
    {
        get
        {
            if (_kind == BusinessPageKind.ExtendData && _extendEditor != null) return _extendEditor.CurrentQueryScope;
            var page = _tabs.SelectedTab;
            var fields = page != null && _queryPages.TryGetValue(page, out var declared) ? declared : [];
            return new RecordQueryScope(CurrentQueryScopeKey,
                new[] { new RecordQueryService(_workspace).IdentityField() }.Concat(fields).ToArray())
            { Context = new(RecordQueryContextKind.Business, CurrentQueryScopeKey, BusinessPage: _kind) };
        }
    }

    private void DeclareQueryPage(TabPage page, string stableKey)
    {
        page.Name = $"business:{_kind}:{stableKey}";
        _queryPages[page] = [];
        _queryBuildPage = page;
        page.Disposed += (_, _) => _queryPages.Remove(page);
    }

    private void RememberQueryPage()
    {
        if (_tabs.SelectedTab is { } selected && !string.IsNullOrEmpty(selected.Name))
            _rememberedQueryPages[_kind] = selected.Name;
    }

    private void RestoreQueryPage()
    {
        if (_rememberedQueryPages.TryGetValue(_kind, out var key) &&
            _tabs.TabPages.Cast<TabPage>().FirstOrDefault(page => page.Name == key) is { } selected)
            _tabs.SelectedTab = selected;
        ApplyQueryAvailability();
    }

    private void NotifyQueryScopeChanged()
    {
        if (_binding || _disposingDetailPages || _releaseCompleted) return;
        RememberQueryPage();
        var key = CurrentQueryScopeKey;
        if (_notifiedQueryScope == key) return;
        _notifiedQueryScope = key;
        QueryScopeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetQueryResultAvailable(bool available)
    {
        _queryResultAvailable = available;
        ApplyQueryAvailability();
    }

    private void ApplyQueryAvailability()
    {
        if (_kind == BusinessPageKind.ExtendData && _extendEditor != null)
        { _extendEditor.SetQueryResultAvailable(_queryResultAvailable); return; }
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

    internal bool SelectQueryScopeForSmokeTest(string key)
    {
        var tab = DetailTabs.TabPages.Cast<TabPage>().FirstOrDefault(page => page.Name == key || page.Text == key);
        if (tab == null) return false;
        DetailTabs.SelectedTab = tab;
        return ReferenceEquals(DetailTabs.SelectedTab, tab);
    }

    private void DeclareQueryField(RecordQueryField field)
    {
        if (_queryBuildPage == null || !_queryPages.TryGetValue(_queryBuildPage, out var fields)) return;
        var index = fields.FindIndex(existing => existing.Key == field.Key);
        if (index >= 0) fields[index] = field; else fields.Add(field);
    }

    private static IReadOnlyList<string> QueryTableKeys(string key) => key switch
    {
        "items-low" or "items-high" or "items-extended" => ["items-low", "items-high", "items-extended"],
        "item-description-low" or "item-description-high" or "item-description-extended" =>
            ["item-description-low", "item-description-high", "item-description-extended"],
        _ => [key]
    };

    private void DeclareQueryBinding(TableDocument document, int rowIndex, TableFieldDefinition definition, string? label)
    {
        var tableKey = document.Definition.Key;
        if (definition.Kind == FieldKind.RawBytes || document.Data.Rows.Count == 0 ||
            !document.Data.Columns.Contains(definition.Name) && definition.Policy != FieldPolicy.Preserve ||
            _queryBuildPage?.Text == "原始字段" || tableKey is "strategy-terrain-availability" or
            "job-series-accessory-groups" or "equipment-type-names" or "equipment-type-display" ||
            tableKey.StartsWith("item-effect-names", StringComparison.Ordinal)) return;
        label ??= ItemFieldLabel(document, rowIndex, definition.Name) ?? FieldLabel(tableKey, definition.Name);
        if (tableKey == "person-r-assign") label = "R形象编号";
        if (tableKey == "person-s-assign") label = "S形象编号";
        if (tableKey == "job-series-attributes")
        {
            if (!document.Data.Columns.Contains(definition.Name)) return;
            var attributeIndex = rowIndex;
            DeclareQueryField(new RecordQueryField($"matrix:job-series-attributes:{attributeIndex}",
                attributeIndex < JobAttributeNames.Length ? JobAttributeNames[attributeIndex] : label,
                id => ReadMatrixQueryValues("job-series-attributes", attributeIndex, $"兵种系 {id}")));
            return;
        }
        if (_kind == BusinessPageKind.Items && tableKey == "detailed-job-growth") return;
        if (_kind == BusinessPageKind.Items && IsItemSemanticQueryField(tableKey, definition.Name)) return;
        var keys = QueryTableKeys(tableKey);
        var fieldName = definition.Name;
        var field = new RecordQueryService(_workspace).PhysicalField(keys, fieldName, label) ??
            new RecordQueryField($"field:{string.Join("|", keys)}:{fieldName}", label, id =>
            {
                foreach (var key in keys)
                {
                    if (!_workspace.Documents.TryGetValue(key, out var source) || FindRow(source, id) is not { } match ||
                        source.Definition.Fields.FirstOrDefault(candidate => candidate.Name == fieldName) is not { } sourceField) continue;
                    return [Convert.ToString(ReadFieldValue(source, match.Index, sourceField), CultureInfo.InvariantCulture) ?? string.Empty];
                }
                return [];
            });
        DeclareQueryField(field with { ReadValues = id => ReadBusinessQueryValues(field, keys, definition.Name, id) });
    }

    private IReadOnlyList<string> ReadBusinessQueryValues(RecordQueryField physical, IReadOnlyList<string> keys, string name, int id)
    {
        var values = new List<string>(physical.ReadValues(id));
        foreach (var key in keys)
        {
            if (!_workspace.Documents.TryGetValue(key, out var document) || FindRow(document, id) is not { } match ||
                document.Definition.Fields.FirstOrDefault(field => field.Name == name) is not { } definition ||
                !TryConvertInt(ReadFieldValue(document, match.Index, definition), out var number)) continue;
            if (key is "items-low" or "items-high" or "items-extended")
            {
                var category = ItemTypeService.Resolve(document, match.Index).Category;
                if (category == ItemCategory.Accessory && name == "装备特效值" ||
                    category == ItemCategory.Consumable && name == "能力成长") return [];
            }
            if (GetFlags(key, name) is { } flags)
                values.AddRange(flags.Where(flag => (number & (1 << flag.Bit)) != 0).Select(flag => flag.Name));
            if (EquipmentColumns.Contains(name)) values.Add(number == 0 ? "否 未勾选" : "是 已勾选");
            if (key == "strategies" && name == "策略类型" && StrategyTypeName(number) is { } typeName) values.Add(typeName);
            else if (IsE5RangeField(key, name)) values.Add(RangeName(name is "攻击范围" or "施法范围", number));
            else if (IsNativeRangeField(key, name)) values.Add(RangeName(false, number));
            else
            {
                try
                {
                    var option = GetOptions(document, match.Index, definition)?.FirstOrDefault(candidate => candidate.Id == number);
                    if (option != null) values.Add(option.Name);
                }
                catch (InvalidOperationException) { }
            }
            if (key == "strategies" && IsLearningField(name) && number > 0 &&
                int.TryParse(new string(name.Where(char.IsDigit).ToArray()), out var jobId))
                values.Add($"{jobId}：{NameOf("detailed-jobs", jobId)}");
        }
        return values;
    }

    private IReadOnlyList<string> ReadMatrixQueryValues(string tableKey, int rowIndex, string fieldName)
    {
        if (!_workspace.Documents.TryGetValue(tableKey, out var document) || rowIndex >= document.Data.Rows.Count ||
            !document.Data.Columns.Contains(fieldName)) return [];
        var value = Convert.ToString(document.Data.Rows[rowIndex][fieldName], CultureInfo.InvariantCulture) ?? string.Empty;
        var field = document.Definition.Fields.FirstOrDefault(candidate => candidate.Name == fieldName);
        if (field == null || field.Kind == FieldKind.RawBytes) return [];
        var option = field != null && TryConvertInt(document.Data.Rows[rowIndex][fieldName], out var number)
            ? JobAttributeOptions(document, rowIndex, field)?.FirstOrDefault(candidate => candidate.Id == number) : null;
        return option == null ? [value] : [value, option.Name];
    }

    private void DeclareItemQueryFields()
    {
        DeclareItemSemanticQueryFields();
        foreach (var key in QueryTableKeys("items-low"))
        {
            if (!_workspace.Documents.TryGetValue(key, out var document) || document.Data.Rows.Count == 0) continue;
            var rowIndex = FindRow(document, _id)?.Index ?? 0;
            foreach (var field in document.Definition.Fields.Where(field => field.Kind != FieldKind.RawBytes && field.Policy != FieldPolicy.Preserve))
                DeclareQueryBinding(document, rowIndex, field, field.Name);
        }
        foreach (var key in QueryTableKeys("item-description-low"))
        {
            if (!_workspace.Documents.TryGetValue(key, out var document) || document.Data.Rows.Count == 0) continue;
            foreach (var field in document.Definition.Fields.Where(field => field.Kind != FieldKind.RawBytes && field.Policy != FieldPolicy.Preserve))
                DeclareQueryBinding(document, FindRow(document, _id)?.Index ?? 0, field, "介绍");
        }
    }

    private void DeclarePersonQueryFields()
    {
        foreach (var key in new[] { "persons", "person-r-assign", "person-s-assign", "person-biography" })
        {
            if (!_workspace.Documents.TryGetValue(key, out var document) || document.Data.Rows.Count == 0) continue;
            foreach (var field in document.Definition.Fields)
            {
                if (key == "persons" && field.Name is "Army" or "暴击台词" or "撤退台词" or "级别" or "经验") continue;
                DeclareQueryBinding(document, FindRow(document, _id)?.Index ?? 0, field, key == "person-biography" ? "列传" : null);
            }
        }
    }

    private void DeclareJobSeriesMatrixQueryFields()
    {
        if (!_workspace.Documents.TryGetValue("job-series-attributes", out var document) ||
            !document.Definition.Fields.Any(field => field.Name.StartsWith("兵种系 ", StringComparison.Ordinal) &&
                field.Kind != FieldKind.RawBytes && document.Data.Columns.Contains(field.Name))) return;
        for (var index = 0; index < Math.Min(JobAttributeNames.Length, document.Data.Rows.Count); index++)
        {
            var rowIndex = index;
            DeclareQueryField(new RecordQueryField($"matrix:job-series-attributes:{index}", JobAttributeNames[index],
                id => ReadMatrixQueryValues("job-series-attributes", rowIndex, $"兵种系 {id}")));
        }
    }

    private void DeclareQuoteQueryFields()
    {
        if (!_workspace.Documents.TryGetValue("persons", out var persons) || !persons.Data.Columns.Contains("暴击台词")) return;
        var service = new PersonQuoteService(_workspace);
        void Add(string key, string label, Func<PersonQuoteMapping, IEnumerable<string>> read)
            => DeclareQueryField(new RecordQueryField("quote:" + key, label, id =>
            {
                try { return read(service.Resolve(id)).ToArray(); }
                catch (InvalidOperationException) { return []; }
                catch (InvalidDataException) { return []; }
                catch (KeyNotFoundException) { return []; }
            }));
        Add("mode", "暴击模式", mapping => [mapping.IsSpecial ? "1 特殊暴击" : "0 普通暴击"]);
        Add("type", "普通类型", mapping => [mapping.CriticalType.ToString(CultureInfo.InvariantCulture)]);
        if (_workspace.Documents.TryGetValue("person-critical-special-map", out var specialMap) && specialMap.Data.Columns.Contains("人物ID") &&
            _workspace.Documents.TryGetValue("person-critical-quotes", out var criticalTable) && criticalTable.Data.Columns.Contains("台词"))
            Add("special-slot", "特殊槽位", mapping => mapping.IsSpecial
                ? mapping.Critical.Select(quote => quote.Id.ToString(CultureInfo.InvariantCulture)) : []);
        if (_workspace.Documents.TryGetValue("person-critical-quotes", out var critical) && critical.Data.Columns.Contains("台词"))
        {
            Add("critical", "暴击台词", mapping => mapping.Critical.Select(quote => quote.Text));
            for (var index = 0; index < 3; index++)
            {
                var quoteIndex = index;
                Add($"critical:{index}", $"暴击 {index + 1}", mapping => mapping.Critical.Count > quoteIndex
                    ? [mapping.Critical[quoteIndex].Text] : []);
            }
        }
        if (_workspace.Documents.TryGetValue("person-retreat-quotes", out var retreat) && retreat.Data.Columns.Contains("台词"))
            Add("retreat", "撤退台词", mapping => [mapping.Retreat?.Text ?? string.Empty]);
    }

    private void DeclareJobLearningQueryFields()
    {
        if (!_workspace.Documents.TryGetValue("strategies", out var source) ||
            !source.Definition.Fields.Any(field => IsLearningField(field.Name) && source.Data.Columns.Contains(field.Name))) return;
        DeclareQueryField(new RecordQueryField("learning:strategies", "习得策略", id =>
        {
            if (!_workspace.Documents.TryGetValue("strategies", out var strategies)) return [];
            var fieldName = $"兵种{id:D2}学习等级";
            if (!strategies.Data.Columns.Contains(fieldName)) return [];
            return strategies.Data.Rows.Cast<DataRow>().Where(row => row.RowState != DataRowState.Deleted &&
                TryConvertInt(row[fieldName], out var level) && level > 0)
                .SelectMany(row => new[] { $"{row["ID"]}：{row["名称"]}", Convert.ToString(row[fieldName], CultureInfo.InvariantCulture) ?? string.Empty }).ToArray();
        }));
    }

    private void DeclareItemPermissionQueryFields()
    {
        if (!_workspace.Documents.TryGetValue("detailed-job-growth", out var growth)) return;
        foreach (DataRow row in growth.Data.Rows)
        {
            var jobId = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
            var label = $"{jobId}：{NameOf("detailed-jobs", jobId)}";
            DeclareQueryField(new RecordQueryField($"permission:job:{jobId}", label, itemId =>
            {
                foreach (var key in QueryTableKeys("items-low"))
                {
                    if (!_workspace.Documents.TryGetValue(key, out var items) || FindRow(items, itemId) is not { } item) continue;
                    var semantics = ItemTypeService.Resolve(items, item.Index);
                    if (semantics.PermissionColumn is not { } permission || !growth.Data.Columns.Contains(permission) ||
                        FindRow(growth, jobId) is not { } job) return [];
                    var allowed = TryConvertInt(job.Row[permission], out var number) && number != 0;
                    return [allowed ? "1 是 可装备" : "0 否 不可装备", $"{jobId}：{NameOf("detailed-jobs", jobId)}"];
                }
                return [];
            }));
        }
    }
}
