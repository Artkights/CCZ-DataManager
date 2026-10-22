using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private readonly List<(Control Owner, Action Refresh)> _equipmentTypeBindings = [];

    private string EquipmentPermissionDisplayName(string storageColumn)
        => new EquipmentTypeCatalogService().GetPermissionDisplayName(_workspace, storageColumn);

    private void RegisterEquipmentTypeBinding(Control owner, Action refresh)
    {
        _equipmentTypeBindings.Add((owner, refresh));
        owner.Disposed += (_, _) => _equipmentTypeBindings.RemoveAll(binding => ReferenceEquals(binding.Owner, owner));
    }

    private void RefreshEquipmentTypeBindings()
    {
        var before = _binding; _binding = true;
        try
        {
            _equipmentTypeBindings.RemoveAll(binding => binding.Owner.IsDisposed);
            foreach (var binding in _equipmentTypeBindings.ToArray()) binding.Refresh();
            _refreshItemPermissions?.Invoke();
        }
        finally { _binding = before; }
    }

    private Control BuildEquipmentTypeEditor()
    {
        var catalog = new EquipmentTypeCatalogService();
        var data = new DataTable();
        data.Columns.Add("类型", typeof(string)); data.Columns.Add("名称", typeof(string)); data.Columns.Add("显示", typeof(bool));
        var grid = GridFor(data);
        grid.Name = "EquipmentTypeEditor";
        grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.RowTemplate.Height = Math.Max(20, Font.Height + 5);
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns["类型"].ReadOnly = true;
        grid.Columns["类型"].FillWeight = 38;
        grid.Columns["名称"].FillWeight = 46;
        grid.Columns["显示"].FillWeight = 16;
        foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        var arrangingRows = false;
        void ArrangeRows()
        {
            if (arrangingRows || grid.IsDisposed || grid.Rows.Count == 0) return;
            arrangingRows = true;
            grid.SuspendLayout();
            try
            {
                var borderHeight = grid.BorderStyle == BorderStyle.None ? 0 : 2;
                var scrollHeight = grid.Controls.OfType<HScrollBar>().Where(scroll => scroll.Visible)
                    .Select(scroll => scroll.Height).FirstOrDefault();
                var available = Math.Max(0, grid.ClientSize.Height - borderHeight -
                    (grid.ColumnHeadersVisible ? grid.ColumnHeadersHeight : 0) - scrollHeight);
                var minimum = Math.Max(grid.Font.Height + 5, 20 * grid.DeviceDpi / 96);
                var height = available / grid.Rows.Count;
                var remainder = available % grid.Rows.Count;
                // Spread spare pixels across the rows so the final row reaches the frame edge.
                foreach (DataGridViewRow row in grid.Rows)
                {
                    var rowHeight = Math.Max(minimum, height + (row.Index < remainder ? 1 : 0));
                    if (row.Height != rowHeight) row.Height = rowHeight;
                }
            }
            finally { grid.ResumeLayout(false); arrangingRows = false; }
        }
        grid.ClientSizeChanged += (_, _) => ArrangeRows();
        grid.ColumnHeadersHeightChanged += (_, _) => ArrangeRows();
        grid.FontChanged += (_, _) => ArrangeRows();
        grid.DpiChangedAfterParent += (_, _) => ArrangeRows();
        grid.DataBindingComplete += (_, _) => ArrangeRows();
        var loading = false; var saving = false;
        string Signature() => string.Join('\u001F', data.Rows.Cast<DataRow>().Select(row => $"{row["名称"]}\u001E{row["显示"]}"));
        var submitted = string.Empty;
        void Refresh()
        {
            if (saving || grid.IsDisposed) return;
            loading = true;
            try
            {
                var entries = catalog.Read(_workspace);
                var canName = _workspace.Documents.TryGetValue(EquipmentTypeCatalogService.NamesTableKey, out var names) && names.Definition.IsWriteSupported;
                var canDisplay = _workspace.Documents.TryGetValue(EquipmentTypeCatalogService.DisplayTableKey, out var display) && display.Definition.IsWriteSupported;
                grid.Columns["名称"].ReadOnly = !canName;
                data.Clear();
                foreach (var entry in entries)
                    data.Rows.Add(entry.GroupIndex < 10 ? $"武器 {entry.GroupIndex + 1}" : entry.GroupIndex < 13 ? $"防具 {entry.GroupIndex - 9}" : entry.GroupIndex == 13 ? "辅助" : "道具",
                        entry.Name, entry.IsVisible);
                for (var index = 0; index < grid.Rows.Count; index++)
                {
                    var cell = grid.Rows[index].Cells["显示"];
                    cell.ReadOnly = !canDisplay || !entries[index].HasDisplayToggle;
                    if (!entries[index].HasDisplayToggle)
                    {
                        cell.Style.ForeColor = SystemColors.GrayText;
                        cell.Style.BackColor = SystemColors.Control;
                        cell.ToolTipText = "此类别没有独立的游戏显示开关。";
                    }
                }
                ArrangeRows();
                submitted = Signature();
            }
            finally { loading = false; }
        }
        bool Save()
        {
            if (_binding || loading || saving) return true;
            saving = true;
            try
            {
                if (!grid.EndEdit()) return false;
                if (grid.BindingContext?[data] is CurrencyManager currency) currency.EndCurrentEdit();
                var entries = catalog.Read(_workspace);
                var edits = new List<CompositeCellEdit>();
                for (var index = 0; index < data.Rows.Count; index++)
                {
                    var name = Convert.ToString(data.Rows[index]["名称"], CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!string.Equals(name, entries[index].Name, StringComparison.Ordinal))
                        edits.Add(new(EquipmentTypeCatalogService.NamesTableKey, index, "名称", name));
                    if (!entries[index].HasDisplayToggle) continue;
                    var visible = Convert.ToBoolean(data.Rows[index]["显示"], CultureInfo.InvariantCulture);
                    if (visible != entries[index].IsVisible)
                        edits.Add(new(EquipmentTypeCatalogService.DisplayTableKey, index, "显示值", EquipmentTypeCatalogService.DisplayValue(index, visible)));
                }
                if (edits.Count > 0)
                {
                    _workspace.ApplyCompositeTransaction("编辑装备类型", edits);
                    RefreshEquipmentTypeBindings();
                    OnEdited();
                }
                submitted = Signature();
                _errors.SetError(grid, string.Empty);
                return true;
            }
            catch (Exception ex) { _errors.SetError(grid, ex.Message); return false; }
            finally { saving = false; }
        }
        grid.CellValidating += (_, args) =>
        {
            if (_binding || loading || args.RowIndex < 0 || grid.Columns[args.ColumnIndex].Name != "名称" ||
                !_workspace.Documents.TryGetValue(EquipmentTypeCatalogService.NamesTableKey, out var names)) return;
            try { _workspace.ValidateCellValue(names, args.RowIndex, "名称", Convert.ToString(args.FormattedValue, CultureInfo.InvariantCulture) ?? string.Empty); }
            catch (Exception ex) { args.Cancel = true; _errors.SetError(grid, ex.Message); }
        };
        grid.CellEndEdit += (_, _) => Save();
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_binding || loading) return;
            NotifyPendingChanges();
            if (grid.CurrentCell is DataGridViewCheckBoxCell && grid.IsCurrentCellDirty)
            { grid.CommitEdit(DataGridViewDataErrorContexts.Commit); Save(); }
        };
        grid.EditingControlShowing += (_, args) =>
        {
            if (args.Control is not TextBox text) return;
            text.TextChanged -= PendingTextChanged; text.TextChanged += PendingTextChanged;
        };
        void PendingTextChanged(object? sender, EventArgs args) => NotifyPendingChanges();
        _flushers.Add(Save);
        _pendingChecks.Add(() => !grid.IsDisposed && (grid.IsCurrentCellDirty || Signature() != submitted));
        _entityRebinders.Add(Refresh);
        RegisterEquipmentTypeBinding(grid, Refresh);
        Refresh();
        return grid;
    }
}
