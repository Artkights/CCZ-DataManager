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
        { Name = $"RecordQueryField{index}", Width = 142, DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false, CausesValidation = false }).ToArray();
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
    private sealed class NavigationTextWidths { public Dictionary<string, int> Widths { get; } = new(); }
    private readonly ConditionalWeakTable<object, NavigationTextWidths> _navigationTextWidths = new();

    private int NavigationTextWidth(IReadOnlyList<RecordPresentation> records)
    {
        using var timing = RuntimeTiming.Measure("MainForm.NavigationTextWidth");
        var font = _entityList.Font;
        var key = $"{font.Name}:{font.SizeInPoints}:{font.Style}:{_entityList.DeviceDpi}";
        var widths = _navigationTextWidths.GetOrCreateValue(records).Widths;
        if (!widths.TryGetValue(key, out var width))
            widths[key] = width = records.Select(record => TextRenderer.MeasureText(record.Display, font).Width + 40)
                .DefaultIfEmpty(246).Max();
        return width;
    }
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
    private CancellationTokenSource? _queryCancellation;
    private long _queryGeneration;
    private IReadOnlyList<RecordPresentation>? _quickLookupRecords;
    private sealed record CachedQueryScope(string Version, RecordQueryScope Scope);
    private readonly Dictionary<RecordQueryContext, CachedQueryScope> _queryScopes = new();
    private sealed record QueryChoiceSet(QueryFieldChoice[] Choices, string FontKey, int Width);
    private readonly Dictionary<RecordQueryContext, QueryChoiceSet> _queryChoices = new();
    private string? _externalQueryIdentity;

    private void RefreshQueryExternalVersion()
    {
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        var paths = _workspace.Definitions.Where(definition => definition.Key.StartsWith("item-effect-names", StringComparison.Ordinal))
            .Select(definition => DataManager.Core.Binary.ProjectFileLocator.Resolve(_workspace.RootPath, definition.FileName))
            .Append(DataManager.Core.Binary.ProjectFileLocator.Resolve(_workspace.RootPath, "Ekd5.exe"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var identity = string.Join('|', paths.Select(path =>
        {
            var info = new FileInfo(path);
            return $"{path}:{(info.Exists ? info.Length : -1)}:{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
        })) + "|" + (_resourceAdapter?.Revision ?? 0);
        if (_externalQueryIdentity == identity) return;
        _externalQueryIdentity = identity;
        WorkspaceReadCache.For(_workspace).InvalidateExternal();
    }

    private void CancelRecordQuery()
    {
        _queryGeneration++;
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = null;
    }

    private RecordQueryScope CreateQueryScope()
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
                field = physical with { Dependencies = (physical.Dependencies ?? []).Append(new("$external")).ToArray(),
                    ReadValues = id => physical.ReadValues(id).SelectMany(value =>
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && names.TryGetValue(number, out var display)
                        ? new[] { value, display } : new[] { value }).ToArray() };
            }
            fields.Add(field);
        }
        return new("table:" + document.Definition.Key, fields);
    }

    private RecordQueryScope CurrentQueryScope()
    {
        if (_businessKind is not null) return _businessEditor.CurrentQueryScope;
        if (QueryPageKey is not { } key) return CreateQueryScope();
        // Physical descriptors include reference display names; observe those dependencies too.
        var cache = WorkspaceReadCache.For(_workspace);
        var version = cache.GetSchemaVersion() + "|" + (_currentDocument == null ? string.Empty :
            cache.GetVersion(cache.GetNavigationDependencies(_currentDocument.Definition.Fields
                .SelectMany(field => field.ReferenceTable == "items" ? new[] { "items-low", "items-high", "items-extended" } :
                    string.IsNullOrEmpty(field.ReferenceTable) ? Array.Empty<string>() : new[] { field.ReferenceTable }))));
        version += "|" + string.Join(',', _grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible)
            .Select(column => column.Name));
        version += "|" + cache.GetVersion([new("$external")]);
        if (_queryScopes.TryGetValue(key, out var cached) && cached.Version == version) return cached.Scope;
        var scope = CreateQueryScope();
        _queryScopes[key] = new(version, scope);
        return scope;
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

    private Control BuildQuerySettingsStrip() => BuildResponsiveQuerySettingsStrip();

    private static void UpdateQueryDropDownHeight(ComboBox combo)
    {
        // Native integral-height calculation repeats expensive dropdown sizing while
        // inserting items. Keep a whole number of rows explicitly, including after scaling.
        var height = Math.Max(1, Math.Min(combo.MaxDropDownItems, combo.Items.Count)) * combo.ItemHeight + 2;
        if (combo.DropDownHeight != height) combo.DropDownHeight = height;
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
        using var timing = RuntimeTiming.Measure("MainForm.BindQueryInputs");
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
        var choices = new[] { new QueryFieldChoice(null, "全部属性") }
            .Concat(scope.Fields.Select(field => new QueryFieldChoice(field.Key, field.DisplayName))).ToArray();
        var font = _queryFields[0].Font;
        var fontKey = $"{font.Name}:{font.SizeInPoints}:{font.Style}:{_queryFields[0].DeviceDpi}";
        if (!_queryChoices.TryGetValue(key, out var choiceSet) || choiceSet.FontKey != fontKey ||
            !choiceSet.Choices.SequenceEqual(choices))
        {
            using var measureTiming = RuntimeTiming.Measure("MainForm.MeasureQueryChoices");
            choiceSet = new(choices, fontKey, Math.Min(800,
                choices.Max(choice => TextRenderer.MeasureText(choice.Display, font).Width) + 32));
            _queryChoices[key] = choiceSet;
        }
        else choices = choiceSet.Choices;
        _bindingQueryInputs = true;
        try
        {
            for (var index = 0; index < 3; index++)
            {
                using var comboTiming = RuntimeTiming.Measure("MainForm.BindOneQueryCombo");
                var combo = _queryFields[index];
                if (!combo.Items.Cast<QueryFieldChoice>().SequenceEqual(choices))
                {
                    using var itemsTiming = RuntimeTiming.Measure("MainForm.ReplaceQueryChoices");
                    combo.BeginUpdate();
                    try
                    {
                        using (RuntimeTiming.Measure("MainForm.ClearQueryChoices")) combo.Items.Clear();
                        using (RuntimeTiming.Measure("MainForm.AddQueryChoices")) combo.Items.AddRange(choices);
                    }
                    finally { using (RuntimeTiming.Measure("MainForm.EndQueryChoices")) combo.EndUpdate(); }
                }
                combo.DropDownWidth = Math.Max(combo.Width, choiceSet.Width);
                UpdateQueryDropDownHeight(combo);
                using (RuntimeTiming.Measure("MainForm.SelectQueryChoice"))
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
        CancelRecordQuery();
        _quickLookupRecords = null;
        _queryScopes.Clear();
        _queryChoices.Clear();
        _externalQueryIdentity = null;
        WorkspaceReadCache.Reset(_workspace);
    }

    private IReadOnlyList<RecordPresentation> CurrentQueryRecords()
    {
        using var timing = RuntimeTiming.Measure("MainForm.CurrentQueryRecords");
        var cache = WorkspaceReadCache.For(_workspace);
        if (_businessKind == BusinessPageKind.ExtendData && _businessEditor.ExtendEditor is { } extend)
            return extend.GetQueryNavigationRecords();
        return _businessKind is { } kind ? cache.GetNavigationRecords(kind)
            : _currentDocument is { } document ? cache.GetTableRecords(document) : [];
    }

    private async Task RefreshLargeQueryAsync(RecordQueryContext page, RecordQueryScope scope,
        IReadOnlyList<RecordPresentation> records, RecordQueryCondition[] conditions, int? currentId,
        bool bindSelection, CancellationTokenSource cancellation, long generation)
    {
        var token = cancellation.Token;
        var version = WorkspaceReadCache.For(_workspace).GetVersion();
        try
        {
            var snapshot = await new RecordQueryService(_workspace).BuildSnapshotAsync(scope, records,
                RecordQueryService.RequestedFieldKeys(conditions), async ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(1, ct); // resume through the WinForms synchronization context
                    if (_grid.IsCurrentCellInEditMode || _businessEditor.HasPendingChanges)
                        throw new OperationCanceledException("查询期间产生了待提交编辑。", ct);
                }, token);
            var matches = await Task.Run(() => snapshot.Filter(conditions, token), token);
            if (token.IsCancellationRequested || IsDisposed || generation != _queryGeneration ||
                QueryPageKey != page) return;
            // The user can resume editing while the immutable snapshot is being matched.
            // Publishing must never rebind or disable that editor before it validates.
            if (_grid.IsCurrentCellInEditMode || _businessEditor.HasPendingChanges)
            {
                _querySummary.Text = "有待提交编辑，保留原结果";
                SetStatus("查询期间产生了待提交编辑，请完成编辑后重新查询。");
                return;
            }
            if (version != WorkspaceReadCache.For(_workspace).GetVersion())
            {
                _querySummary.Text = "数据已更新，正在重新查询…";
                QueueQueryRefresh();
                return;
            }
            ApplyQueryMatches(page, records, matches, currentId, bindSelection);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && generation == _queryGeneration && QueryPageKey == page && !token.IsCancellationRequested)
            {
                cancellation.Cancel();
                if (_grid.IsCurrentCellInEditMode || _businessEditor.HasPendingChanges)
                {
                    _querySummary.Text = "有待提交编辑，保留原结果";
                    SetStatus("查询期间产生了待提交编辑，请完成编辑后重新查询。");
                }
                else QueueQueryRefresh();
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && generation == _queryGeneration)
            { _querySummary.Text = "查询失败，保留原结果"; SetStatus("查询失败：" + ex.Message, true); }
        }
        finally
        {
            if (!IsDisposed && generation == _queryGeneration)
            {
                if (_querySummary.Text == "正在查询…") _querySummary.Text = "查询已取消，保留原结果";
                if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void RefreshQueryNavigation(bool bindSelection = true)
    {
        using var timing = RuntimeTiming.Measure("MainForm.RefreshQueryNavigation");
        if (_refreshingQuery || QueryPageKey is not { } pageKey) return;
        CancelRecordQuery();
        _refreshingQuery = true;
        try
        {
            using (RuntimeTiming.Measure("MainForm.RefreshQueryExternalVersion")) RefreshQueryExternalVersion();
            RecordQueryScope scope;
            using (RuntimeTiming.Measure("MainForm.CurrentQueryScope")) scope = CurrentQueryScope();
            var state = QueryState(pageKey);
            int? currentId = _queryBoundPage == pageKey ? (_entityList.SelectedItem as EntityChoice)?.Id ?? state.SelectedId : state.SelectedId;
            if (_businessKind is not null) currentId = _businessEditor.CurrentId;
            else if (_grid.CurrentRow?.DataBoundItem is DataRowView current) currentId = Convert.ToInt32(current["ID"], CultureInfo.InvariantCulture);
            BindQueryInputs(pageKey, scope);
            var records = CurrentQueryRecords();
            var conditions = state.Applied.Conditions.ToArray();
            var keys = RecordQueryService.RequestedFieldKeys(conditions);
            // An empty query is navigation only: never read detail fields on activation.
            if (keys is { Count: 0 })
                ApplyQueryMatches(pageKey, records, records, currentId, bindSelection);
            else if ((long)records.Count * (keys?.Count ?? scope.Fields.Count) > 20_000)
            {
                _querySummary.Text = "正在查询…";
                _queryCancellation = new();
                _ = RefreshLargeQueryAsync(pageKey, scope, records, conditions, currentId,
                    bindSelection, _queryCancellation, _queryGeneration);
            }
            else
            {
                var snapshot = new RecordQueryService(_workspace).BuildSnapshot(scope, records, keys);
                ApplyQueryMatches(pageKey, records, snapshot.Filter(conditions), currentId, bindSelection);
            }
        }
        finally { _refreshingQuery = false; }
    }

    private void ApplyQueryMatches(RecordQueryContext pageKey, IReadOnlyList<RecordPresentation> records,
        IEnumerable<RecordPresentation> matches, int? currentId, bool bindSelection)
    {
        using var timing = RuntimeTiming.Measure("MainForm.ApplyQueryMatches");
        var state = QueryState(pageKey);
        var previousSelection = _selectingBusinessEntity;
        var previousGrid = _suppressGridEvents;
        var previousRefresh = _refreshingQuery;
        _refreshingQuery = _selectingBusinessEntity = _suppressGridEvents = true;
        try
        {
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
            // An explicit measured extent avoids ListBox measuring every inserted item again.
            using (RuntimeTiming.Measure("MainForm.ApplyNavigationExtent"))
                _entityList.HorizontalExtent = Math.Max(1, NavigationTextWidth(records));
            _entityList.BeginUpdate();
            try
            {
                var sameItems = _entityList.Items.Count == filtered.Length &&
                    _entityList.Items.Cast<object>().OfType<EntityChoice>().Select(entity => (entity.Id, entity.Name))
                        .SequenceEqual(filtered.Select(record => (record.Id, record.Name)));
                if (!sameItems)
                {
                    using (RuntimeTiming.Measure("MainForm.ClearEntityList")) _entityList.Items.Clear();
                    using var listTiming = RuntimeTiming.Measure("MainForm.BindEntityList");
                    _entityList.Items.AddRange(filtered.Select(record => (object)new EntityChoice(record.Id, record.Name)).ToArray());
                }
                if (!ReferenceEquals(records, _quickLookupRecords))
                {
                    using var lookupTiming = RuntimeTiming.Measure("MainForm.BindQuickLookup");
                    _quickLookup.SetOptions(records.Select(record => new IdNameOption(record.Id, record.Name)));
                    _quickLookupRecords = records;
                }
                var selected = Array.FindIndex(filtered, record => record.Id == currentId);
                _entityList.SelectedIndex = selected >= 0 ? selected : filtered.Length > 0 ? 0 : -1;
                state.SelectedId = (_entityList.SelectedItem as EntityChoice)?.Id ?? currentId;
            }
            finally { using (RuntimeTiming.Measure("MainForm.EndEntityListUpdate")) _entityList.EndUpdate(); }
            using (RuntimeTiming.Measure("MainForm.QuerySummaryAndAvailability"))
            {
                _querySummary.Text = $"匹配 {filtered.Length} / 共 {records.Count} 项";
                _emptyQueryLabel.Visible = filtered.Length == 0;
                _businessEditor.SetQueryResultAvailable(filtered.Length != 0);
                if (_emptyQueryLabel.Visible) _emptyQueryLabel.BringToFront();
            }
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
            _refreshingQuery = previousRefresh;
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
        var width = NavigationTextWidth(records);
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
        // Offscreen tests use DoEvents rather than Application.Run. Its temporary message
        // loop can uninstall this context, while the real application retains it.
        EnsureSmokeQueryContext();
        for (var index = 0; index < 3; index++)
        {
            var condition = index < conditions.Count ? conditions[index] : new(null, string.Empty);
            _queryFields[index].SelectedIndex = _queryFields[index].Items.Cast<QueryFieldChoice>().ToList()
                .FindIndex(choice => choice.Key == condition.FieldKey);
            _queryInputs[index].Text = condition.Text;
        }
        var applied = SubmitRecordQuery();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (applied && _querySummary.Text.Contains("正在", StringComparison.Ordinal) && timeout.ElapsedMilliseconds < 30000)
        { Application.DoEvents(); EnsureSmokeQueryContext(); Thread.Sleep(1); }
        EnsureSmokeQueryContext();
        return applied;

        static void EnsureSmokeQueryContext()
        {
            if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
    }
    internal void QuickLocateForSmokeTest(int id) => SelectEntityById(id);
    internal string QuerySummaryForSmokeTest => _querySummary.Text;
    internal bool EmptyQueryForSmokeTest => _emptyQueryLabel.Visible;
}
