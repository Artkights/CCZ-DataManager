using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly SearchableIdComboBox _queryText = new() { Name = "RecordQuery", Width = 265, MatchDisplayText = true, CausesValidation = false };
    private readonly Button _queryButton = new() { Name = "ExecuteQuery", Text = "查询", AutoSize = true, CausesValidation = false };
    private readonly Button _clearQueryButton = new() { Name = "ClearQuery", Text = "清空", AutoSize = true, CausesValidation = false };
    private readonly Label _querySummary = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
    private readonly FlowLayoutPanel _queryGroup = new() { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
    private readonly Label _emptyQueryLabel = new() { Name = "EmptyQueryResult", Text = "没有匹配记录，请修改或清空查询条件。", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, BackColor = SystemColors.Window, Visible = false };
    private readonly Dictionary<string, string> _pageQueries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pageQueryIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sizedNavigationPages = new(StringComparer.Ordinal);
    private SplitContainer? _navigationSplit;
    private bool _refreshingQuery, _queryRefreshQueued;
    private bool _queryInputEdited;
    private string? _queryBoundPage;
    private bool _restoringHideEmpty, _appliedHideEmpty;

    private string? QueryPageKey => _businessKind == BusinessPageKind.ExtendData && _businessEditor.ExtendEditor is { } extend
        ? "business:ExtendData:" + extend.Category
        : _businessKind is { } kind ? "business:" + kind : _currentDocument?.Definition.Key;

    private string ActiveQuery => QueryPageKey is { } key ? _pageQueries.GetValueOrDefault(key, string.Empty) : string.Empty;

    private FlowLayoutPanel BuildQuerySettingsStrip()
    {
        var strip = new FlowLayoutPanel { Name = "SettingsAndQuery", Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(8, 2, 8, 2),
            BackColor = Color.FromArgb(245, 247, 250) };
        FlowLayoutPanel Group(string caption, Control control, int width)
        {
            var group = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 8, 0) };
            group.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 4, 0) });
            control.Dock = DockStyle.None;
            control.Width = width;
            control.Margin = new Padding(0, 3, 0, 3);
            group.Controls.Add(control);
            strip.Controls.Add(group);
            return group;
        }
        Group("版本", _profileSelector, 165);
        Group("布局", _variantSelector, 195);
        _hideEmpty.Margin = new Padding(0, 6, 10, 3);
        strip.Controls.Add(_hideEmpty);
        Group("快速定位", _quickLookup, 190);
        _queryText.Margin = new Padding(0, 3, 5, 3);
        _shellTips.SetToolTip(_queryText, "输入当前页左侧列表的 ID 或名称，选择匹配项，或按回车查询。仅匹配左侧显示的文字。");
        _queryGroup.Controls.Add(_queryText);
        _queryGroup.Controls.Add(_queryButton);
        _queryGroup.Controls.Add(_clearQueryButton);
        _queryGroup.Controls.Add(_querySummary);
        strip.Controls.Add(_queryGroup);
        Group("表", _tableSelector, 285);
        _tableSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _queryButton.Click += (_, _) => SubmitRecordQuery();
        _clearQueryButton.Click += (_, _) => ExecuteRecordQuery(string.Empty);
        _queryText.SelectionChangeCommitted += (_, _) =>
        {
            if (_queryText.SelectedItem is IdNameOption option) ExecuteRecordQuery(option.Display, option.Id);
        };
        _queryText.TextUpdate += (_, _) => _queryInputEdited = true;
        _queryText.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.Handled = args.SuppressKeyPress = true;
            SubmitRecordQuery();
        };
        return strip;
    }

    private void SubmitRecordQuery()
    {
        // SelectedItem can also be assigned by typing a complete label. Only
        // retain an exact selection that was explicitly committed previously.
        if (!_queryInputEdited && QueryPageKey is { } key && _pageQueryIds.TryGetValue(key, out var id) && _queryText.Text == ActiveQuery)
            ExecuteRecordQuery(_queryText.Text, id);
        else ExecuteRecordQuery(_queryText.Text);
    }

    private bool ExecuteRecordQuery(string query, int? selectedId = null)
    {
        if (QueryPageKey is not { } key || !PreparePendingEdits()) return false;
        _pageQueries[key] = query.Trim();
        if (selectedId.HasValue) _pageQueryIds[key] = selectedId.Value;
        else _pageQueryIds.Remove(key);
        _queryText.DroppedDown = false;
        if (_businessKind == BusinessPageKind.ExtendData && _businessEditor.ExtendEditor is { } extend)
            extend.SearchText = query.Trim();
        _queryInputEdited = false;
        RefreshQueryNavigation();
        return true;
    }

    private void ResetQueryNavigation()
    {
        _queryBoundPage = null;
        _pageQueries.Clear();
        _pageQueryIds.Clear();
        _queryInputEdited = false;
        _sizedNavigationPages.Clear();
        _queryText.SetOptions([]);
        _queryText.SetBoundValue(string.Empty, filterOptions: true);
        _emptyQueryLabel.Visible = false;
        _querySummary.Text = string.Empty;
    }

    private IReadOnlyList<RecordPresentation> CurrentQueryRecords()
    {
        var service = new RecordPresentationService(_workspace);
        if (_businessKind == BusinessPageKind.ExtendData && _businessEditor.ExtendEditor is { } extend)
        {
            return extend.GetEntityOptions().Select(option => new RecordPresentation(option.Id, option.Name,
                string.Empty)).ToArray();
        }
        return _businessKind is { } kind ? service.GetBusinessRecords(kind)
            : _currentDocument is { } document ? service.GetTableRecords(document) : [];
    }

    private void RefreshQueryNavigation(bool bindSelection = true)
    {
        if (_refreshingQuery || QueryPageKey is not { } pageKey) return;
        _refreshingQuery = true;
        var previousSelection = _selectingBusinessEntity;
        var previousGrid = _suppressGridEvents;
        _selectingBusinessEntity = _suppressGridEvents = true;
        try
        {
            var records = CurrentQueryRecords();
            var query = ActiveQuery;
            if (_queryBoundPage != pageKey) _queryInputEdited = false;
            var inputText = _queryBoundPage == pageKey && _queryText.ContainsFocus && _queryInputEdited ? _queryText.Text : null;
            var hasSelectedId = _pageQueryIds.TryGetValue(pageKey, out var selectedQueryId);
            if (hasSelectedId && records.FirstOrDefault(record => record.Id == selectedQueryId) is { } selectedRecord)
                _pageQueries[pageKey] = query = selectedRecord.Display;
            _queryText.SetOptions(records.Select(record => new IdNameOption(record.Id,
                string.IsNullOrWhiteSpace(record.Name) ? "未命名" : record.Name)));
            _queryText.SetBoundValue(inputText ?? query, preserveEditSelection: inputText != null, filterOptions: true);
            _queryText.DropDownWidth = Math.Max(_queryText.Width, Math.Min(760, records
                .Select(record => TextRenderer.MeasureText(record.Display, _queryText.Font).Width + 40).DefaultIfEmpty(265).Max()));
            var currentId = _queryBoundPage == pageKey ? (_entityList.SelectedItem as EntityChoice)?.Id : null;
            _queryBoundPage = pageKey;
            if (_businessKind is not null) currentId ??= _businessEditor.CurrentId;
            else if (_grid.CurrentRow?.DataBoundItem is DataRowView current) currentId = Convert.ToInt32(current["ID"], CultureInfo.InvariantCulture);
            IEnumerable<RecordPresentation> matches = records.Where(record => hasSelectedId ? record.Id == selectedQueryId : record.Matches(query));
            if (_currentDocument is { } document)
            {
                var clauses = new List<string>();
                if (_hideEmpty.Checked) clauses.Add("(" + NonEmptyRowFilter(document.Data) + ")");
                if (query.Length > 0)
                {
                    var ids = matches.Select(record => record.Id.ToString(CultureInfo.InvariantCulture)).ToArray();
                    clauses.Add(ids.Length == 0 ? "false" : "[ID] IN (" + string.Join(',', ids) + ")");
                }
                var expression = string.Join(" AND ", clauses);
                if (document.Data.DefaultView.RowFilter != expression) document.Data.DefaultView.RowFilter = expression;
                var visibleIds = document.Data.DefaultView.Cast<DataRowView>().Select(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture)).ToHashSet();
                matches = matches.Where(record => visibleIds.Contains(record.Id));
            }
            else if (_hideEmpty.Checked && _businessKind is { } kind && kind != BusinessPageKind.ExtendData)
            {
                // Entity pages have one primary record per ID. Derived names never make an empty physical row nonempty.
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
                _entityList.Items.Clear();
                foreach (var record in filtered) _entityList.Items.Add(new EntityChoice(record.Id, record.Name));
                _quickLookup.SetOptions(records.Select(record => new IdNameOption(record.Id, record.Name)));
                var selected = Array.FindIndex(filtered, record => record.Id == currentId);
                _entityList.SelectedIndex = selected >= 0 ? selected : filtered.Length > 0 ? 0 : -1;
            }
            finally { _entityList.EndUpdate(); }
            _querySummary.Text = $"匹配 {filtered.Length} / 共 {records.Count} 项";
            _emptyQueryLabel.Visible = filtered.Length == 0;
            _businessEditor.Enabled = filtered.Length != 0;
            if (_emptyQueryLabel.Visible) _emptyQueryLabel.BringToFront();
            FitNavigationWidth(pageKey, records);
            if (bindSelection && _entityList.SelectedItem is EntityChoice entity)
            {
                if (_businessKind is { } businessKind)
                    _businessEditor.Bind(businessKind, entity.Id);
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
        if (_refreshingQuery || QueryPageKey == null) return true;
        if (_entityList.Items.Cast<object>().OfType<EntityChoice>().Any(entity => entity.Id == id)) return true;
        if (!CurrentQueryRecords().Any(record => record.Id == id)) return true;
        if (!PreparePendingEdits()) return false;
        _pageQueries[QueryPageKey] = string.Empty;
        _pageQueryIds.Remove(QueryPageKey);
        if (_businessKind == BusinessPageKind.ExtendData && _businessEditor.ExtendEditor is { } extend) extend.SearchText = string.Empty;
        // A direct jump must also reveal a legitimate empty record.
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

    internal bool QueryForSmokeTest(string query) => ExecuteRecordQuery(query);
    internal void QuickLocateForSmokeTest(int id) => SelectEntityById(id);
    internal string QuerySummaryForSmokeTest => _querySummary.Text;
    internal bool EmptyQueryForSmokeTest => _emptyQueryLabel.Visible;
}
