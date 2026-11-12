using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;
using System.Runtime.CompilerServices;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly TextBox[] _queryInputs = Enumerable.Range(1, 3).Select(index => new TextBox
        { Name = $"RecordQuery{index}", Width = 112, PlaceholderText = "关键词（空格分词）", CausesValidation = false }).ToArray();
    private readonly ComboBox[] _queryFields = Enumerable.Range(1, 3).Select(index => new ComboBox
        { Name = $"RecordQueryField{index}", Width = 142, DropDownStyle = ComboBoxStyle.DropDownList, CausesValidation = false }).ToArray();
    private readonly Button _queryButton = new() { Name = "ExecuteQuery", Text = "查询", AutoSize = true, CausesValidation = false };
    private readonly Button _clearQueryButton = new() { Name = "ClearQuery", Text = "清空", AutoSize = true, CausesValidation = false };
    private readonly Label _querySummary = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
    private readonly FlowLayoutPanel _queryGroup = new() { Name = "RecordQueryGroups", Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
    private readonly Label _emptyQueryLabel = new() { Name = "EmptyQueryResult", Text = "没有匹配记录，请切换详情子页、修改或清空查询条件。", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Bottom, Height = 32, BackColor = SystemColors.Window, Visible = false };
    private sealed record QueryFieldChoice(string? Key, string Display)
    {
        public override string ToString() => Display;
    }
    private sealed class QueryPageState : RecordQueryState
    {
        public int? SelectedId { get; set; }
    }
    private static RecordQueryCondition[] EmptyConditions() => Enumerable.Range(0, 3).Select(_ => new RecordQueryCondition(null, string.Empty)).ToArray();
    private readonly Dictionary<RecordQueryContext, QueryPageState> _pageQueries = new();
    private readonly HashSet<string> _sizedNavigationPages = new(StringComparer.Ordinal);
    private SplitContainer? _navigationSplit;
    private bool _refreshingQuery, _queryRefreshQueued, _bindingQueryInputs;
    private RecordQueryContext? _queryBoundPage;
    private bool _restoringHideEmpty, _appliedHideEmpty;
    private sealed class PhysicalQueryFilterState
    {
        public string? Applied { get; set; }
        public string External { get; set; } = string.Empty;
    }
    private readonly ConditionalWeakTable<DataTable, PhysicalQueryFilterState> _physicalQueryFilters = new();
    private sealed record QueryRecordSnapshot(string Version, RecordQuerySnapshot Snapshot);
    private readonly Dictionary<RecordQueryContext, QueryRecordSnapshot> _queryRecords = new();

    private RecordQueryScope CurrentQueryScope()
    {
        if (_businessKind is not null) return _businessEditor.CurrentQueryScope;
        var service = new RecordQueryService(_workspace);
        if (_currentDocument is not { } document) return new("none", [service.IdentityField()]);
        var fields = new List<RecordQueryField> { service.IdentityField() };
        foreach (var column in _grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible))
        {
            if (column.Tag is not ProjectedField && string.IsNullOrEmpty(column.DataPropertyName)) continue;
            var field = column.Tag is ProjectedField projected
                ? service.PhysicalField([projected.TableKey], projected.FieldName, column.HeaderText)
                : service.PhysicalField([document.Definition.Key], column.DataPropertyName, column.HeaderText);
            if (field == null || fields.Any(existing => existing.Key == field.Key)) continue;
            if (document.Definition.Fields.FirstOrDefault(definition => definition.Name == column.DataPropertyName) is { ReferenceTable.Length: > 0 } definition)
            {
                var physical = field;
                var names = GetReferenceChoices(document, definition).ToDictionary(choice => choice.Value, choice => choice.Display);
                field = physical with { ReadValues = id => physical.ReadValues(id).SelectMany(value =>
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && names.TryGetValue(number, out var display)
                        ? new[] { value, display } : new[] { value }).ToArray() };
            }
            fields.Add(field);
        }
        return new("table:" + document.Definition.Key, fields);
    }

    private RecordQueryContext? QueryPageKey => _businessKind is { } kind
        ? new(kind == BusinessPageKind.ExtendData ? RecordQueryContextKind.ExtendData : RecordQueryContextKind.Business,
            _businessEditor.CurrentQueryScopeKey, kind, ExtendCategory: _businessEditor.ExtendEditor?.Category.ToString())
        : _currentDocument is { } document
            ? new(RecordQueryContextKind.PhysicalTable, "table:" + document.Definition.Key, TableKey: document.Definition.Key) : null;

    private QueryPageState QueryState(RecordQueryContext key)
    {
        if (!_pageQueries.TryGetValue(key, out var state)) _pageQueries[key] = state = new();
        return state;
    }

    private Control BuildQuerySettingsStrip()
    {
        var host = new TableLayoutPanel { Name = "SettingsAndQuery", Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 2, 8, 2),
            BackColor = Color.FromArgb(245, 247, 250) };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        void Group(string caption, Control control, int width)
        {
            var group = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 8, 0) };
            group.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 4, 0) });
            control.Dock = DockStyle.None;
            control.Width = width;
            control.Margin = new Padding(0, 3, 0, 3);
            group.Controls.Add(control);
            settings.Controls.Add(group);
        }
        Group("版本", _profileSelector, 165);
        Group("布局", _variantSelector, 195);
        _hideEmpty.Margin = new Padding(0, 6, 10, 3);
        settings.Controls.Add(_hideEmpty);
        Group("快速定位", _quickLookup, 190);
        Group("表", _tableSelector, 285);
        _tableSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        host.Controls.Add(settings, 0, 0);
        host.Controls.Add(_queryGroup, 0, 1);
        for (var index = 0; index < 3; index++)
        {
            var pair = new FlowLayoutPanel { Name = $"RecordQueryPair{index + 1}", AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 8, 0) };
            _queryFields[index].Margin = new Padding(0, 3, 4, 3);
            _queryInputs[index].Margin = new Padding(0, 3, 0, 3);
            pair.Controls.Add(_queryFields[index]);
            pair.Controls.Add(_queryInputs[index]);
            _queryGroup.Controls.Add(pair);
            _shellTips.SetToolTip(_queryFields[index], "选择当前详情子页的属性；全部属性包含 ID：名称及该子页的所有记录属性。");
            _shellTips.SetToolTip(_queryInputs[index], "空格分词，框内各词和三组条件全部满足。按回车或点击查询后应用。");
            _queryFields[index].SelectedIndexChanged += (_, _) => RememberQueryInputs();
            _queryInputs[index].TextChanged += (_, _) => RememberQueryInputs();
            _queryInputs[index].KeyDown += (_, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.Handled = args.SuppressKeyPress = true;
                SubmitRecordQuery();
            };
        }
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.Add(_queryButton);
        actions.Controls.Add(_clearQueryButton);
        actions.Controls.Add(_querySummary);
        _queryGroup.Controls.Add(actions);
        _queryButton.Click += (_, _) => SubmitRecordQuery();
        _clearQueryButton.Click += (_, _) => ClearRecordQuery();
        return host;
    }

    private RecordQueryCondition[] ReadQueryInputs() => Enumerable.Range(0, 3).Select(index => new RecordQueryCondition(
        (_queryFields[index].SelectedItem as QueryFieldChoice)?.Key, _queryInputs[index].Text)).ToArray();

    private void RememberQueryInputs()
    {
        if (!_bindingQueryInputs && _queryBoundPage is { } key) QueryState(key).Inputs = ReadQueryInputs();
    }

    private bool SubmitRecordQuery()
    {
        if (QueryPageKey is not { } key) return false;
        var conditions = ReadQueryInputs();
        QueryState(key).Inputs = conditions;
        if (!PreparePendingEdits()) return false;
        QueryState(key).Applied = new(conditions.ToArray());
        RefreshQueryNavigation();
        return true;
    }

    private bool ClearRecordQuery()
    {
        if (QueryPageKey is not { } key || !PreparePendingEdits()) return false;
        ResetCurrentQuery(key);
        RefreshQueryNavigation();
        return true;
    }

    private void ResetCurrentQuery(RecordQueryContext key)
    {
        var state = QueryState(key);
        state.Inputs = EmptyConditions();
        state.Applied = new();
        _queryBoundPage = null;
    }

    private void BindQueryInputs(RecordQueryContext key, RecordQueryScope scope)
    {
        var state = QueryState(key);
        var available = scope.Fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var removed = false;
        for (var index = 0; index < 3; index++)
            if ((state.Inputs[index].FieldKey is { Length: > 0 } input && !available.Contains(input)) ||
                (state.Applied.Conditions[index].FieldKey is { Length: > 0 } applied && !available.Contains(applied)))
            {
                state.Inputs[index] = new(null, string.Empty);
                state.Applied = new(state.Applied.Conditions.Select((condition, ordinal) => ordinal == index ? new(null, string.Empty) : condition).ToArray());
                removed = true;
            }
        _bindingQueryInputs = true;
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var choices = new[] { new QueryFieldChoice(null, "全部属性") }
                    .Concat(scope.Fields.Select(field => new QueryFieldChoice(field.Key, field.DisplayName))).ToArray();
                var combo = _queryFields[index];
                if (!combo.Items.Cast<QueryFieldChoice>().SequenceEqual(choices))
                {
                    combo.Items.Clear();
                    combo.Items.AddRange(choices);
                }
                combo.DropDownWidth = Math.Max(combo.Width, Math.Min(800,
                    choices.Max(choice => TextRenderer.MeasureText(choice.Display, combo.Font).Width) + 32));
                combo.SelectedIndex = Array.FindIndex(choices, choice => choice.Key == state.Inputs[index].FieldKey);
                if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
                if (_queryInputs[index].Text != state.Inputs[index].Text) _queryInputs[index].Text = state.Inputs[index].Text;
            }
            _queryBoundPage = key;
        }
        finally { _bindingQueryInputs = false; }
        if (removed) SetStatus("当前基底已不提供选中的属性，已清除对应查询条件。", true);
    }

    private void ResetQueryNavigation()
    {
        _queryBoundPage = null;
        _pageQueries.Clear();
        _sizedNavigationPages.Clear();
        _bindingQueryInputs = true;
        try
        {
            foreach (var combo in _queryFields) combo.Items.Clear();
            foreach (var input in _queryInputs) input.Clear();
        }
        finally { _bindingQueryInputs = false; }
        _emptyQueryLabel.Visible = false;
        _querySummary.Text = string.Empty;
        _queryRecords.Clear();
    }

    private RecordQuerySnapshot CurrentQuerySnapshot(RecordQueryScope scope)
    {
        var page = QueryPageKey ?? scope.Context ?? new RecordQueryContext(RecordQueryContextKind.Business, scope.Key);
        var version = string.Join('|', _workspace.Documents.Values.Select(document =>
            $"{document.Definition.Key}:{RuntimeHelpers.GetHashCode(document)}:{document.Revision}")) + "|" + string.Join('|', scope.Fields.Select(field => field.Key));
        if (!string.IsNullOrWhiteSpace(_workspace.RootPath))
        {
            var exe = new FileInfo(DataManager.Core.Binary.ProjectFileLocator.Resolve(_workspace.RootPath, "Ekd5.exe"));
            version += $"|{exe.FullName}:{(exe.Exists ? exe.Length : -1)}:{(exe.Exists ? exe.LastWriteTimeUtc.Ticks : 0)}";
        }
        if (_queryRecords.TryGetValue(page, out var cached) && cached.Version == version) return cached.Snapshot;
        var service = new RecordPresentationService(_workspace);
        IReadOnlyList<RecordPresentation> records = _businessKind == BusinessPageKind.ExtendData && _businessEditor.ExtendEditor is { } extend
            ? extend.GetEntityOptions().Select(option => new RecordPresentation(option.Id, option.Name, string.Empty)).ToArray()
            : _businessKind is { } kind ? service.GetBusinessRecords(kind)
            : _currentDocument is { } document ? service.GetTableRecords(document) : [];
        var snapshot = new RecordQueryService(_workspace).BuildSnapshot(scope, records);
        _queryRecords[page] = new(version, snapshot);
        return snapshot;
    }

    private IReadOnlyList<RecordPresentation> CurrentQueryRecords() => CurrentQuerySnapshot(CurrentQueryScope()).Records;

    private void RefreshQueryNavigation(bool bindSelection = true)
    {
        if (_refreshingQuery || QueryPageKey is not { } pageKey) return;
        _refreshingQuery = true;
        var previousSelection = _selectingBusinessEntity;
        var previousGrid = _suppressGridEvents;
        _selectingBusinessEntity = _suppressGridEvents = true;
        try
        {
            var scope = CurrentQueryScope();
            var state = QueryState(pageKey);
            int? currentId = _queryBoundPage == pageKey ? (_entityList.SelectedItem as EntityChoice)?.Id ?? state.SelectedId : state.SelectedId;
            if (_businessKind is not null) currentId = _businessEditor.CurrentId;
            else if (_grid.CurrentRow?.DataBoundItem is DataRowView current) currentId = Convert.ToInt32(current["ID"], CultureInfo.InvariantCulture);
            BindQueryInputs(pageKey, scope);
            var snapshot = CurrentQuerySnapshot(scope);
            var records = snapshot.Records;
            IEnumerable<RecordPresentation> matches = snapshot.Filter(state.Applied.Conditions);
            if (_currentDocument is { } document)
            {
                var clauses = new List<string>();
                var filterState = _physicalQueryFilters.GetOrCreateValue(document.Data);
                var currentFilter = document.Data.DefaultView.RowFilter ?? string.Empty;
                if (filterState.Applied != currentFilter) filterState.External = currentFilter;
                if (filterState.External.Length > 0) clauses.Add("(" + filterState.External + ")");
                if (_hideEmpty.Checked) clauses.Add("(" + NonEmptyRowFilter(document.Data) + ")");
                if (state.Applied.Conditions.Any(condition => !string.IsNullOrWhiteSpace(condition.Text)))
                {
                    var ids = matches.Select(record => record.Id.ToString(CultureInfo.InvariantCulture)).ToArray();
                    clauses.Add(ids.Length == 0 ? "false" : "[ID] IN (" + string.Join(',', ids) + ")");
                }
                var expression = string.Join(" AND ", clauses);
                if (document.Data.DefaultView.RowFilter != expression) document.Data.DefaultView.RowFilter = expression;
                filterState.Applied = expression;
                var visibleIds = document.Data.DefaultView.Cast<DataRowView>().Select(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture)).ToHashSet();
                matches = matches.Where(record => visibleIds.Contains(record.Id));
            }
            else if (_hideEmpty.Checked && _businessKind is { } kind && kind != BusinessPageKind.ExtendData)
            {
                var primaryKeys = kind switch
                {
                    BusinessPageKind.Persons => new[] { "persons" }, BusinessPageKind.Items => new[] { "items-low", "items-high", "items-extended" },
                    BusinessPageKind.JobSeries => new[] { "job-series" }, BusinessPageKind.DetailedJobs => new[] { "detailed-jobs" },
                    BusinessPageKind.Strategies => new[] { "strategies" }, _ => new[] { "shops" }
                };
                var nonemptyIds = new HashSet<int>();
                foreach (var key in primaryKeys)
                    if (_workspace.Documents.TryGetValue(key, out var primary))
                        foreach (var row in primary.Data.Select(NonEmptyRowFilter(primary.Data))) nonemptyIds.Add(Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture));
                matches = matches.Where(record => nonemptyIds.Contains(record.Id));
            }
            var filtered = matches.ToArray();
            _entityList.BeginUpdate();
            try
            {
                var sameItems = _entityList.Items.Count == filtered.Length &&
                    _entityList.Items.Cast<object>().OfType<EntityChoice>().Select(entity => (entity.Id, entity.Name))
                        .SequenceEqual(filtered.Select(record => (record.Id, record.Name)));
                if (!sameItems)
                {
                    _entityList.Items.Clear();
                    foreach (var record in filtered) _entityList.Items.Add(new EntityChoice(record.Id, record.Name));
                }
                _quickLookup.SetOptions(records.Select(record => new IdNameOption(record.Id, record.Name)));
                var selected = Array.FindIndex(filtered, record => record.Id == currentId);
                _entityList.SelectedIndex = selected >= 0 ? selected : filtered.Length > 0 ? 0 : -1;
                state.SelectedId = (_entityList.SelectedItem as EntityChoice)?.Id ?? currentId;
            }
            finally { _entityList.EndUpdate(); }
            _querySummary.Text = $"匹配 {filtered.Length} / 共 {records.Count} 项";
            _emptyQueryLabel.Visible = filtered.Length == 0;
            _businessEditor.SetQueryResultAvailable(filtered.Length != 0);
            if (_emptyQueryLabel.Visible) _emptyQueryLabel.BringToFront();
            FitNavigationWidth(pageKey.ScopeKey, records);
            if (bindSelection && _entityList.SelectedItem is EntityChoice entity)
            {
                if (_businessKind is { } businessKind) _businessEditor.Bind(businessKind, entity.Id);
                else if (_grid.CurrentRow?.DataBoundItem is not DataRowView selectedRow || Convert.ToInt32(selectedRow["ID"], CultureInfo.InvariantCulture) != entity.Id)
                    SelectEntityById(entity.Id);
            }
        }
        finally
        {
            _selectingBusinessEntity = previousSelection;
            _suppressGridEvents = previousGrid;
            _refreshingQuery = false;
        }
    }

    private bool RevealQueryEntity(int id)
    {
        if (_refreshingQuery || QueryPageKey is not { } key) return true;
        if (_entityList.Items.Cast<object>().OfType<EntityChoice>().Any(entity => entity.Id == id)) return true;
        if (!CurrentQueryRecords().Any(record => record.Id == id)) return true;
        if (!PreparePendingEdits()) return false;
        ResetCurrentQuery(key);
        if (_hideEmpty.Checked) _hideEmpty.Checked = false;
        RefreshQueryNavigation(bindSelection: false);
        return true;
    }
    private void FitNavigationWidth(string key, IReadOnlyList<RecordPresentation> records)
    {
        if (_navigationSplit is not { } split || !_sizedNavigationPages.Add(key) || split.ClientSize.Width < 720) return;
        var limit = split.ClientSize.Width / 3;
        var width = records.Select(record => TextRenderer.MeasureText($"{record.Id}：{record.Name}", _entityList.Font).Width + 40)
            .DefaultIfEmpty(246).Max();
        split.SplitterDistance = Math.Clamp(width, Math.Min(246, limit), limit);
    }

    private void SyncQuerySelectionFromGrid(bool force = false)
    {
        if ((!force && (_suppressGridEvents || _refreshingQuery)) || _businessKind.HasValue || _currentDocument == null ||
            _grid.CurrentRow?.DataBoundItem is not DataRowView view) return;
        var id = Convert.ToInt32(view["ID"], CultureInfo.InvariantCulture);
        var previous = _selectingBusinessEntity;
        _selectingBusinessEntity = true;
        try { _entityList.SelectedIndex = _entityList.Items.Cast<object>().ToList().FindIndex(item => item is EntityChoice entity && entity.Id == id); }
        finally { _selectingBusinessEntity = previous; }
    }

    private void ChangeHideEmptyFilter()
    {
        if (_restoringHideEmpty) return;
        if (!PreparePendingEdits())
        {
            _restoringHideEmpty = true;
            try { _hideEmpty.Checked = _appliedHideEmpty; }
            finally { _restoringHideEmpty = false; }
            return;
        }
        _appliedHideEmpty = _hideEmpty.Checked;
        RefreshQueryNavigation();
    }

    private void QueueQueryRefresh()
    {
        if (_queryRefreshQueued || _refreshingQuery || !IsHandleCreated || IsDisposed) return;
        _queryRefreshQueued = true;
        BeginInvoke((Action)(() =>
        {
            _queryRefreshQueued = false;
            if (IsDisposed || _grid.IsCurrentCellInEditMode || _businessEditor.HasPendingChanges) return;
            RefreshQueryNavigation();
        }));
    }

    internal bool QueryForSmokeTest(string query) => QueryConditionsForSmokeTest([new(null, query)]);
    internal IReadOnlyList<TextBox> QueryInputsForSmokeTest => _queryInputs;
    internal IReadOnlyList<ComboBox> QueryFieldsForSmokeTest => _queryFields;
    internal RecordQueryScope QueryScopeForSmokeTest => CurrentQueryScope();
    internal bool QueryConditionsForSmokeTest(IReadOnlyList<RecordQueryCondition> conditions)
    {
        for (var index = 0; index < 3; index++)
        {
            var condition = index < conditions.Count ? conditions[index] : new(null, string.Empty);
            _queryFields[index].SelectedIndex = _queryFields[index].Items.Cast<QueryFieldChoice>().ToList()
                .FindIndex(choice => choice.Key == condition.FieldKey);
            _queryInputs[index].Text = condition.Text;
        }
        return SubmitRecordQuery();
    }
    internal void QuickLocateForSmokeTest(int id) => SelectEntityById(id);
    internal string QuerySummaryForSmokeTest => _querySummary.Text;
    internal bool EmptyQueryForSmokeTest => _emptyQueryLabel.Visible;
}
