using System.Data;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly Dictionary<string, Dictionary<string, int>> _columnWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GridViewState> _gridViewStates = new(StringComparer.Ordinal);
    private string? _gridLayoutKey;
    private readonly Dictionary<string, Dictionary<string, int>> _gridContentWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _gridManualWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, HashSet<(DataTable Table, string Field)>>> _gridWidthDependencies = new(StringComparer.Ordinal);
    private bool _arrangingGridWidths;
    private bool _gridLayoutEventsInstalled;
    private readonly HashSet<DataTable> _gridWidthSources = [];
    private void InvalidateGridWidthCache(object? sender, DataColumnChangeEventArgs args)
    {
        if (sender is not DataTable table || args.Column == null) return;
        foreach (var view in _gridWidthDependencies)
            foreach (var column in view.Value)
                if (column.Value.Contains((table, args.Column.ColumnName)))
                {
                    if (_gridContentWidths.TryGetValue(view.Key, out var content)) content.Remove(column.Key);
                    if (_columnWidths.TryGetValue(view.Key, out var saved)) saved.Remove(column.Key);
                }
    }
    private void ReleaseGridWidthSources()
    {
        foreach (var table in _gridWidthSources) table.ColumnChanged -= InvalidateGridWidthCache;
        _gridWidthSources.Clear();
        _gridWidthDependencies.Clear();
    }
    private sealed record GridViewState(object? CurrentId, string? Column, object? FirstId, int HorizontalOffset);

    private BusinessEditorPanel CreateBusinessEditor()
    {
        var panel = new BusinessEditorPanel(_workspace, _resourceAdapter ??= new(), _rangeCatalog) { Visible = false };
        panel.Edited += (_, _) => { RefreshDirtyState(); QueueQueryRefresh(); };
        panel.PendingChangesChanged += (_, _) => RefreshDirtyState();
        panel.SaveRequested += (_, _) => SaveProject();
        panel.SImageLocationRequested += LocatePersonSImage;
        panel.PersonImageLocationRequested += LocatePersonImage;
        panel.ExtendCategoryChanged += (_, _) => { if (ReferenceEquals(panel, _businessEditor)) RefreshExtendEntityList(); };
        panel.QueryScopeChanged += (_, _) =>
        {
            if (ReferenceEquals(panel, _businessEditor) && !_bindingModule && !_refreshingQuery)
                RefreshQueryNavigation();
        };
        return panel;
    }

    private void ActivateBusinessEditor(BusinessPageKind kind)
    {
        if (!_businessEditors.TryGetValue(kind, out var next))
            _businessEditors[kind] = next = CreateBusinessEditor();
        if (ReferenceEquals(next, _businessEditor)) { next.ActivateView(); return; }
        var parent = _businessEditor.Parent;
        var index = parent?.Controls.GetChildIndex(_businessEditor) ?? -1;
        _businessEditor.DeactivateView();
        _businessEditor.Visible = false;
        _businessEditor = next;
        if (parent == null) return;
        // Cached editors keep their inherited font, DPI and dock parent for
        // their complete lifetime. Hidden editors never validate on navigation.
        if (next.Parent == null) parent.Controls.Add(next);
        if (index >= 0) parent.Controls.SetChildIndex(next, index);
        next.ActivateView();
    }

    private void ResetBusinessEditors()
    {
        ResetReferenceChoices();
        var parent = _businessEditor.Parent;
        var index = parent?.Controls.GetChildIndex(_businessEditor) ?? -1;
        // Each cached page remains parented until the project is replaced.
        foreach (var panel in _businessEditors.Values.Distinct()) panel.Dispose();
        _businessEditors.Clear();
        _businessEditor = CreateBusinessEditor();
        _businessEditors[BusinessPageKind.Persons] = _businessEditor;
        if (parent != null)
        {
            parent.Controls.Add(_businessEditor);
            if (index >= 0) parent.Controls.SetChildIndex(_businessEditor, index);
        }
        _moduleTables.Clear();
        _activeModuleKey = null;
        _columnWidths.Clear();
        _gridContentWidths.Clear();
        _gridManualWidths.Clear();
        ReleaseGridWidthSources();
        _gridViewStates.Clear();
        _gridLayoutKey = null;
    }

    private void DiscardAllEditorInputs()
    {
        foreach (var panel in _businessEditors.Values) panel.DiscardPendingChanges();
        _engineDetail.DiscardPendingChanges();
        _battleFormulaPanel.DiscardPendingChanges();
        DiscardEngineAssignmentChanges();
        _workspace.DiscardNativeDrafts();
    }

    private void RememberGridWidths()
    {
        if (_gridLayoutKey == null || _grid.Columns.Count == 0) return;
        _columnWidths[_gridLayoutKey] = _grid.Columns.Cast<DataGridViewColumn>()
            .ToDictionary(column => column.Name, column => column.Width, StringComparer.Ordinal);
        if (_grid.DataSource is DataTable table && table.Columns.Contains("ID"))
        {
            object? IdAt(int index) => index >= 0 && index < _grid.RowCount &&
                _grid.Rows[index].DataBoundItem is DataRowView row ? row["ID"] : null;
            _gridViewStates[_gridLayoutKey] = new(IdAt(_grid.CurrentCell?.RowIndex ?? -1),
                _grid.CurrentCell?.OwningColumn.Name, IdAt(_grid.FirstDisplayedScrollingRowIndex), _grid.HorizontalScrollingOffset);
        }
    }

    private void ApplyGridWidths(string key)
    {
        EnsureGridLayoutEvents();
        _arrangingGridWidths = true;
        try
        {
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        foreach (var table in _workspace.Documents.Values.Select(document => document.Data))
            if (_gridWidthSources.Add(table)) table.ColumnChanged += InvalidateGridWidthCache;
        var invalid = _grid.Columns.Cast<DataGridViewColumn>().Where(column =>
            !_gridContentWidths.TryGetValue(key, out var widths) || !widths.ContainsKey(column.Name))
            .Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        EnsureMeasuredGridWidths(key);
        _columnWidths.TryGetValue(key, out var saved);
        foreach (DataGridViewColumn column in _grid.Columns)
            if (!invalid.Contains(column.Name) && saved != null && saved.TryGetValue(column.Name, out var width)) column.Width = width;
            else if (_gridContentWidths[key].TryGetValue(column.Name, out var contentWidth)) column.Width = contentWidth;
        _gridLayoutKey = key;
        if (_gridManualWidths.TryGetValue(key, out var manual))
            foreach (DataGridViewColumn column in _grid.Columns)
                if (manual.TryGetValue(column.Name, out var width)) column.Width = width;
        FillGridRemainingWidth();
        RestoreGridView(key);
        }
        finally { _arrangingGridWidths = false; }
    }

    private void EnsureGridLayoutEvents()
    {
        if (_gridLayoutEventsInstalled) return;
        _gridLayoutEventsInstalled = true;
        _grid.ColumnWidthChanged += (_, args) =>
        {
            if (_arrangingGridWidths || _suppressGridEvents || _gridLayoutKey == null) return;
            if (!_gridManualWidths.TryGetValue(_gridLayoutKey, out var widths)) _gridManualWidths[_gridLayoutKey] = widths = new(StringComparer.Ordinal);
            widths[args.Column.Name] = args.Column.Width;
        };
        _grid.SizeChanged += (_, _) => ReflowGridWidths(false);
        _grid.FontChanged += (_, _) => ReflowGridWidths(true);
        _grid.DpiChangedAfterParent += (_, _) => ReflowGridWidths(true);
    }

    private void EnsureMeasuredGridWidths(string key, bool remeasure = false)
    {
        if (!_gridContentWidths.TryGetValue(key, out var widths))
            _gridContentWidths[key] = widths = new(StringComparer.Ordinal);
        var missing = _grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible &&
            (remeasure || !widths.ContainsKey(column.Name))).Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0) return;
        foreach (var measured in MeasureGridContentWidths(key, missing)) widths[measured.Key] = measured.Value;
    }

    private Dictionary<string, int> MeasureGridContentWidths(string key, HashSet<string> names)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = _grid.DataSource as DataTable;
        var scale = _grid.DeviceDpi / 96f;
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            if (!column.Visible || !names.Contains(column.Name)) continue;
            if (!_gridWidthDependencies.TryGetValue(key, out var dependencies))
                _gridWidthDependencies[key] = dependencies = new(StringComparer.Ordinal);
            var sources = new HashSet<(DataTable Table, string Field)>();
            dependencies[column.Name] = sources;
            if (table != null && table.Columns.Contains(column.DataPropertyName)) sources.Add((table, column.DataPropertyName));
            if (column.Tag is ProjectedField projection && _workspace.Documents.TryGetValue(projection.TableKey, out var projectedDocument))
                sources.Add((projectedDocument.Data, projection.FieldName));
            var definition = _currentDocument?.Definition.Fields.FirstOrDefault(field => field.Name == column.DataPropertyName);
            if (!string.IsNullOrWhiteSpace(definition?.ReferenceTable))
                foreach (var reference in ResolveReferenceDocuments(definition.ReferenceTable))
                    foreach (DataColumn field in reference.Data.Columns)
                        // Names, identifiers and equipment categories can all change a choice's rendered text.
                        sources.Add((reference.Data, field.ColumnName));
            var padding = (int)Math.Ceiling(28 * scale) + (column is DataGridViewComboBoxColumn ? SystemInformation.VerticalScrollBarWidth : 0);
            var width = TextRenderer.MeasureText(column.HeaderText, _grid.Font, Size.Empty, TextFormatFlags.SingleLine).Width + padding;
            var texts = new HashSet<string>(StringComparer.Ordinal);
            if (table != null && table.Columns.Contains(column.DataPropertyName))
                foreach (DataRow row in table.Rows) texts.Add(Convert.ToString(row[column.DataPropertyName], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            if (column is DataGridViewComboBoxColumn { DataSource: ReferenceChoice[] choices })
            {
                foreach (var choice in choices) texts.Add(choice.Display);
                if (column.CellTemplate is EquipmentReferenceCell)
                    foreach (var value in texts.ToArray())
                        if (int.TryParse(value, out var id) && choices.All(choice => choice.Value != id)) texts.Add($"{id}：原值（类别不匹配）");
            }
            if (column.Tag is ProjectedField projected && _workspace.Documents.TryGetValue(projected.TableKey, out var source))
                foreach (DataRow row in source.Data.Rows) texts.Add(Convert.ToString(row[projected.FieldName], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            foreach (var text in texts)
                width = Math.Max(width, TextRenderer.MeasureText(text.Replace('\r', ' ').Replace('\n', ' '), _grid.Font, Size.Empty, TextFormatFlags.SingleLine).Width + padding);
            // DataGridView permits up to 65536 pixels. Long descriptions remain
            // readable through horizontal scrolling instead of a narrow sample cap.
            result[column.Name] = Math.Clamp(width, (int)Math.Ceiling(64 * scale), 65536);
        }
        return result;
    }

    private void ReflowGridWidths(bool remeasure)
    {
        if (_arrangingGridWidths || _suppressGridEvents || _gridLayoutKey == null ||
            _currentDocument?.Definition.Key != _gridLayoutKey || !_grid.Visible) return;
        _arrangingGridWidths = true;
        try
        {
            var key = _gridLayoutKey;
            EnsureMeasuredGridWidths(key, remeasure);
            _gridManualWidths.TryGetValue(key, out var manual);
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                if (manual != null && manual.TryGetValue(column.Name, out var chosen)) column.Width = chosen;
                else if (_gridContentWidths[key].TryGetValue(column.Name, out var content)) column.Width = content;
            }
            FillGridRemainingWidth();
        }
        finally { _arrangingGridWidths = false; }
    }

    private void FillGridRemainingWidth()
    {
        var columns = _grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).ToArray();
        if (columns.Length == 0) return;
        var available = Math.Max(0, _grid.ClientSize.Width - (_grid.RowHeadersVisible ? _grid.RowHeadersWidth : 0) - SystemInformation.VerticalScrollBarWidth - 3);
        var remaining = available - columns.Sum(column => column.Width);
        if (remaining <= 0) return;
        _gridManualWidths.TryGetValue(_gridLayoutKey ?? string.Empty, out var manual);
        var stretch = columns.Where(column => !column.Frozen && (manual == null || !manual.ContainsKey(column.Name))).ToArray();
        if (stretch.Length == 0) return;
        var total = stretch.Sum(column => column.Width);
        for (var index = 0; index < stretch.Length; index++)
        {
            var extra = index == stretch.Length - 1 ? remaining : Math.Min(remaining, (int)((long)remaining * stretch[index].Width / Math.Max(1, total)));
            total -= stretch[index].Width;
            stretch[index].Width = Math.Min(65536, stretch[index].Width + extra);
            remaining -= extra;
        }
    }

    private void RestoreGridView(string key)
    {
        if (!_gridViewStates.TryGetValue(key, out var state) || _grid.DataSource is not DataTable table ||
            !table.Columns.Contains("ID") || _grid.RowCount == 0) return;
        var view = table.DefaultView;
        int Find(object? id)
        {
            if (id == null) return -1;
            for (var index = 0; index < view.Count; index++) if (Equals(view[index]["ID"], id)) return index;
            return -1;
        }
        var selected = Find(state.CurrentId);
        if (selected >= 0)
        {
            var column = state.Column != null && _grid.Columns.Contains(state.Column) && _grid.Columns[state.Column].Visible
                ? _grid.Columns[state.Column] : _grid.Columns.Cast<DataGridViewColumn>().First(column => column.Visible);
            _grid.CurrentCell = _grid[column.Index, selected];
            _selectingBusinessEntity = true;
            try { _entityList.SelectedIndex = _entityList.Items.Cast<object>().ToList().FindIndex(item =>
                item is EntityChoice entity && Equals(entity.Id, state.CurrentId)); }
            finally { _selectingBusinessEntity = false; }
        }
        var first = Find(state.FirstId);
        if (first >= 0 && !_grid.Rows[first].Frozen) _grid.FirstDisplayedScrollingRowIndex = first;
        _grid.HorizontalScrollingOffset = state.HorizontalOffset;
    }

    private static string NonEmptyRowFilter(DataTable table)
        => string.Join(" OR ", table.Columns.Cast<DataColumn>().Where(column => column.ColumnName != "ID" && !column.ExtendedProperties.Contains("Derived"))
            .Select(column => "ISNULL(CONVERT([" + column.ColumnName.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal) + "], 'System.String'), '')")
            .Select(value => $"(TRIM({value}) <> '' AND {value} <> '0')").DefaultIfEmpty("false"));

    private sealed class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView() => DoubleBuffered = true;
    }
}
