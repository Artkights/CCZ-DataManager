using ImageManager.Core;

namespace ImageManager.App;

/// <summary>Maps source images explicitly, prepares actual candidate payloads, and confirms one transaction.</summary>
public sealed class ImageImportDialog : Form
{
    private readonly ProjectProfile _project;
    private readonly ImageResourceDescriptor _resource;
    private readonly ImageImportOperation _operation;
    private readonly IReadOnlyList<LogicalImageAsset> _targets;
    private readonly IImageImportPreparationService _preparation;
    private readonly List<SourceSlot> _slots = [];
    private readonly List<string> _pendingPaths = [];
    private readonly DataGridView _mapping = Grid();
    private readonly DataGridView _results = Grid();
    private readonly ListBox _pending = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill, PlaceholderText = "可粘贴素材目录；自动识别仍会保留冲突供手动分配" };
    private readonly ComboBox _naming = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 315 };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4) };
    private readonly Button _prepare = new() { Text = "准备转换并预览", AutoSize = true, MinimumSize = new Size(148, 34) };
    private readonly Button _confirm = new() { Text = "确认写入", AutoSize = true, MinimumSize = new Size(96, 34), Enabled = false };
    private readonly PictureBox _before = Preview();
    private readonly PictureBox _sourcePreview = Preview();
    private readonly PictureBox _after = Preview();
    private readonly Dictionary<string, Bitmap> _sourceImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox _previewMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 126 };
    private readonly NumericUpDown _frame = new() { Minimum = 0, Maximum = 0, Width = 68 };
    private PreparedImageImport? _prepared;
    private CancellationTokenSource? _cancellation;
    private long _revision;
    private long _preparedRevision = -1;
    private bool _populating;
    private bool _updatingPreview;
    private bool _closed;
    private string? _lastFolder;
    private string _sourcePreviewNotice = "";

    public ImageImportDialog(ProjectProfile project, ImageResourceDescriptor resource, ImageImportOperation operation,
        IReadOnlyList<LogicalImageAsset>? targets = null, IImageImportPreparationService? preparation = null)
    {
        _project = project;
        _resource = resource;
        _operation = operation;
        _targets = targets ?? [];
        _preparation = preparation ?? new ImageImportPreparationService();
        Text = (operation == ImageImportOperation.Append ? "新增图片" : "替换图片") + " — " + resource.DisplayName;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10F);
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(780, 560);
        AllowDrop = true;
        BuildLayout();
        if (operation == ImageImportOperation.Replace)
        {
            for (var index = 0; index < _targets.Count; index++) AddSlots(_targets[index], index);
        }
        else AddSlots(null, 0);
        PopulateMapping();
        SetStatus("先选择图片或目录并核对分配，再准备转换。确认写入前不会更改项目。" + Rules());
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            foreach (var path in paths)
                if (Directory.Exists(path)) ScanDirectory(path); else if (ImageImportSourceDiscovery.IsImage(path)) AddPending([path]);
        };
        Shown += (_, _) => FitWorkingArea();
        DpiChanged += (_, _) => { if (IsHandleCreated) BeginInvoke((Action)FitWorkingArea); };
        FormClosing += (_, _) => { _closed = true; _revision++; _cancellation?.Cancel(); };
    }

    /// <summary>The immutable candidate plan remains usable after the dialog's preview bitmaps are disposed.</summary>
    public MutationPlan? ResultPlan { get; private set; }
    public PreparedImageImport? PreparedResult => _prepared;

    private bool IsRs => _resource.Kind is ResourceKind.RComposite or ResourceKind.SComposite;
    private bool IsPaired => _resource.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon;
    private RsImportNaming Naming => _naming.SelectedIndex == 1 ? RsImportNaming.Legacy : RsImportNaming.Physical;

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.SizeChanged += (_, _) => root.RowStyles[2].Height = Math.Clamp(root.ClientSize.Height / 9F, 56, 96);
        Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, AutoSize = true };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "素材目录", AutoSize = true, Margin = new Padding(4, 8, 8, 4) }, 0, 0);
        header.Controls.Add(_folder, 1, 0);
        var browse = Button("浏览并识别", () => { using var dialog = new FolderBrowserDialog { Description = "选择素材目录（冲突项保留为待分配图片）", UseDescriptionForTitle = true };
            if (Directory.Exists(_folder.Text)) dialog.SelectedPath = _folder.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) ScanDirectory(dialog.SelectedPath); });
        header.Controls.Add(browse, 2, 0);
        var options = Flow();
        options.Controls.Add(Button("识别粘贴目录", () => ScanDirectory(_folder.Text.Trim())));
        options.Controls.Add(Button("添加图片到待分配", ChooseFiles));
        if (_resource.Kind == ResourceKind.SComposite)
        {
            _naming.Items.AddRange(["物理编号：将领 S_242 / 兵种 S_002", "旧编号：S002 → 242 / 兵种S001 → 2"]);
            _naming.SelectedIndex = 0;
            _naming.SelectedIndexChanged += (_, _) =>
            {
                InvalidatePrepared();
                SetStatus("编号格式已改变。已分配素材保持；请重新识别目录并核对物理目标。旧编号不会自动推测。");
            };
            options.Controls.Add(_naming);
        }
        header.Controls.Add(options, 0, 1); header.SetColumnSpan(options, 3);
        root.Controls.Add(header, 0, 0);
        var mappingTab = new TabPage("1 素材映射") { Padding = new Padding(4), AutoScroll = true };
        var previewTab = new TabPage("2 转换预览") { Padding = new Padding(4), AutoScroll = true };
        AddScrollableContent(mappingTab, BuildMapping(), 320);
        AddScrollableContent(previewTab, BuildResults(), 240);
        _tabs.TabPages.Add(mappingTab); _tabs.TabPages.Add(previewTab);
        root.Controls.Add(_tabs, 0, 1);
        root.Controls.Add(_details, 0, 2);
        root.Controls.Add(_status, 0, 3);
        var actions = Flow(); actions.FlowDirection = FlowDirection.RightToLeft;
        var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(82, 34), DialogResult = DialogResult.Cancel };
        _prepare.Click += async (_, _) => await PrepareAsync();
        _confirm.Click += (_, _) => Confirm();
        actions.Controls.Add(cancel); actions.Controls.Add(_confirm); actions.Controls.Add(_prepare);
        root.Controls.Add(actions, 0, 4);
        AcceptButton = _prepare; CancelButton = cancel;
    }

    private Control BuildMapping()
    {
        _mapping.Name = "ImageImportMappingGrid";
        _mapping.MultiSelect = true;
        _mapping.Columns.Add(new DataGridViewCheckBoxColumn { Name = "include", HeaderText = "导入", Width = 55 });
        AddColumn(_mapping, "group", "组", 45);
        AddColumn(_mapping, "target", "目标 / 物理编号", 205);
        AddColumn(_mapping, "role", "动作 / 规格", 180);
        _mapping.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "素材路径（可直接粘贴）", Width = 360, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 160 });
        _mapping.Columns.Add(new DataGridViewButtonColumn { Name = "browse", HeaderText = "选图", Text = "选择…", UseColumnTextForButtonValue = true, Width = 65 });
        AddColumn(_mapping, "state", "映射状态", 215);
        _mapping.CurrentCellDirtyStateChanged += (_, _) => { if (_mapping.IsCurrentCellDirty) _mapping.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _mapping.CellValueChanged += (_, e) => MappingChanged(e.RowIndex, e.ColumnIndex);
        _mapping.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _mapping.Columns[e.ColumnIndex].Name != "browse") return;
            using var dialog = FileDialog(false);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            Remember(dialog.FileName);
            var slot = (SourceSlot)_mapping.Rows[e.RowIndex].Tag!;
            Assign(slot, dialog.FileName); PopulateMapping();
        };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_mapping, 0, 0);
        var tools = Flow();
        tools.Controls.Add(Button("清空所选槽", () => { foreach (var slot in SelectedSlots()) { slot.SourcePath = ""; slot.Status = "未分配"; } InvalidatePrepared(); PopulateMapping(); }));
        tools.Controls.Add(Button("排除所选槽", () => { foreach (var slot in SelectedSlots()) slot.Included = false; InvalidatePrepared(); PopulateMapping(); }));
        if (_operation == ImageImportOperation.Append)
        {
            tools.Controls.Add(Button("增加一组", () => { AddSlots(null, _slots.Select(slot => slot.Group).DefaultIfEmpty(-1).Max() + 1); InvalidatePrepared(); PopulateMapping(); }));
            tools.Controls.Add(Button("本组上移", () => MoveGroup(-1)));
            tools.Controls.Add(Button("本组下移", () => MoveGroup(1)));
        }
        root.Controls.Add(tools, 0, 1);
        var pool = new GroupBox { Text = "待分配图片（任意文件名；按此列表的可见顺序分配）", Dock = DockStyle.Fill };
        pool.Controls.Add(_pending); root.Controls.Add(pool, 0, 2);
        var poolTools = Flow();
        poolTools.Controls.Add(Button("分配到当前槽", AssignCurrent));
        poolTools.Controls.Add(Button("按列表顺序分配到空槽", AssignInOrder));
        poolTools.Controls.Add(Button("同一图片应用到所选槽", ReuseSelected));
        poolTools.Controls.Add(Button("文件名排序", () => { _pendingPaths.Sort(StringComparer.OrdinalIgnoreCase); PopulatePending(); }));
        poolTools.Controls.Add(Button("移除待分配项", () => { foreach (var path in SelectedPending()) _pendingPaths.Remove(path); PopulatePending(); }));
        root.Controls.Add(poolTools, 0, 3);
        return root;
    }

    private Control BuildResults()
    {
        _results.Name = "ImageImportResultGrid";
        _results.MinimumSize = new Size(0, 80);
        _results.Margin = Padding.Empty;
        _results.ReadOnly = true; _results.MultiSelect = false;
        AddColumn(_results, "target", "目标", 175);
        AddColumn(_results, "role", "动作", 65);
        AddColumn(_results, "source", "素材", 150);
        AddColumn(_results, "size", "源 → 输出", 145);
        AddColumn(_results, "sourceFormat", "素材格式 / 色深", 135);
        AddColumn(_results, "format", "目标格式", 140);
        AddColumn(_results, "state", "状态 / 处理", 260);
        _results.SelectionChanged += (_, _) => UpdatePreview();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_results, 0, 0);
        var controls = Flow();
        _previewMode.Items.AddRange(["整条 / 整图", "逐帧"]); _previewMode.SelectedIndex = 0;
        _previewMode.SelectedIndexChanged += (_, _) => UpdatePreview();
        _frame.ValueChanged += (_, _) => UpdatePreview();
        controls.Controls.Add(new Label { Text = "预览", AutoSize = true, Margin = new Padding(4, 8, 4, 4) });
        controls.Controls.Add(_previewMode);
        controls.Controls.Add(new Label { Text = "第0帧起", AutoSize = true, Margin = new Padding(8, 8, 4, 4) });
        controls.Controls.Add(_frame);
        controls.Controls.Add(Button("排除当前来源并返回映射", ExcludeCurrentResult));
        root.Controls.Add(controls, 0, 1);
        var images = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        for (var index = 0; index < 3; index++) images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
        images.RowStyles.Add(new RowStyle(SizeType.AutoSize)); images.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        images.Controls.Add(new Label { Text = "当前目标", AutoSize = true }, 0, 0);
        images.Controls.Add(new Label { Text = "源图（红框为裁切范围）", AutoSize = true }, 1, 0);
        images.Controls.Add(new Label { Text = "转换后（实际候选载荷）", AutoSize = true }, 2, 0);
        foreach (var (picture, column) in new[] { (_before, 0), (_sourcePreview, 1), (_after, 2) })
        {
            var viewport = PreviewImageLayout.CreateViewport(picture);
            viewport.MinimumSize = new Size(0, 96);
            images.Controls.Add(viewport, column, 1);
        }
        root.Controls.Add(images, 0, 2);
        return root;
    }

    private static void AddScrollableContent(TabPage page, Control content, int minimumHeight)
    {
        content.Dock = DockStyle.Top; content.MinimumSize = new Size(0, minimumHeight);
        page.Controls.Add(content);
        void Arrange() => content.Height = Math.Max(content.MinimumSize.Height, page.ClientSize.Height - page.Padding.Vertical);
        page.ClientSizeChanged += (_, _) => Arrange();
        Arrange();
    }

    private void AddSlots(LogicalImageAsset? target, int group)
    {
        var roles = _resource.Kind switch
        {
            ResourceKind.RComposite => new[] { "front", "back" },
            ResourceKind.SComposite => new[] { "mov", "atk", "spc" },
            ResourceKind.FaceComposite when target?.IsFixedSlot == true => Enumerable.Range(0, 8).Select(index => $"expression:{index}").ToArray(),
            _ when IsPaired => new[] { "large", "small" },
            _ => new[] { "image" }
        };
        foreach (var role in roles) _slots.Add(new SourceSlot(target, role, group)
        { Included = role != "small" && (!role.StartsWith("expression:", StringComparison.Ordinal) || role == "expression:0") });
    }

    private void PopulateMapping()
    {
        _populating = true;
        try
        {
            _mapping.Rows.Clear();
            foreach (var slot in _slots.OrderBy(slot => slot.Group))
            {
                var rowIndex = _mapping.Rows.Add(slot.Included, slot.Group + 1, TargetLabel(slot), RoleLabel(slot.Role), slot.SourcePath, null, slot.Status);
                _mapping.Rows[rowIndex].Tag = slot;
            }
        }
        finally { _populating = false; }
    }

    private void MappingChanged(int rowIndex, int columnIndex)
    {
        if (_populating || rowIndex < 0 || columnIndex < 0) return;
        var row = _mapping.Rows[rowIndex];
        if (row.Tag is not SourceSlot slot) return;
        var column = _mapping.Columns[columnIndex].Name;
        if (column == "include") slot.Included = Convert.ToBoolean(row.Cells[columnIndex].Value);
        else if (column == "source")
        {
            slot.SourcePath = Convert.ToString(row.Cells[columnIndex].Value)?.Trim().Trim('"') ?? "";
            slot.Included = slot.SourcePath.Length > 0;
            slot.Status = slot.SourcePath.Length > 0 ? "已分配，待准备" : "未分配";
            _populating = true;
            row.Cells["include"].Value = slot.Included;
            row.Cells["state"].Value = slot.Status;
            _populating = false;
        }
        else return;
        InvalidatePrepared();
    }

    private void ChooseFiles()
    {
        using var dialog = FileDialog(true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Remember(dialog.FileNames[0]); AddPending(dialog.FileNames);
        SetStatus("图片已加入待分配列表。可逐槽选择，也可明确按列表顺序分配；文件名不会决定物理编号。");
    }

    private OpenFileDialog FileDialog(bool multi) => new()
    {
        Title = "选择素材图片（文件名可任意）", CheckFileExists = true, Multiselect = multi,
        Filter = IsRs ? ImageImportSourceDiscovery.RsFileFilter : ImageImportSourceDiscovery.FileFilter,
        InitialDirectory = _lastFolder ?? _project.RootPath
    };

    private void Remember(string path) => _lastFolder = Path.GetDirectoryName(path);
    private void AddPending(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (IsRs && !ImageImportSourceDiscovery.IsRsImage(path)) continue;
            if (!_pendingPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) _pendingPaths.Add(path);
        }
        PopulatePending();
    }
    private void PopulatePending()
    {
        _pending.BeginUpdate(); _pending.Items.Clear();
        foreach (var path in _pendingPaths) _pending.Items.Add(path);
        _pending.EndUpdate();
    }
    private string[] SelectedPending() => _pending.SelectedItems.Cast<string>().ToArray();
    private SourceSlot[] SelectedSlots() => _mapping.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
        .Select(row => (SourceSlot)row.Tag!).ToArray();
    private SourceSlot? CurrentSlot() => _mapping.CurrentRow?.Tag as SourceSlot;
    private void Assign(SourceSlot slot, string path)
    {
        slot.SourcePath = path; slot.Included = true; slot.Status = "已明确分配，待准备"; InvalidatePrepared();
    }
    private void AssignCurrent()
    {
        var paths = SelectedPending(); var slot = CurrentSlot();
        if (paths.Length != 1 || slot == null) { SetStatus("请选择一张待分配图片和一个目标槽。"); return; }
        Assign(slot, paths[0]); PopulateMapping();
    }
    private void ReuseSelected()
    {
        var paths = SelectedPending(); var slots = SelectedSlots();
        if (paths.Length != 1 || slots.Length == 0) { SetStatus("请选择一张待分配图片，并在上表选择要共用它的多个目标槽。"); return; }
        foreach (var slot in slots) Assign(slot, paths[0]);
        PopulateMapping(); SetStatus($"已明确将同一图片分配到 {slots.Length} 个槽；准备时会逐槽校验规格和物理目标。");
    }
    private void AssignInOrder()
    {
        var paths = SelectedPending(); if (paths.Length == 0) paths = _pendingPaths.ToArray();
        if (paths.Length == 0) { SetStatus("待分配列表为空，请先添加图片。"); return; }
        var empty = _slots.Where(slot => slot.Included && string.IsNullOrWhiteSpace(slot.SourcePath)).OrderBy(slot => slot.Group).ToList();
        if (_operation == ImageImportOperation.Append)
        {
            while (empty.Count < paths.Length)
            {
                AddSlots(null, _slots.Select(slot => slot.Group).DefaultIfEmpty(-1).Max() + 1);
                empty = _slots.Where(slot => slot.Included && string.IsNullOrWhiteSpace(slot.SourcePath)).OrderBy(slot => slot.Group).ToList();
            }
        }
        var count = Math.Min(paths.Length, empty.Count);
        for (var index = 0; index < count; index++) Assign(empty[index], paths[index]);
        PopulateMapping();
        SetStatus($"已按待分配列表的可见顺序分配 {count} 张。请逐行核对目标；尚有 {paths.Length - count} 张未分配。");
    }

    private void MoveGroup(int direction)
    {
        var slot = CurrentSlot(); if (slot == null) return;
        var groups = _slots.Select(item => item.Group).Distinct().Order().ToArray();
        var index = Array.IndexOf(groups, slot.Group); var other = index + direction;
        if (other < 0 || other >= groups.Length) return;
        var first = groups[index]; var second = groups[other];
        foreach (var item in _slots)
            if (item.Group == first) item.Group = second; else if (item.Group == second) item.Group = first;
        InvalidatePrepared(); PopulateMapping(); SetStatus("追加组顺序已改变；请重新准备以核对新编号。");
    }

    private void ScanDirectory(string root)
    {
        if (!Directory.Exists(root)) { SetStatus("目录不存在，请检查素材路径。"); return; }
        _folder.Text = root; _lastFolder = root;
        InvalidatePrepared();
        _pendingPaths.Clear(); PopulatePending();
        if (_operation == ImageImportOperation.Replace)
            foreach (var slot in _slots) { slot.SourcePath = ""; slot.Status = "未匹配，可手动分配"; }
        var messages = new List<string>();
        try
        {
            if (IsRs && _operation == ImageImportOperation.Append)
            {
                var directories = ImageImportSourceDiscovery.AppendDirectories(root, _resource.Kind);
                _slots.Clear();
                if (directories.Length == 0) messages.Add("未发现已知动作名；图片仍可加入待分配列表后手动指定。");
                for (var index = 0; index < directories.Length; index++)
                {
                    AddSlots(null, index);
                    FillRoles(directories[index], _slots.Where(slot => slot.Group == index), messages);
                }
                if (_slots.Count == 0) AddSlots(null, 0);
            }
            else if (IsRs)
            {
                foreach (var target in _targets)
                {
                    var directories = ImageImportSourceDiscovery.TargetDirectories(root, target, Naming, _targets.Count == 1);
                    if (directories.Length != 1)
                    {
                        messages.Add($"{target.DisplayName}：{(directories.Length == 0 ? "没有匹配目录" : "匹配多个目录，须手动选择")}。");
                        if (directories.Length > 1)
                            foreach (var slot in _slots.Where(slot => slot.Target == target)) slot.Status = "多个候选目录，须手动选择";
                        foreach (var directory in directories) AddPending(ImageImportSourceDiscovery.Files(directory));
                        continue;
                    }
                    var source = directories[0];
                    if (_resource.Kind == ResourceKind.SComposite && Naming == RsImportNaming.Legacy)
                    {
                        if (new[] { "turn2", "turn3" }.Any(name => Directory.Exists(Path.Combine(source, name))))
                        {
                            messages.Add($"{target.DisplayName}：旧多转目录须明确选择其中一转的动作条；不会展开写入多个物理号。");
                            foreach (var slot in _slots.Where(slot => slot.Target == target)) slot.Status = "旧多转目录，须手动选择";
                            continue;
                        }
                        var turn1 = Path.Combine(source, "turn1");
                        if (Directory.Exists(turn1))
                        {
                            if (ImageImportSourceDiscovery.Files(source).Any(ImageImportSourceDiscovery.IsRsImage))
                            {
                                messages.Add($"{target.DisplayName}：根目录和turn1同时有图片，须明确选择来源。");
                                foreach (var slot in _slots.Where(slot => slot.Target == target)) slot.Status = "根目录与turn1冲突，须手动选择";
                                AddPending(ImageImportSourceDiscovery.Files(turn1)); continue;
                            }
                            source = turn1;
                        }
                    }
                    FillRoles(source, _slots.Where(slot => slot.Target == target), messages);
                }
            }
            else
            {
                var files = ImageImportSourceDiscovery.Files(root); AddPending(files);
                foreach (var slot in _slots)
                {
                    if (slot.Role.StartsWith("expression:", StringComparison.Ordinal)) continue;
                    var names = slot.Target == null ? new[] { slot.Role } :
                        new[] { slot.Target.DisplayName + (IsPaired ? "_" + slot.Role : ""), slot.Target.LogicalId.ToString("D3") + (IsPaired ? "_" + slot.Role : "") };
                    var candidates = files.Where(path => names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
                    FillSlot(slot, candidates, messages);
                }
            }
            AddPending(ImageImportSourceDiscovery.Files(root));
            foreach (var child in Directory.EnumerateDirectories(root)) AddPending(ImageImportSourceDiscovery.Files(child));
            PopulateMapping();
            var assigned = _slots.Count(slot => slot.SourcePath.Length > 0 && slot.Included);
            SetStatus($"目录识别完成；当前 {assigned} 个槽已有素材。未知文件名保留在待分配列表，冲突项须明确选择。" + Rules());
            _details.Text = messages.Count == 0 ? "未发现歧义。请检查全部目标和动作，再准备转换。" : string.Join(Environment.NewLine, messages);
        }
        catch (Exception ex) { SetStatus("目录识别失败；尚未写入项目。"); _details.Text = ex.Message; PopulateMapping(); }
    }

    private void FillRoles(string directory, IEnumerable<SourceSlot> slots, List<string> messages)
    {
        AddPending(ImageImportSourceDiscovery.Files(directory));
        foreach (var slot in slots) FillSlot(slot, ImageImportSourceDiscovery.Candidates(directory, slot.Role), messages);
    }
    private void FillSlot(SourceSlot slot, string[] candidates, List<string> messages)
    {
        if (candidates.Length == 1) Assign(slot, candidates[0]);
        else if (candidates.Length > 1)
        {
            slot.SourcePath = ""; slot.Status = "多个候选，须手动选择";
            messages.Add($"{TargetLabel(slot)} / {slot.Role}：{string.Join("；", candidates)}");
        }
        else { slot.Status = slot.SourcePath.Length > 0 ? "保留原分配；本目录未匹配" : "未匹配，可手动分配"; }
    }

    private async Task PrepareAsync()
    {
        _mapping.EndEdit();
        InvalidatePrepared();
        var unresolved = _slots.Where(slot => slot.Included && slot.Status.Contains("须手动选择", StringComparison.Ordinal)).ToArray();
        if (unresolved.Length > 0)
        {
            _tabs.SelectedIndex = 0;
            _details.Text = string.Join(Environment.NewLine, unresolved.Select(slot => $"{TargetLabel(slot)} / {slot.Role}：多个候选。请选定素材，或取消勾选以排除该槽。"));
            SetStatus("存在未解决的素材冲突；请在映射表选择来源或排除问题槽。");
            return;
        }
        var revision = _revision;
        var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        _prepare.Text = "重新准备";
        SetStatus("正在后台解码、转换并校验候选载荷…可继续调整映射或取消。");
        var assignments = _slots.OrderBy(slot => slot.Group).Select(slot =>
            new ImageImportAssignment(slot.Target, slot.Role, slot.SourcePath, slot.Group,
                slot.Included && (_operation == ImageImportOperation.Append || !string.IsNullOrWhiteSpace(slot.SourcePath)))).ToArray();
        try
        {
            var prepared = await _preparation.PrepareAsync(new ImageImportRequest(_project, _resource, _operation, assignments), cancellation.Token);
            if (_closed || IsDisposed || cancellation.IsCancellationRequested || revision != _revision) { prepared.Dispose(); return; }
            _prepared = prepared; _preparedRevision = revision;
            _confirm.Enabled = prepared.CanCommit && prepared.Plan != null;
            PopulateResults(); _tabs.SelectedIndex = 1;
            SetStatus(_confirm.Enabled ? "转换完成。请检查实际候选预览及物理目标，再确认写入。" : "存在阻断项或没有可写入素材。可排除问题来源、返回映射并重新准备。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed && !IsDisposed && revision == _revision)
            { _details.Text = ex.ToString(); SetStatus("准备失败，尚未写入。请修正素材或排除问题行后重试。"); }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
        }
    }

    private void PopulateResults()
    {
        _results.Rows.Clear();
        if (_prepared == null) return;
        foreach (var row in _prepared.Rows)
        {
            var index = _results.Rows.Add(row.TargetLabel, row.Role, Path.GetFileName(row.SourcePath),
                $"{FormatSize(row.SourceSize)} → {FormatSize(row.OutputSize)}", row.SourceFormat,
                $"{row.OriginalFormat} → {row.OutputFormat}", string.IsNullOrWhiteSpace(row.Error) ? row.Notes : row.Error);
            _results.Rows[index].Tag = row;
            if (!string.IsNullOrWhiteSpace(row.Error)) _results.Rows[index].DefaultCellStyle.ForeColor = Color.Firebrick;
        }
        if (_results.Rows.Count > 0) { _results.CurrentCell = _results.Rows[0].Cells[0]; _results.Rows[0].Selected = true; }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_updatingPreview || _closed || IsDisposed) return;
        _updatingPreview = true;
        try
        {
        var index = _results.CurrentRow?.Index ?? -1;
        if (_prepared == null || index < 0 || index >= _prepared.Rows.Count) { ClearPreviews(); return; }
        var row = _prepared.Rows[index];
        var spec = row.Role switch { "front" or "back" => (48, 64, 20), "mov" => (48, 48, 11), "atk" => (64, 64, 12), "spc" => (48, 48, 5), _ => (0, 0, 1) };
        var count = spec.Item3;
        _frame.Enabled = _previewMode.SelectedIndex == 1 && count > 1;
        if (_frame.Maximum != count - 1) { _frame.Maximum = count - 1; if (_frame.Value > _frame.Maximum) _frame.Value = _frame.Maximum; }
        ReplacePreview(_before, PreviewImage(row.Before, spec, _previewMode.SelectedIndex == 1, (int)_frame.Value));
        ReplacePreview(_sourcePreview, SourcePreview(row, spec));
        ReplacePreview(_after, PreviewImage(row.After, spec, _previewMode.SelectedIndex == 1, (int)_frame.Value));
        _details.Text = $"目标：{row.TargetLabel} / {row.Role}{Environment.NewLine}素材：{row.SourcePath}{Environment.NewLine}" +
            $"源尺寸：{FormatSize(row.SourceSize)}；源格式/色深：{row.SourceFormat}；输出尺寸：{FormatSize(row.OutputSize)}；目标存储：{row.OriginalFormat} → {row.OutputFormat}{Environment.NewLine}" +
            $"处理：{row.Notes}{Environment.NewLine}" +
            (row.SourceCrop is { } crop ? $"源裁切范围：X={crop.X}，Y={crop.Y}，宽={crop.Width}，高={crop.Height}{Environment.NewLine}" : "") +
            (string.IsNullOrWhiteSpace(row.Error) ? "" : "阻断：" + row.Error) +
            (_sourcePreviewNotice.Length == 0 ? "" : Environment.NewLine + _sourcePreviewNotice);
        }
        finally { _updatingPreview = false; }
    }

    private Bitmap? SourcePreview(ImageImportPreviewRow row, (int Width, int Height, int Count) spec)
    {
        _sourcePreviewNotice = "";
        if (string.IsNullOrWhiteSpace(row.SourcePath)) return null;
        try
        {
            var path = Path.GetFullPath(row.SourcePath);
            var snapshots = _prepared?.Plan?.SourceSnapshots;
            if (snapshots == null || !snapshots.TryGetValue(path, out var snapshot)) return null;
            snapshot.Verify(path);
            if (!_sourceImages.TryGetValue(row.SourcePath, out var source))
            {
                source = ImageImportSourceDecoder.Decode(File.ReadAllBytes(row.SourcePath));
                try { snapshot.Verify(path); }
                catch { source.Dispose(); throw; }
                _sourceImages.Add(row.SourcePath, source);
            }
            var preview = PreviewImage(source, spec, _previewMode.SelectedIndex == 1, (int)_frame.Value)!;
            if (row.SourceCrop is { } crop && crop.Width > 0 && crop.Height > 0)
            {
                using var graphics = Graphics.FromImage(preview);
                using var pen = new Pen(Color.Red, Math.Max(1F, Math.Min(preview.Width, preview.Height) / 160F));
                graphics.DrawRectangle(pen, crop.X, crop.Y, Math.Max(0, crop.Width - 1), Math.Max(0, crop.Height - 1));
            }
            return preview;
        }
        catch (IOException)
        {
            _sourcePreviewNotice = "源文件已变化或无法读取，请重新准备。";
            _confirm.Enabled = false; _preparedRevision = -1;
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        { _sourcePreviewNotice = "源图暂时无法预览；请检查素材并重新准备。"; return null; }
    }

    private static Bitmap? PreviewImage(Bitmap? source, (int Width, int Height, int Count) spec, bool single, int frame)
    {
        if (source == null) return null;
        if (!single || spec.Count == 1 || source.Width != spec.Width || source.Height != spec.Height * spec.Count) return (Bitmap)source.Clone();
        return source.Clone(new Rectangle(0, frame * spec.Height, spec.Width, spec.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    }
    private void ExcludeCurrentResult()
    {
        var index = _results.CurrentRow?.Index ?? -1;
        if (_prepared == null || index < 0 || index >= _prepared.Rows.Count) return;
        var path = _prepared.Rows[index].SourcePath;
        if (string.IsNullOrWhiteSpace(path)) { _tabs.SelectedIndex = 0; SetStatus("请在映射表排除或修正缺少素材的目标槽。"); return; }
        foreach (var slot in _slots.Where(slot => slot.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase))) slot.Included = false;
        InvalidatePrepared(); PopulateMapping(); _tabs.SelectedIndex = 0;
        SetStatus("已排除此来源的全部映射。请重新准备；不完整的追加组仍会阻断。");
    }
    private void Confirm()
    {
        if (_prepared == null || !_prepared.CanCommit || _prepared.Plan == null || _preparedRevision != _revision || _closed) return;
        ResultPlan = _prepared.Plan; DialogResult = DialogResult.OK; Close();
    }
    private void InvalidatePrepared()
    {
        _revision++; _cancellation?.Cancel(); _confirm.Enabled = false; _preparedRevision = -1; ResultPlan = null;
        ClearPreviews(); _results.Rows.Clear(); _prepared?.Dispose(); _prepared = null;
        foreach (var image in _sourceImages.Values) image.Dispose(); _sourceImages.Clear();
    }
    private void ClearPreviews() { ReplacePreview(_before, null); ReplacePreview(_sourcePreview, null); ReplacePreview(_after, null); }
    private static void ReplacePreview(PictureBox box, Bitmap? image) { var previous = box.Image; box.Image = image; previous?.Dispose(); }

    private string TargetLabel(SourceSlot slot)
    {
        if (slot.Target != null)
        {
            if (slot.Target.Kind == ResourceKind.SComposite)
            {
                if (slot.Target.SMode == SImageMode.Job) return slot.Target.DisplayName;
                return SResourceExchangeNaming.Category(slot.Target) + "/" + SResourceExchangeNaming.BaseName(slot.Target) +
                    (slot.Target.JobId.HasValue ? $" · 兵种{slot.Target.JobId} {new[] { "我军", "友军", "敌军" }[slot.Target.FactionIndex ?? 0]}" : "");
            }
            return slot.Target.DisplayName;
        }
        return $"追加组 {slot.Group + 1}（新编号见转换预览）";
    }
    private static string RoleLabel(string role) => role switch
    {
        "front" => "正面 front · 48×1280", "back" => "背面 back · 48×1280",
        "mov" => "移动 mov · 48×528", "atk" => "攻击 atk · 64×768", "spc" => "特殊 spc · 48×240",
        "large" => "大图（自动适配）", "small" => "小图（可选，未给则派生）",
        _ when role.StartsWith("expression:", StringComparison.Ordinal) => $"表情{role["expression:".Length..]}（中心裁切）",
        _ => "图片"
    };
    private static string FormatSize(Size size) => size.IsEmpty ? "—" : $"{size.Width}×{size.Height}";
    private string Rules() => IsRs ? " R/S素材限BMP/PNG/JPG/JPEG，保持标准竖条尺寸与帧数；替换可缺动作，追加须完整组。" : IsPaired
        ? " 图标自动适配，未选小图时由大图最近邻派生；预览分别显示大小图。"
        : _resource.Kind == ResourceKind.FaceComposite ? " 头像按目标画布居中裁切；Face0可分别指定表情0～7，只写入已分配表情及对应Tou；裁切与结果见预览。"
        : _resource.Kind == ResourceKind.DllBitmap ? " DLL图片接受任意尺寸，自动适配目标MOD画布与原DIB编码；新增按目标模板转换，实际结果见预览。"
        : _resource.Kind == ResourceKind.MapPair ? " 地图底图接受任意尺寸，自动转换为当前地图的JPG画布；本入口只改底图，地形格使用“导入地图与地形”。"
        : _resource.Kind == ResourceKind.WeatherSpecial ? " 天气新增与替换自动适配216×200整条并按目标调色板编码；固定4帧，可末尾新增，禁止删除或重命名编号。"
        : _resource.Kind == ResourceKind.PmapSpecial ? " Pmap新增与替换自动适配目标MOD画布和调色板；保留已验证的16字节专用头，禁止删除/重命名编号，像素编辑暂不可用。"
        : " 普通E5图片接受任意尺寸，自动适配目标MOD的画布、透明规则和存储编码；新增按目标模板转换，实际结果见预览。";
    private void SetStatus(string text) => _status.Text = text;
    private void FitWorkingArea()
    {
        var area = (Owner == null ? Screen.FromControl(this) : Screen.FromControl(Owner)).WorkingArea;
        var maximum = new Size(Math.Max(640, area.Width - 32), Math.Max(440, area.Height - 32));
        MinimumSize = new Size(Math.Min(MinimumSize.Width, maximum.Width), Math.Min(MinimumSize.Height, maximum.Height));
        Size = new Size(Math.Min(Width, maximum.Width), Math.Min(Height, maximum.Height));
    }
    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        BackgroundColor = SystemColors.Window, RowTemplate = { Height = 28 }, ShowCellToolTips = true
    };
    private static void AddColumn(DataGridView grid, string name, string title, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, Width = width, ReadOnly = true });
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(2) };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(65, 30), Margin = new Padding(3) };
        button.Click += (_, _) => action(); return button;
    }
    private static PictureBox Preview() => new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(229, 232, 236), BorderStyle = BorderStyle.FixedSingle };
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true; _revision++; _cancellation?.Cancel();
            ClearPreviews(); _prepared?.Dispose(); _prepared = null;
            foreach (var image in _sourceImages.Values) image.Dispose(); _sourceImages.Clear();
        }
        base.Dispose(disposing);
    }
    private sealed class SourceSlot(LogicalImageAsset? target, string role, int group)
    {
        internal LogicalImageAsset? Target { get; } = target;
        internal string Role { get; } = role;
        internal int Group { get; set; } = group;
        internal bool Included { get; set; } = true;
        internal string SourcePath { get; set; } = "";
        internal string Status { get; set; } = "未分配";
    }
}
