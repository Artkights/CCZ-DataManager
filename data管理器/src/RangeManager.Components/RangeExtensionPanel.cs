using Ccz.ManagerUi;

namespace RangeManager.App;

/// <summary>
/// Editor for the range dispatch map.  The executable stores a native
/// 33-byte prefix, while the visible source list is the complete attack
/// directory (0x00..0x25 plus any CCZRNG03 managed records).
/// </summary>
public sealed class RangeExtensionPanel : UserControl, IEditorViewLifecycle
{
    private readonly LayoutScheduler _layouts;
    public void ActivateView() => _layouts.Activate();
    public void DeactivateView() => _layouts.Deactivate();
    public void RequestViewLayout() => _layouts.Request();

    private void ArrangeView()
    {
        var rowHeight = Math.Max(72 * DeviceDpi / 96, Font.Height + 12);
        var metric = (rowHeight, _grid.RowCount);
        if (_rowMetrics == metric) return;
        _rowMetrics = metric;
        if (_grid.RowTemplate.Height != rowHeight) _grid.RowTemplate.Height = rowHeight;
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Height != rowHeight) row.Height = rowHeight;
        PerformLayout();
    }
    private (int Height, int Rows)? _rowMetrics;
    private readonly DataGridView _grid = new BufferedGrid() { Name = "RangeExtensionMappingGrid" };
    private readonly Label _status = new() { AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly Label _hint = new() { AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };
    private readonly Button _restore = ManagerUiStyle.CreateButton("恢复本表");
    private RangeEditSession? _session;
    private bool _binding;
    private bool _refreshPending;
    private bool _refreshScheduled;
    private readonly ExtensionRangePreviewCache _previewCache = new();
    private readonly List<int> _sourceIds = [];
    private static readonly string[] RangeValueOptions = Enumerable
        .Range(0, RangeExtensionMapping.MaxRangeValue + 1)
        .Select(value => $"0x{value:X2}")
        .ToArray();

    public RangeExtensionPanel()
    {
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Inherit;
        Padding = new Padding(8);
        BuildLayout();
        _layouts = new LayoutScheduler(this, ArrangeView);
        _restore.Click += (_, _) =>
        {
            try { _session?.RestoreExtension(); RequestMappingRefresh(); }
            catch (Exception ex) { _status.Text = "恢复失败：" + ex.Message; }
        };
        _grid.CellValidating += GridCellValidating;
        _grid.EditingControlShowing += (_, e) =>
        {
            // Keep the native combo-box behavior while allowing validation to
            // reject a manually typed value during regression and keyboard
            // editing.  Selecting an item still commits the same way as a
            // DropDownList.
            if (e.Control is ExtensionRangePreviewComboBoxEditingControl combo && _grid.CurrentCell?.ColumnIndex == 2)
            {
                combo.PreviewProvider = id => _previewCache.Get(id);
                // A ComboBox commits its DataGridView value when the edit
                // ends.  Paint the adjacent thumbnail as soon as the user
                // picks an item, while leaving the session mutation to
                // CellValidating so Escape still cancels the draft.
                combo.SelectionChangeCommitted -= PreviewComboSelectionChanged;
                combo.SelectionChangeCommitted += PreviewComboSelectionChanged;
            }
        };
        // DataGridView raises CellEndEdit while it is still changing its
        // current cell.  Rebuilding Rows synchronously from that event calls
        // SetCurrentCellAddressCore recursively and throws.  Coalesce the
        // request and rebuild after the current message has returned.
        _grid.CellEndEdit += (_, _) => RequestMappingRefresh();
        _grid.CellValueChanged += (_, e) =>
        {
            if (!_binding && e.ColumnIndex == 2 && !(_grid.IsCurrentCellInEditMode))
                RefreshMappingCells();
        };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _sourceIds.Count) return;
            int id = e.ColumnIndex == 1 ? _sourceIds[e.RowIndex] :
                e.ColumnIndex == 3 && TryParseByte(Convert.ToString(_grid.Rows[e.RowIndex].Cells[2].Value) ?? "", out var value)
                    ? value : -1;
            if (id >= 0 && _previewCache.Get(id) is { } preview) e.ToolTipText = preview.Status;
        };
    }

    public DataGridView GridForTest => _grid;
    public Label StatusForTest => _status;
    public ExtensionRangePreviewCache PreviewCacheForTest => _previewCache;
    public IReadOnlyList<string> RangeValueOptionsForTest => RangeValueOptions;

    public bool TryCommitPendingEdits(out string error)
    {
        error = "";
        try
        {
            if (!_grid.EndEdit())
            {
                error = string.IsNullOrWhiteSpace(_status.Text) ? "拓展范围中存在尚未修正的输入。" : _status.Text;
                return false;
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private void BuildLayout()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.RowTemplate.Height = 72;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "RangeValue", HeaderText = "范围值", ReadOnly = true, Width = 90,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
        });
        _grid.Columns.Add(CreatePreviewColumn("RangePreview", "范围图"));
        var next = new DataGridViewComboBoxColumn
        {
            Name = "NextRange", HeaderText = "下一级范围值", Width = 150,
            DataSource = RangeValueOptions.ToArray(), FlatStyle = FlatStyle.Standard,
            ValueType = typeof(string),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = false,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            CellTemplate = new ExtensionRangePreviewComboBoxCell()
        };
        ((ExtensionRangePreviewComboBoxCell)next.CellTemplate).PreviewProvider = id => _previewCache.Get(id);
        // Re-apply the source after installing the custom cell template. Some
        // WinForms builds clear a column DataSource while cloning a derived
        // DataGridViewComboBoxCell; keeping the items on the column ensures
        // both the closed cell and the popup retain all 0x00..0xFE values.
        next.DataSource = null;
        next.Items.AddRange(RangeValueOptions);
        _grid.Columns.Add(next);
        _grid.Columns.Add(CreatePreviewColumn("NextPreview", "下一级图"));
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Chain", HeaderText = "链路预览", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 0, 8) };
        top.Controls.Add(new Label { Text = "拓展范围映射", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        top.Controls.Add(_status);
        top.Controls.Add(_hint);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_restore);
        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(top);
    }

    private static DataGridViewImageColumn CreatePreviewColumn(string name, string header)
    {
        return new DataGridViewImageColumn
        {
            Name = name,
            HeaderText = header,
            Width = 82,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                NullValue = null,
                BackColor = Color.FromArgb(245, 245, 245)
            }
        };
    }

    public void Bind(RangeEditSession? session)
    {
        if (_session != null) _session.Changed -= SessionChanged;
        _session = session;
        if (_session != null) _session.Changed += SessionChanged;
        RefreshMapping();
    }

    // Rebind values as well as repainting.  Undo/redo and restore replace the
    // session snapshot; a plain Control.Refresh would leave stale cell text in
    // the grid even though the mapping draft had already changed.
    private void SessionChanged(object? sender, EventArgs e)
    {
        if (_binding) { _grid.Invalidate(); return; }
        RequestMappingRefresh();
    }

    private void RequestMappingRefresh()
    {
        if (IsDisposed || Disposing) return;
        _refreshPending = true;
        if (!IsHandleCreated || _refreshScheduled) return;
        _refreshScheduled = true;
        try
        {
            BeginInvoke((MethodInvoker)ProcessPendingRefresh);
        }
        catch (InvalidOperationException)
        {
            // A handle may disappear between IsHandleCreated and BeginInvoke
            // while the host form is closing.  Leave the pending flag set so
            // a later Bind/handle creation can perform a normal refresh.
            _refreshScheduled = false;
        }
    }

    private void ProcessPendingRefresh()
    {
        _refreshScheduled = false;
        if (!_refreshPending || IsDisposed || Disposing) return;
        // A user may already be editing the next row when this callback is
        // delivered.  Do not discard that input, especially a value whose
        // validation was cancelled.  CellEndEdit will schedule the pending
        // refresh again after the edit is committed or cancelled.
        if (_binding || _grid.IsCurrentCellInEditMode) return;
        _refreshPending = false;
        RefreshMapping();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_refreshPending) RequestMappingRefresh();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _refreshScheduled = false;
        base.OnHandleDestroyed(e);
    }

    public void RefreshMapping()
    {
        if (IsDisposed || Disposing) return;
        // Host actions (save/undo/redo) may also request a rebind while
        // DataGridView is validating.  Apply the same edit barrier to all
        // callers, rather than guarding only CellEndEdit.
        if (_binding || _grid.IsCurrentCellInEditMode)
        {
            RequestMappingRefresh();
            return;
        }
        _refreshPending = false;
        _rowMetrics = null;

        int selectedRow = -1;
        int selectedColumn = -1;
        int firstVisibleRow = -1;
        if (_grid.CurrentCell != null)
        {
            selectedRow = _grid.CurrentCell.RowIndex;
            selectedColumn = _grid.CurrentCell.ColumnIndex;
        }
        if (_grid.Rows.Count > 0 && _grid.FirstDisplayedScrollingRowIndex >= 0)
            firstVisibleRow = _grid.FirstDisplayedScrollingRowIndex;

        _binding = true;
        try
        {
            _grid.Rows.Clear();
            _previewCache.Clear();
            _sourceIds.Clear();
            var mapping = _session?.Extension;
            if (mapping == null)
            {
                _status.Text = "打开基底后读取拓展范围映射表。";
                _hint.Text = "";
                _grid.Enabled = false;
                _restore.Enabled = false;
                return;
            }
            _grid.Enabled = mapping.Editable;
            _restore.Enabled = mapping.Editable && mapping.IsDirty;
            _status.Text = mapping.Supported
                ? $"{mapping.AddressDescription}{(mapping.IsDirty ? " · 有未保存修改" : "")}" 
                : "拓展范围只读：" + mapping.Reason;
            _hint.Text = mapping.Supported
                ? $"范围值来自当前 EXE 的完整攻击范围目录；下一级值限 0x00..0x{RangeExtensionMapping.MaxRangeValue:X2}。没有原始 E5 图片时显示按 EXE 定义生成的内存预览。"
                : "当前基底的拓展范围代码身份未匹配，保持只读，不按版本名称强行写入。";
            if (!mapping.Supported) return;
            var attacks = _session?.Bindings.Where(binding => binding.Key.Kind == RangeKind.Attack)
                .OrderBy(binding => binding.Key.Id).ToArray() ?? [];
            // Build the complete source set before calculating any chain.  If
            // this is filled row by row, the first rows incorrectly appear to
            // terminate when they point at a later (0x21+ or managed) id.
            _sourceIds.AddRange(attacks.Select(binding => binding.Key.Id));
            _previewCache.Rebuild(_session!, attacks);
            foreach (var binding in attacks)
            {
                int source = binding.Key.Id;
                int value = MappingValue(mapping, source);
                var rangePreview = _previewCache.Get(source)?.Image;
                var nextPreview = _previewCache.Get(value)?.Image;
                int row = _grid.Rows.Add($"0x{source:X2}", rangePreview, $"0x{value:X2}", nextPreview,
                    Chain(mapping, source, _sourceIds));
                _grid.Rows[row].Tag = source;
                // The native table has only 0x00..0x20 bytes, but the source
                // directory also contains real attack definitions 0x21..0x25
                // and managed 0x26+ records.  All displayed attack bindings
                // are valid mapping sources; CCZRNG03 stores the tail safely
                // in its managed table instead of extending the native bytes.
                _grid.Rows[row].Cells[2].ReadOnly = false;
            }

            if (_grid.Rows.Count > 0)
            {
                int row = Math.Clamp(selectedRow < 0 ? 0 : selectedRow, 0, _grid.Rows.Count - 1);
                int column = Math.Clamp(selectedColumn < 0 ? 1 : selectedColumn, 0, _grid.Columns.Count - 1);
                _grid.CurrentCell = _grid.Rows[row].Cells[column];
                _grid.ClearSelection();
                _grid.Rows[row].Selected = true;
                if (firstVisibleRow >= 0 && _grid.Rows.Count > 0)
                    _grid.FirstDisplayedScrollingRowIndex = Math.Clamp(firstVisibleRow, 0, _grid.Rows.Count - 1);
            }
        }
        finally { _binding = false; }
    }

    private static int MappingValue(RangeExtensionMapping mapping, int source)
        => source >= 0 && source < RangeExtensionMapping.ValueCount ? mapping.GetValue(source) : source;

    private static string Chain(RangeExtensionMapping mapping, int start, IReadOnlyCollection<int> knownIds)
    {
        var result = new List<string>();
        var seen = new HashSet<int>();
        int current = start;
        while (seen.Add(current))
        {
            result.Add($"0x{current:X2}");
            int next = MappingValue(mapping, current);
            if (!knownIds.Contains(next)) { result.Add($"0x{next:X2}"); break; }
            current = next;
        }
        if (seen.Contains(current)) result.Add("↻");
        return string.Join(" → ", result);
    }

    private void GridCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_binding || e.RowIndex < 0 || e.ColumnIndex != 2 || _session?.Extension is not { Supported: true } mapping) return;
        string text = Convert.ToString(e.FormattedValue)?.Trim() ?? "";
        if (!TryParseByte(text, out int value) || value > RangeExtensionMapping.MaxRangeValue)
        {
            e.Cancel = true;
            _status.Text = $"下一级范围值必须为 0x00..0x{RangeExtensionMapping.MaxRangeValue:X2}，0xFF 为保留哨兵。";
            return;
        }
        int index = (int)(_grid.Rows[e.RowIndex].Tag ?? -1);
        _binding = true;
        try
        {
            if (!_session.UpdateExtension(index, value, out string error))
            {
                e.Cancel = true;
                _status.Text = error;
            }
            else
            {
                RefreshMappingCells();
            }
        }
        finally { _binding = false; }
    }

    private void RefreshMappingCells()
    {
        if (_session?.Extension is not { Supported: true } mapping) return;
        for (int row = 0; row < _grid.Rows.Count && row < _sourceIds.Count; row++)
        {
            int source = _sourceIds[row];
            int value = MappingValue(mapping, source);
            _grid.Rows[row].Cells[2].Value = $"0x{value:X2}";
            _grid.Rows[row].Cells[3].Value = _previewCache.Get(value)?.Image;
            _grid.Rows[row].Cells[4].Value = Chain(mapping, source, _sourceIds);
        }
        _grid.Invalidate();
    }

    private void PreviewComboSelectionChanged(object? sender, EventArgs e)
    {
        if (_binding || sender is not ComboBox combo || _grid.CurrentCell?.ColumnIndex != 2 ||
            !TryParseByte(Convert.ToString(combo.SelectedItem) ?? combo.Text, out int value) ||
            _grid.CurrentCell.RowIndex < 0 || _grid.CurrentCell.RowIndex >= _grid.Rows.Count)
            return;
        _grid.Rows[_grid.CurrentCell.RowIndex].Cells[3].Value = _previewCache.Get(value)?.Image;
        _grid.InvalidateRow(_grid.CurrentCell.RowIndex);
    }

    private static bool TryParseByte(string text, out int value)
    {
        text = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshPending = false;
            _refreshScheduled = false;
            _previewCache.Dispose();
            if (_session != null) _session.Changed -= SessionChanged;
        }
        base.Dispose(disposing);
    }

    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() => DoubleBuffered = true;
    }
}
