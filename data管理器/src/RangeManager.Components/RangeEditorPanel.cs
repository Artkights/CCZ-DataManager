using Ccz.ManagerUi;

namespace RangeManager.App;

/// <summary>Both range pages share the same navigation, draft editor and geometry canvas.</summary>
public sealed class RangeEditorPanel : UserControl, IEditorViewLifecycle
{
    private readonly LayoutScheduler _layouts;
    private Action? _arrangeEditor;
    public void ActivateView() => _layouts.Activate();
    public void DeactivateView() => _layouts.Deactivate();
    public void RequestViewLayout() => _layouts.Request();
    private sealed record Choice(RangeKey Key, bool Modified)
    {
        public override string ToString() => $"{Key.Id:D2}  (0x{Key.Id:X2}){(Modified ? " *" : "")}";
    }

    private readonly RangeKind _kind;
    private RangeEditSession? _session;
    private readonly ListBox _ids = new() { Name = "RangeIds" };
    private readonly ShapeGridControl _preview = new() { Name = "RangeGeometryCanvas" };
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Black };
    private readonly Label _title = new() { AutoSize = true, Padding = new Padding(0, 3, 0, 3) };
    private readonly Label _aliases = new() { AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Visible = false };
    private readonly Label _error = new() { AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.Firebrick, Visible = false };
    private readonly Label _hint = new() { AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Visible = false };
    private readonly Label _centerFixed = new() { Name = "RangeCenterFixed", AutoSize = true };
    private readonly Label _centerHint = new() { Name = "RangeCenterHint", AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Visible = false };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    private readonly ComboBox _groups = new() { Name = "RangeGroup", DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox _center = new() { Name = "RangeCenter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly TextBox _text = new() { Name = "RangeGenerators", Dock = DockStyle.Fill, Multiline = true,
        ScrollBars = ScrollBars.Vertical, Height = 46, AcceptsReturn = true };
    private readonly CheckBox _automatic = new() { Text = "自动网格", AutoSize = true, Checked = true };
    private readonly NumericUpDown _columns = Number(7, 511, 9);
    private readonly NumericUpDown _rows = Number(7, 511, 9);
    private readonly NumericUpDown _pixels = Number(16, 64, 20);
    private readonly NumericUpDown _targetX = Number(-31, 31, 1);
    private readonly NumericUpDown _targetY = Number(-31, 31, 0);
    private readonly Button _apply = ManagerUiStyle.CreateButton("应用生成元");
    private readonly Button _restore = ManagerUiStyle.CreateButton("恢复本项");
    private readonly Button _add = ManagerUiStyle.CreateButton("新增");
    private readonly Button _delete = ManagerUiStyle.CreateButton("删除");
    private readonly Button _edit = ManagerUiStyle.CreateButton("修改");
    // Keep the Pierce geometry menu alive for the lifetime of the panel. A
    // menu disposed synchronously from its Closed event can be disposed while
    // WinForms is still unwinding the item-click message, which causes
    // ContextMenuStrip.SetVisibleCore to throw ObjectDisposedException.
    private readonly ContextMenuStrip _pierceAddMenu = new() { Name = "PierceAddMenu" };
    private readonly Dictionary<RangeKey, int> _selectedGroups = [];
    private RangePixelTiles? _tiles;
    private bool _binding, _editingText, _fittingCanvas;
    private RangeKey? _selected;
    private int _requiredColumns = 7, _requiredRows = 7;

    public RangeEditorPanel(RangeKind kind)
    {
        _kind = kind;
        AutoScaleMode = AutoScaleMode.Inherit;
        _layouts = new LayoutScheduler(this, () => _arrangeEditor?.Invoke());
        Size = new Size(1400, 760);
        Dock = DockStyle.Fill;
        _automatic.Name = "AutomaticGrid";
        _columns.Name = "GridColumns";
        _rows.Name = "GridRows";
        _pixels.Name = "GridCellPixels";
        _targetX.Name = "PreviewTargetX";
        _targetY.Name = "PreviewTargetY";
        BuildLayout();
        WireEvents();
        RefreshSelection();
    }

    public RangeKey? SelectedKey => _selected;
    public ListBox IdListForTest => _ids;
    public ShapeGridControl PreviewForTest => _preview;
    public TextBox GeneratorsForTest => _text;
    public string ValidationMessage => _error.Text;
    public CheckBox AutomaticGridForTest => _automatic;
    public NumericUpDown GridColumnsForTest => _columns;
    public NumericUpDown GridRowsForTest => _rows;
    public NumericUpDown CellPixelsForTest => _pixels;
    public NumericUpDown TargetXForTest => _targetX;
    public NumericUpDown TargetYForTest => _targetY;
    public ComboBox GroupSelectorForTest => _groups;
    public ComboBox CenterSelectorForTest => _center;
    public Label CenterHintForTest => _centerHint;
    public Label CenterFixedLabelForTest => _centerFixed;
    private int GroupIndex => Math.Max(0, _groups.SelectedIndex);
    private RangePreviewContext Context => new(new CellOffset((int)_targetX.Value, (int)_targetY.Value));

    private static NumericUpDown Number(int min, int max, int value) => new()
    {
        Minimum = min, Maximum = max, Value = value, Width = 66
    };

    private static void AddField(FlowLayoutPanel flow, string caption, Control control)
    {
        var group = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 10, 0) };
        group.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 4, 0) });
        control.Margin = new Padding(0, 0, 0, 0);
        group.Controls.Add(control);
        flow.Controls.Add(group);
    }

    private void BuildLayout()
    {
        ManagerUiStyle.ConfigureEntityList(_ids);
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var commands = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 3, RowCount = 1,
            Height = 38, Padding = new Padding(0, 6, 0, 0) };
        foreach (var button in new[] { _add, _delete, _edit })
        {
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            button.AutoSize = false;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Dock = DockStyle.Fill;
            commands.Controls.Add(button);
        }
        left.Controls.Add(_ids);
        left.Controls.Add(commands);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 8) };
        var editorViewport = new Panel { Name = "RangeEditViewport", Dock = DockStyle.Bottom, AutoScroll = true, Height = 220 };
        var editor = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        AddField(options, "编辑组", _groups);
        if (_kind == RangeKind.Pierce)
        {
            _center.Items.AddRange([CenterName(RangeCenterMode.Target), CenterName(RangeCenterMode.Self)]);
            AddField(options, "展开中心", _center);
            AddField(options, "预览目标 X", _targetX);
            AddField(options, "Y", _targetY);
        }
        else
        {
            _centerFixed.Text = "自身中心（攻击范围固定）";
            AddField(options, "展开中心", _centerFixed);
        }
        options.Controls.Add(_automatic);
        AddField(options, "列", _columns);
        AddField(options, "行", _rows);
        AddField(options, "格像素", _pixels);
        editor.Controls.Add(options);
        editor.Controls.Add(new Label { Text = "生成元（dx,dy; …）", AutoSize = true, Padding = new Padding(0, 3, 0, 0) });
        editor.Controls.Add(_text);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
        buttons.Controls.AddRange([_apply, _restore]);
        editor.Controls.Add(buttons);
        editor.Controls.Add(_aliases);
        editor.Controls.Add(_error);
        editor.Controls.Add(_centerHint);
        editor.Controls.Add(_hint);
        _viewport.Controls.Add(_preview);
        editorViewport.Controls.Add(editor);
        right.Controls.Add(_viewport);
        right.Controls.Add(editorViewport);
        right.Controls.Add(_title);
        _title.Dock = DockStyle.Top;

        bool fittingEditor = false;
        void FitEditor()
        {
            if (fittingEditor) return;
            fittingEditor = true;
            try
            {
                int available = Math.Max(80, right.ClientSize.Height - right.Padding.Vertical - _title.Height);
                // Leave the canvas at least the editor's height even when its
                // horizontal scroll bar reduces the visible drawing area.
                int maximum = Math.Max(40, (available - SystemInformation.HorizontalScrollBarHeight) / 2);
                int scrollBarHeight = editorViewport.HorizontalScroll.Visible ||
                    editor.PreferredSize.Width > editorViewport.ClientSize.Width
                    ? SystemInformation.HorizontalScrollBarHeight : 0;
                editorViewport.Height = Math.Min(maximum,
                    Math.Max(40, editor.PreferredSize.Height + scrollBarHeight + 2));
            }
            finally { fittingEditor = false; }
        }
        right.SizeChanged += (_, _) => RequestViewLayout();
        editor.SizeChanged += (_, _) => RequestViewLayout();
        editorViewport.ClientSizeChanged += (_, _) => RequestViewLayout();
        _title.SizeChanged += (_, _) => RequestViewLayout();

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1,
            Size = new Size(1400, 760), Panel1MinSize = 180, Panel2MinSize = 480, SplitterDistance = 246 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        bool fittingNavigation = false;
        var navigationDpi = DeviceDpi;
        var navigationWidth = 246 * DeviceDpi / 96;
        split.SplitterMoved += (_, _) => { if (!fittingNavigation) navigationWidth = split.SplitterDistance; };
        void FitNavigation()
        {
            if (fittingNavigation || split.Width <= split.SplitterWidth) return;
            fittingNavigation = true;
            try
            {
                var navButtons = new[] { _add, _delete, _edit };
                var preferred = navButtons.Select(button =>
                {
                    var native = button.GetPreferredSize(Size.Empty);
                    var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine);
                    return new Size(Math.Max(native.Width, text.Width + 16 * DeviceDpi / 96),
                        Math.Max(native.Height, text.Height + 8 * DeviceDpi / 96));
                }).ToArray();
                int buttonWidth = preferred.Max(size => size.Width);
                int needed = left.Padding.Horizontal + commands.Padding.Horizontal +
                    navButtons.Sum(button => buttonWidth + button.Margin.Horizontal) + 4;
                if (navigationDpi != DeviceDpi)
                {
                    navigationWidth = (int)Math.Round(navigationWidth * DeviceDpi / (double)Math.Max(1, navigationDpi));
                    navigationDpi = DeviceDpi;
                }
                int minimum = Math.Min(split.Width - split.SplitterWidth, Math.Max(180 * DeviceDpi / 96, needed));
                split.Panel2MinSize = Math.Min(480 * DeviceDpi / 96, Math.Max(0, split.Width - minimum - split.SplitterWidth));
                int available = split.Width - split.Panel2MinSize - split.SplitterWidth;
                if (split.SplitterDistance < minimum) split.SplitterDistance = minimum;
                split.Panel1MinSize = minimum;
                var distance = Math.Max(minimum, Math.Min(available, navigationWidth));
                if (split.SplitterDistance != distance) split.SplitterDistance = distance;
                commands.Height = preferred.Max(size => size.Height) + commands.Padding.Vertical;
            }
            finally { fittingNavigation = false; }
        }
        _arrangeEditor = () =>
        {
            FitNavigation();
            // Native combo boxes finish their font-dependent height after
            // FontChanged. FlowLayoutPanel's cached preferred height can be
            // shorter, so reserve the measured child height on this pass.
            foreach (var field in options.Controls.OfType<FlowLayoutPanel>())
            {
                var minimum = new Size(0, field.Controls.Cast<Control>().Select(control =>
                    control.Height + control.Margin.Vertical).DefaultIfEmpty(0).Max() + field.Padding.Vertical);
                if (field.MinimumSize != minimum) field.MinimumSize = minimum;
            }
            RefreshTextLayout();
            FitEditor();
            FitCanvas();
        };
        split.SizeChanged += (_, _) => RequestViewLayout();
        foreach (var button in new[] { _add, _delete, _edit }) button.FontChanged += (_, _) => RequestViewLayout();
        _viewport.SizeChanged += (_, _) => RequestViewLayout();
        _viewport.Layout += (_, _) => RequestViewLayout();
        RequestViewLayout();
    }

    private void WireEvents()
    {
        foreach (var label in new[] { _aliases, _error, _centerHint, _hint })
            label.TextChanged += (_, _) =>
            {
                label.Visible = !string.IsNullOrWhiteSpace(label.Text);
                _tips.SetToolTip(label, label.Text);
            };
        _ids.SelectedIndexChanged += (_, _) =>
        {
            if (_binding) return;
            _selected = (_ids.SelectedItem as Choice)?.Key;
            RefreshSelection();
        };
        _groups.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _selected == null) return;
            _selectedGroups[_selected.Value] = GroupIndex;
            RefreshSelection();
        };
        _center.SelectionChangeCommitted += (_, _) =>
        {
            if (_binding || _session == null || _selected == null || _kind != RangeKind.Pierce || _center.SelectedIndex < 0)
                return;
            var selectedCenter = _center.SelectedIndex == 1 ? RangeCenterMode.Self : RangeCenterMode.Target;
            Run(() =>
            {
                if (_session.UpdateCenter(_selected.Value, selectedCenter, out var error))
                {
                    _error.Text = "";
                    RefreshSelection();
                }
                else
                {
                    _error.Text = error;
                    RefreshSelection();
                }
            });
        };
        _text.TextChanged += (_, _) => UpdateText();
        _preview.CellEditRequested += (_, e) => Run(() =>
        {
            if (_session == null || _selected == null) return;
            _session.EditCell(_selected.Value, GroupIndex, e.Cell, Context, e.Selected);
        });
        _preview.CellHovered += (_, cell) =>
        {
            if (_session == null || _selected == null || _groups.SelectedIndex < 0) return;
            var binding = _session.Get(_selected.Value);
            var generator = NativeRangeCatalog.ToGenerator(binding, GroupIndex, cell, Context);
            if (generator == null) { _preview.SetHoverCells([]); return; }
            var definition = binding.Definition.Clone();
            definition.Groups[GroupIndex].Generators = [generator];
            var proposed = binding.Clone(definition);
            _preview.SetHoverCells(NativeRangeCatalog.ExpandGroup(proposed, GroupIndex, Context));
        };
        _apply.Click += (_, _) =>
        {
            UpdateText();
            Run(() =>
            {
                _session?.Validate();
                if (string.IsNullOrEmpty(_error.Text)) _hint.Text = "已应用到当前草稿；点击工具栏“保存”写入基底和 E5。";
            });
        };
        _restore.Click += (_, _) => Run(() => { if (_selected != null) _session?.Restore(_selected.Value); });
        _delete.Click += (_, _) => Run(() => { if (_selected != null) _session?.Delete(_selected.Value); });
        _edit.Click += (_, _) => { _text.Focus(); _text.SelectAll(); };
        if (_kind == RangeKind.Pierce)
        {
            AddPierceMenuItem("目标中心 · 镜像", RangeGeometryKind.Mirror, RangeCenterMode.Target);
            AddPierceMenuItem("目标中心 · 方向", RangeGeometryKind.Directional, RangeCenterMode.Target);
            AddPierceMenuItem("自身中心 · 镜像", RangeGeometryKind.Mirror, RangeCenterMode.Self);
            AddPierceMenuItem("自身中心 · 方向", RangeGeometryKind.Directional, RangeCenterMode.Self);
        }
        _add.Click += (_, _) =>
        {
            if (_kind == RangeKind.Attack) { AddRange(RangeGeometryKind.Mirror); return; }
            _pierceAddMenu.Show(_add, new Point(0, _add.Height));
        };
        _automatic.CheckedChanged += (_, _) => { if (!_binding) UpdatePreview(); };
        foreach (var number in new[] { _pixels, _targetX, _targetY })
            number.ValueChanged += (_, _) => { if (!_binding) UpdatePreview(); };
        foreach (var number in new[] { _columns, _rows })
            number.ValueChanged += (_, _) =>
            {
                if (_binding) return;
                bool insufficient = !_automatic.Checked && (_columns.Value < _requiredColumns || _rows.Value < _requiredRows);
                UpdatePreview();
                if (insufficient) _hint.Text = $"手动网格至少需要 {_requiredColumns}×{_requiredRows} 格；已恢复到可完整显示范围的尺寸。";
            };
    }

    public void Bind(RangeEditSession? session)
    {
        if (_session != null) _session.Changed -= SessionChanged;
        _session = session;
        _selected = null;
        if (_session != null) _session.Changed += SessionChanged;
        RefreshBindings();
    }

    private void SessionChanged(object? sender, EventArgs e) => RefreshBindings();

    public void RefreshBindings()
    {
        var previous = _selected;
        _binding = true;
        _ids.BeginUpdate();
        try
        {
            _ids.Items.Clear();
            if (_session != null)
                foreach (var row in _session.Bindings.Where(b => b.Key.Kind == _kind).OrderBy(b => b.Key.Id))
                    _ids.Items.Add(new Choice(row.Key, row.IsModified));
            int selectedIndex = -1;
            for (int i = 0; i < _ids.Items.Count; i++)
                if (((Choice)_ids.Items[i]).Key == previous) { selectedIndex = i; break; }
            _ids.SelectedIndex = selectedIndex >= 0 ? selectedIndex : _ids.Items.Count > 0 ? 0 : -1;
            _selected = (_ids.SelectedItem as Choice)?.Key;
        }
        finally { _ids.EndUpdate(); _binding = false; }
        RefreshSelection();
    }

    /// <summary>Selects the failed draft and keeps its text available for correction.</summary>
    public bool TryFocusValidationError(RangeKey key, string? message = null, int? groupIndex = null)
    {
        if (_session == null || key.Kind != _kind) return false;
        var binding = _session.Bindings.FirstOrDefault(candidate => candidate.Key == key);
        if (binding == null) return false;
        if (groupIndex is { } requested && requested >= 0 && requested < binding.Definition.Groups.Count)
            _selectedGroups[key] = requested;
        else
        {
            for (int group = 0; group < binding.Definition.Groups.Count; group++)
                if (_session.GetTextError(key, group) != null)
                {
                    _selectedGroups[key] = group;
                    break;
                }
        }
        _selected = key;
        RefreshBindings();
        if (string.IsNullOrWhiteSpace(_error.Text) && !string.IsNullOrWhiteSpace(message))
            _error.Text = message;
        if (_text.CanFocus)
        {
            _text.Focus();
            _text.SelectAll();
        }
        else _ids.Focus();
        return true;
    }

    private void RefreshSelection()
    {
        _binding = true;
        try
        {
            var binding = _selected != null ? _session?.Get(_selected.Value) : null;
            _tiles?.Dispose();
            _tiles = null;
            if (binding != null && _session != null)
            {
                try { _tiles = _session.CreatePreviewTiles(binding.Key); }
                catch (Exception ex) { _error.Text = ex.Message; }
            }
            string addError = _session?.GetAddError(_kind) ?? "";
            _add.Enabled = _session != null && addError.Length == 0;
            bool hasGroup = binding?.Definition.Groups.Count > 0;
            _delete.Enabled = binding?.Editable == true;
            _edit.Enabled = _apply.Enabled = binding?.Editable == true && hasGroup;
            _restore.Enabled = binding?.Editable == true;
            _text.ReadOnly = binding?.Editable != true || !hasGroup;
            _groups.Enabled = binding?.Definition.Groups.Count > 0;
            _title.Text = binding == null ? "打开基底后选择范围编号" : $"{binding.Key}  {binding.Name}";
            int group = binding == null ? 0 : Math.Clamp(_selectedGroups.GetValueOrDefault(binding.Key), 0, Math.Max(0, binding.Definition.Groups.Count - 1));
            _groups.Items.Clear();
            if (binding != null)
                foreach (var item in binding.Definition.Groups) _groups.Items.Add($"{item.Name}（{GeometryName(item.Geometry)}）");
            _groups.SelectedIndex = _groups.Items.Count > 0 ? group : -1;
            if (_kind == RangeKind.Pierce)
            {
                var capability = binding == null || _session == null
                    ? new RangeCenterCapability(false, "打开基底后可选择范围中心")
                    : RangeCenterCapability.Evaluate(_session.Game, binding);
                bool sessionCanChange = false;
                string sessionError = "";
                if (binding != null && _session != null)
                    sessionCanChange = _session.CanChangeCenter(binding.Key, out sessionError);
                bool canChange = binding != null && capability.CanChange && sessionCanChange;
                _center.SelectedIndex = binding == null ? -1 : binding.Center == RangeCenterMode.Self ? 1 : 0;
                _center.Enabled = canChange;
                _centerHint.Text = binding == null
                    ? capability.Reason
                    : canChange ? "可切换目标中心或自身中心"
                    : (string.IsNullOrWhiteSpace(sessionError) ? capability.Reason : sessionError);
            }
            else
            {
                _centerFixed.Text = "自身中心（攻击范围固定）";
                _centerHint.Text = "";
            }
            if (!_editingText) _text.Text = binding == null || binding.Definition.Groups.Count == 0 ? "" : _session!.GetText(binding.Key, group);
            _error.Text = binding == null || binding.Definition.Groups.Count == 0 ? "" : _session!.GetTextError(binding.Key, group) ?? "";
            var aliases = binding == null ? [] : _session!.Aliases(binding.Key).Where(b => b.Key != binding.Key).Select(b => b.Key.ToString()).ToArray();
            _aliases.Text = aliases.Length == 0 ? "" : "共享同一范围定义，修改会同步影响：" + string.Join("、", aliases);
            string baseAddHint = addError.Length == 0 ? "" : "当前基底不能新增此类范围：" + addError;
            _hint.Text = binding == null ? baseAddHint : !binding.Editable ? binding.ReadOnlyReason + (baseAddHint.Length == 0 ? "" : " " + baseAddHint) :
                !hasGroup ? "此编号为引擎保留语义，没有可编辑的几何生成元。" :
                $"左键选中，右键取消；{(binding.Center == RangeCenterMode.Self ? "以自身展开" : "以主目标展开")}。" +
                (string.IsNullOrWhiteSpace(binding.Special) ? "" : " " + binding.Special) +
                (baseAddHint.Length == 0 ? "" : " " + baseAddHint);
            _preview.Editable = binding?.Editable == true && hasGroup && string.IsNullOrEmpty(_error.Text);
            _preview.SetHoverCells([]);
        }
        finally { _binding = false; }
        RefreshTextLayout();
        UpdatePreview();
    }

    private static string GeometryName(RangeGeometryKind kind) => kind switch
    {
        RangeGeometryKind.Mirror => "镜像", RangeGeometryKind.Directional => "方向", _ => "两侧"
    };

    private void UpdateText()
    {
        if (_binding || _session == null || _selected == null || _groups.SelectedIndex < 0) return;
        _editingText = true;
        try
        {
            _session.UpdateText(_selected.Value, GroupIndex, _text.Text, out var error);
            _error.Text = error;
            _preview.Editable = _session.Get(_selected.Value).Editable && string.IsNullOrEmpty(error);
            UpdatePreview();
        }
        catch (Exception ex) { _error.Text = ex.Message; }
        finally { _editingText = false; }
    }

    private void UpdatePreview()
    {
        if (_session == null || _selected == null)
        {
            _preview.SetLayers([], []);
            _preview.SetGrid(9, 9);
            FitCanvas();
            return;
        }
        try
        {
            var binding = _session.Get(_selected.Value);
            var cells = _session.Preview(_selected.Value, Context);
            // A self-centred Pierce is placed around the attacker.  The
            // primary target remains part of the direction context, but it is
            // not an occupied/marked cell in this editor: drawing it over the
            // grid would hide a selectable Pierce cell at the same position.
            bool showTarget = _kind == RangeKind.Pierce && binding.Center == RangeCenterMode.Target;
            _preview.TargetMarker = showTarget ? Context.Target : null;
            var bounds = cells.Append(new CellOffset(0, 0))
                .Concat(showTarget ? new[] { Context.Target } : [])
                .ToArray();
            // The editable preview follows the same square contract as the
            // 64×64 E5 output.  Use one side based on the complete expanded
            // bounds so a wide or tall shape cannot produce a rectangular
            // canvas with a different hit-test transform on the other axis.
            int requiredSide = Math.Max(7, bounds.Max(c => Math.Max(Math.Abs(c.Dx), Math.Abs(c.Dy))) * 2 + 3);
            _requiredColumns = _requiredRows = requiredSide;
            _binding = true;
            try
            {
                _columns.Enabled = _rows.Enabled = !_automatic.Checked;
                int configuredSide = Math.Max((int)_columns.Value | 1, (int)_rows.Value | 1);
                int side = _automatic.Checked ? requiredSide : Math.Max(requiredSide, configuredSide);
                side = Math.Clamp(side, 7, 511) | 1;
                _columns.Value = side;
                _rows.Value = side;
                _preview.SetGrid(side, side, (int)_pixels.Value);
            }
            finally { _binding = false; }
            _preview.SetLayers(_kind == RangeKind.Attack ? cells : [], _kind == RangeKind.Pierce ? cells : [], tiles: _tiles);
            FitCanvas();
        }
        catch (Exception ex) { _error.Text = ex.Message; }
    }

    private void FitCanvas()
    {
        if (_fittingCanvas) return;
        _fittingCanvas = true;
        try
        {
            var required = _preview.GetPreferredSize(Size.Empty);
            var horizontal = false;
            var vertical = false;
            for (var pass = 0; pass < 3; pass++)
            {
                horizontal = required.Width > Math.Max(0, _viewport.Width - (vertical ? SystemInformation.VerticalScrollBarWidth : 0));
                vertical = required.Height > Math.Max(0, _viewport.Height - (horizontal ? SystemInformation.HorizontalScrollBarHeight : 0));
            }
            var size = new Size(Math.Max(required.Width, _viewport.Width - (vertical ? SystemInformation.VerticalScrollBarWidth : 0)),
                Math.Max(required.Height, _viewport.Height - (horizontal ? SystemInformation.HorizontalScrollBarHeight : 0)));
            if (_viewport.AutoScrollMinSize != required || _preview.Size != size)
            {
                _viewport.SuspendLayout();
                try { _viewport.AutoScrollMinSize = required; _preview.Size = size; }
                finally { _viewport.ResumeLayout(true); }
            }
            if (_preview.Location != _viewport.AutoScrollPosition) _preview.Location = _viewport.AutoScrollPosition;
        }
        finally { _fittingCanvas = false; }
    }

    private void RefreshTextLayout()
    {
        _text.Height = Font.Height * 2 + 10;
        foreach (var label in new[] { _aliases, _error, _centerHint, _hint }) label.Height = Font.Height + 3;
    }

    private static string CenterName(RangeCenterMode center) => center == RangeCenterMode.Self ? "自身中心" : "目标中心";

    private void AddPierceMenuItem(string text, RangeGeometryKind geometry, RangeCenterMode center)
    {
        var item = new ToolStripMenuItem(text) { Tag = (geometry, center) };
        item.Click += (_, _) =>
        {
            if (item.Tag is ValueTuple<RangeGeometryKind, RangeCenterMode> choice)
                AddRange(choice.Item1, choice.Item2);
        };
        _pierceAddMenu.Items.Add(item);
    }

    private void AddRange(RangeGeometryKind geometry, RangeCenterMode? center = null) => Run(() =>
    {
        if (_session == null) return;
        _selected = _session.Add(_kind, geometry,
            center ?? (_kind == RangeKind.Attack ? RangeCenterMode.Self : RangeCenterMode.Target));
        RefreshBindings();
        _text.Focus();
    });

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { _error.Text = ex.Message; }
    }

    public bool SelectRangeForTest(int id)
    {
        for (int i = 0; i < _ids.Items.Count; i++)
            if (((Choice)_ids.Items[i]).Key.Id == id) { _ids.SelectedIndex = i; return true; }
        return false;
    }

    public bool EditTextForTest(int id, string text)
    {
        if (!SelectRangeForTest(id)) return false;
        _text.Text = text;
        return string.IsNullOrEmpty(_error.Text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _session != null) _session.Changed -= SessionChanged;
        if (disposing) _tips.Dispose();
        if (disposing) _tiles?.Dispose();
        if (disposing) _pierceAddMenu.Dispose();
        base.Dispose(disposing);
    }
}
