using System.Data;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly Dictionary<string, Dictionary<string, int>> _columnWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GridViewState> _gridViewStates = new(StringComparer.Ordinal);
    private string? _gridLayoutKey;
    private sealed record GridViewState(object? CurrentId, string? Column, object? FirstId, int HorizontalOffset);

    private BusinessEditorPanel CreateBusinessEditor()
    {
        var panel = new BusinessEditorPanel(_workspace, _resourceAdapter ??= new()) { Visible = false };
        panel.Edited += (_, _) => RefreshDirtyState();
        panel.PendingChangesChanged += (_, _) => RefreshDirtyState();
        panel.SaveRequested += (_, _) => SaveProject();
        panel.ExtendCategoryChanged += (_, _) => { if (ReferenceEquals(panel, _businessEditor)) RefreshExtendEntityList(); };
        return panel;
    }

    private void ActivateBusinessEditor(BusinessPageKind kind)
    {
        if (!_businessEditors.TryGetValue(kind, out var next))
            _businessEditors[kind] = next = CreateBusinessEditor();
        if (ReferenceEquals(next, _businessEditor)) return;
        var parent = _businessEditor.Parent;
        var index = parent?.Controls.GetChildIndex(_businessEditor) ?? -1;
        _businessEditor.Visible = false;
        parent?.Controls.Remove(_businessEditor);
        _businessEditor = next;
        if (parent == null) return;
        parent.Controls.Add(next);
        if (index >= 0) parent.Controls.SetChildIndex(next, index);
    }

    private void ResetBusinessEditors()
    {
        ResetReferenceChoices();
        var parent = _businessEditor.Parent;
        var index = parent?.Controls.GetChildIndex(_businessEditor) ?? -1;
        // Detaching cached editors keeps inactive controls out of navigation,
        // validation and accessibility, while preserving their view state.
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
        _gridViewStates.Clear();
        _gridLayoutKey = null;
    }

    private void DiscardAllEditorInputs()
    {
        foreach (var panel in _businessEditors.Values) panel.DiscardPendingChanges();
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
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        if (_columnWidths.TryGetValue(key, out var saved))
        {
            foreach (DataGridViewColumn column in _grid.Columns)
                if (saved.TryGetValue(column.Name, out var width)) column.Width = width;
        }
        else
        {
            // Measure a fixed sample once, never on every scroll. Sampling via
            // the bound view avoids realizing every DataGridViewRow.
            var view = (_grid.DataSource as DataTable)?.DefaultView;
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                var width = TextRenderer.MeasureText(column.HeaderText, _grid.Font).Width + 32;
                if (view != null && view.Table!.Columns.Contains(column.DataPropertyName))
                    for (var i = 0; i < Math.Min(view.Count, 24); i++)
                        width = Math.Max(width, TextRenderer.MeasureText(Convert.ToString(view[i][column.DataPropertyName]) ?? string.Empty,
                            _grid.Font, new Size(300, 24), TextFormatFlags.SingleLine).Width + 32);
                column.Width = Math.Clamp(width, 68, 340);
            }
        }
        _gridLayoutKey = key;
        RestoreGridView(key);
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
        => string.Join(" OR ", table.Columns.Cast<DataColumn>().Where(column => column.ColumnName != "ID")
            .Select(column => "ISNULL(CONVERT([" + column.ColumnName.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal) + "], 'System.String'), '')")
            .Select(value => $"(TRIM({value}) <> '' AND {value} <> '0')").DefaultIfEmpty("false"));

    private sealed class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView() => DoubleBuffered = true;
    }
}
