using System.Data;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Migration;
using DataManager.Core.TableDefinitions;
using DataManager.Images;
using ImageManager.App;

namespace DataManager.App;

public sealed partial class MainForm : Form
{
    private readonly Icon _applicationIcon;
    private ProjectWorkspace _workspace = new();
    private readonly ListBox _moduleList = new();
    private readonly TabControl _moduleTabs = new() { Dock = DockStyle.Fill, Height = 34 };
    private readonly ListBox _entityList = new();
    private readonly SearchableIdComboBox _quickLookup = new() { Width = 260, Visible = false };
    private readonly ComboBox _tableSelector = new();
    private readonly ComboBox _profileSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _variantSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly DataGridView _grid = new BufferedDataGridView();
    private readonly Label _scanLabel = new();
    private readonly ToolTip _shellTips = new() { AutoPopDelay = 20000 };
    private readonly Label _dirtyLabel = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _diagnosticBox = new();
    private readonly PictureBox _resourcePreview = new() { Dock = DockStyle.Right, Width = 220, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(248, 248, 248), Visible = false };
    private readonly TextBox _historyFilter = new() { Width = 260, PlaceholderText = "按时间、模块、文件或字段筛选历史", Visible = false };
    private readonly CheckBox _hideEmpty = new() { Text = "隐藏空白行", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "保存", AutoSize = true };
    private readonly Button _undoButton = new() { Text = "撤回", AutoSize = true };
    private readonly Button _redoButton = new() { Text = "前进", AutoSize = true };
    private readonly Button _historyUndoButton = new() { Text = "历史撤回", AutoSize = true };
    private readonly Button _historyRedoButton = new() { Text = "历史前进", AutoSize = true };
    private readonly Button _batchButton = new() { Text = "批量修改", AutoSize = true };
    private readonly Button _importButton = new() { Text = "导入", AutoSize = true };
    private readonly Button _exportButton = new() { Text = "导出", AutoSize = true };
    private readonly Button _scanButton = new() { Text = "重新扫描", AutoSize = true };
    private readonly Button _migrationButton = new() { Text = "一键迁移", AutoSize = true };
    private readonly Button _diagnosticsButton = new() { Text = "诊断详情", AutoSize = true };
    private readonly Button _resourceAddButton = new() { Text = "资源新增", AutoSize = true, Visible = false };
    private readonly Button _resourceReplaceButton = new() { Text = "资源替换", AutoSize = true, Visible = false };
    private readonly Button _resourceDeleteButton = new() { Text = "资源删除", AutoSize = true, Visible = false };
    private TableDocument? _currentDocument;
    private string? _currentModule;
    private readonly Dictionary<(DataRow Row, string Column), object?> _editingValues = new();
    private bool _suppressGridEvents;
    private bool _suppressProfileEvents;
    private bool _resourceModule;
    private ResourceCatalogAdapter? _resourceAdapter = new();
    private BusinessEditorPanel _businessEditor;
    private readonly Dictionary<BusinessPageKind, BusinessEditorPanel> _businessEditors = [];
    private readonly Dictionary<string, string> _moduleTables = new(StringComparer.Ordinal);
    private string? _activeModuleKey;
    private bool _bindingModule;
    private readonly IntegratedResourcePanel _imageEditor;
    private BusinessPageKind? _businessKind;
    private bool _selectingBusinessEntity;
    private bool _refreshingResourceCategories;
    private bool _restoringTableSelection;
    private readonly Dictionary<string, UnifiedHistoryEntry> _historyEntries = new(StringComparer.Ordinal);
    private ByteTextInputGuard? _gridTextGuard;
    private TextBoxBase? _guardedGridEditor;
    private readonly Label _gridBudget = new() { Dock = DockStyle.Bottom, Height = 26, Visible = false, TextAlign = ContentAlignment.MiddleLeft };
    private bool HasPendingChanges => _workspace.IsDirty || _businessEditors.Values.Any(editor => editor.HasPendingChanges) || _grid.IsCurrentCellDirty || (_gridTextGuard?.HasPendingChanges == true && _grid.IsCurrentCellInEditMode);

    public MainForm(bool openDefaultProject = true)
    {
        _applicationIcon = LoadApplicationIcon();
        Icon = _applicationIcon;
        _imageEditor = new IntegratedResourcePanel(_resourceAdapter) { Visible = false };
        _businessEditor = CreateBusinessEditor();
        _businessEditors[BusinessPageKind.Persons] = _businessEditor;
        _imageEditor.CategoriesChanged += () => RefreshResourceCategories();
        Text = $"data 与形象管理器 - 曹操传加强版 6.5/6.6 · {Application.ProductVersion.Split('+')[0]}";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1500;
        Height = 900;
        MinimumSize = new Size(1100, 650);
        KeyPreview = true;
        FormClosing += OnFormClosing;
        BuildLayout();
        WireEvents();
        if (openDefaultProject)
            Shown += (_, _) =>
            {
                // Shown can be queued after a caller has already loaded an
                // explicit project. Never replace that selection at startup.
                if (string.IsNullOrWhiteSpace(_workspace.RootPath)) OpenDefaultOrPrompt();
            };
    }

    private static Icon LoadApplicationIcon()
    {
        const string resourceName = "DataManager.App.Assets.About.Doro-white.ico";
        Icon? associated = null;
        try
        {
            // The apphost carries the ICO selected by ApplicationIcon.  Use it
            // first so Explorer, the taskbar and the running window all share
            // the exact shell icon selected for the published executable.
            associated = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            // When the UI is hosted by the test runner or `dotnet`, its
            // associated icon belongs to that host rather than this app.
            if (!Path.GetFileName(Application.ExecutablePath).Equals("data管理器.exe", StringComparison.OrdinalIgnoreCase))
            {
                associated?.Dispose();
                associated = null;
            }
        }
        catch { /* fall through to the embedded copy */ }
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            associated?.Dispose();
            return (Icon)SystemIcons.Application.Clone();
        }
        using var embedded = new Icon(stream);
        // A test host or an unpacked development DLL can have an unrelated
        // associated icon.  The apphost's ApplicationIcon is accepted when
        // it carries the same ICO size; otherwise retain the embedded source
        // that is guaranteed to travel with the single-file executable.
        if (associated != null && associated.Size == embedded.Size) return associated;
        associated?.Dispose();
        return (Icon)embedded.Clone();
    }

    private void BuildLayout()
    {
        var toolStrip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(8, 4, 8, 3),
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(245, 247, 250)
        };
        foreach (var button in new[] { _saveButton, _undoButton, _redoButton, _historyUndoButton, _historyRedoButton, _batchButton, _importButton, _exportButton, _scanButton, _migrationButton, _diagnosticsButton, _resourceAddButton, _resourceReplaceButton, _resourceDeleteButton })
        {
            button.Margin = new Padding(0, 0, 6, 0);
            toolStrip.Controls.Add(button);
        }
        var rootButton = new Button { Text = "打开项目", AutoSize = true, Margin = new Padding(18, 0, 6, 0) };
        rootButton.Click += (_, _) => OpenProjectFromDialog();
        toolStrip.Controls.Add(rootButton);
        var settingsStrip = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 1, 8, 1),
            ColumnCount = 9, RowCount = 1, BackColor = Color.FromArgb(245, 247, 250)
        };
        foreach (var width in new[] { 42, 165, 48, 195, 108, 68, 170, 28 })
            settingsStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        settingsStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsStrip.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        settingsStrip.Controls.Add(new Label { Text = "版本", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 4, 0) }, 0, 0);
        settingsStrip.Controls.Add(_profileSelector, 1, 0);
        settingsStrip.Controls.Add(new Label { Text = "布局", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 4, 0) }, 2, 0);
        settingsStrip.Controls.Add(_variantSelector, 3, 0);
        settingsStrip.Controls.Add(_hideEmpty, 4, 0);
        settingsStrip.Controls.Add(new Label { Text = "快速定位", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 4, 0) }, 5, 0);
        settingsStrip.Controls.Add(_quickLookup, 6, 0);
        settingsStrip.Controls.Add(new Label { Text = "表", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 4, 0) }, 7, 0);
        settingsStrip.Controls.Add(_tableSelector, 8, 0);
        foreach (var selector in new ComboBox[] { _profileSelector, _variantSelector, _quickLookup, _tableSelector })
        {
            selector.Dock = DockStyle.Fill;
            selector.Margin = new Padding(0, 3, 4, 3);
        }
        _hideEmpty.Anchor = AnchorStyles.Left;
        _hideEmpty.Margin = new Padding(6, 0, 0, 0);
        _tableSelector.DropDownStyle = ComboBoxStyle.DropDownList;

        foreach (var profile in _workspace.AvailableProfiles)
        {
            _profileSelector.Items.Add(new ProfileChoice(profile.Id, profile.DisplayName));
        }

        var header = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 1, 8, 1) };
        _moduleTabs.Dock = DockStyle.Fill;
        _moduleTabs.Multiline = false;
        _moduleTabs.Appearance = TabAppearance.FlatButtons;
        _moduleTabs.ItemSize = new Size(120, 28);
        _moduleTabs.SizeMode = TabSizeMode.FillToRight;
        header.Controls.Add(_moduleTabs);

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        left.Controls.Add(_entityList);
        _entityList.Dock = DockStyle.Fill;
        _entityList.Font = new Font(Font.FontFamily, 10.5f);
        _entityList.SelectionMode = SelectionMode.One;
        _entityList.HorizontalScrollbar = true;
        _moduleList.Visible = false;
        _moduleList.Font = new Font(Font.FontFamily, 10.5f);
        _moduleList.SelectionMode = SelectionMode.One;

        var rightHeader = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 24, WrapContents = false, Padding = new Padding(4, 1, 4, 0) };
        rightHeader.Controls.Add(_dirtyLabel);
        _dirtyLabel.AutoSize = true;
        _dirtyLabel.ForeColor = Color.DarkOrange;
        rightHeader.Controls.Add(_statusLabel);
        _statusLabel.AutoSize = false;
        _statusLabel.Size = new Size(620, 24);
        _statusLabel.AutoEllipsis = true;
        rightHeader.Controls.Add(_historyFilter);
        void FitShellText()
        {
            foreach (var column in new[] { 0, 2, 4, 5, 7 }) settingsStrip.ColumnStyles[column].SizeType = SizeType.AutoSize;
            settingsStrip.Height = Math.Max(34 * DeviceDpi / 96, _profileSelector.PreferredHeight + 8);
            toolStrip.Height = Math.Max(38 * DeviceDpi / 96, toolStrip.Controls.Cast<Control>().Select(control => control.PreferredSize.Height).DefaultIfEmpty(28).Max() + toolStrip.Padding.Vertical);
            header.Height = Math.Max(34 * DeviceDpi / 96, _moduleTabs.Font.Height + 15);
            _moduleTabs.ItemSize = new Size(_moduleTabs.ItemSize.Width, Math.Max(28 * DeviceDpi / 96, _moduleTabs.Font.Height + 10));
            rightHeader.Height = Math.Max(24 * DeviceDpi / 96, Font.Height + 6);
        }
        FontChanged += (_, _) => FitShellText();
        HandleCreated += (_, _) => FitShellText();
        FitShellText();

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.RowHeadersWidth = 44;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        SelectionAppearance.Configure(_grid, preserveCurrentRow: true);

        _diagnosticBox.Dock = DockStyle.Bottom;
        _diagnosticBox.Height = 86;
        _diagnosticBox.Multiline = true;
        _diagnosticBox.ReadOnly = true;
        _diagnosticBox.ScrollBars = ScrollBars.Vertical;
        _diagnosticBox.BackColor = Color.FromArgb(250, 250, 250);
        _diagnosticBox.Visible = false;

        var editor = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 8) };
        editor.Controls.Add(_resourcePreview);
        editor.Controls.Add(_grid);
        editor.Controls.Add(_businessEditor);
        editor.Controls.Add(_imageEditor);
        editor.Controls.Add(_diagnosticBox);
        editor.Controls.Add(_gridBudget);
        _gridBudget.SizeChanged += (_, _) => RefreshGridBudget();
        editor.Controls.Add(rightHeader);

        var split = new SplitContainer { Size = new Size(Width - 24, Height - 180), Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, IsSplitterFixed = true };
        split.Panel1MinSize = 246;
        split.SplitterDistance = 246;
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(editor);

        Controls.Add(split);
        Controls.Add(header);
        Controls.Add(settingsStrip);
        Controls.Add(toolStrip);
        _scanLabel.Text = "未打开项目";
        _dirtyLabel.Text = "无未保存修改";
        _statusLabel.Text = "就绪";
    }

    private void WireEvents()
    {
        _moduleList.SelectedIndexChanged += (_, _) => SelectModule();
        _moduleTabs.SelectedIndexChanged += (_, _) => { if (!_bindingModule) SelectModuleTab(); };
        _moduleTabs.Selecting += (_, e) => { if (!_bindingModule && !PreparePendingEdits()) e.Cancel = true; };
        _entityList.SelectedIndexChanged += (_, _) => SelectEntity();
        _entityList.MouseMove += (_, args) =>
        {
            var index = _entityList.IndexFromPoint(args.Location);
            var text = _resourceModule && index >= 0 && _entityList.Items[index] is ResourceChoice resource
                ? resource.Name : string.Empty;
            if (_shellTips.GetToolTip(_entityList) != text) _shellTips.SetToolTip(_entityList, text);
        };
        _entityList.MouseLeave += (_, _) => _shellTips.SetToolTip(_entityList, string.Empty);
        _tableSelector.SelectedIndexChanged += (_, _) => { if (!_bindingModule) SelectTable(); };
        _hideEmpty.CheckedChanged += (_, _) => ApplyEmptyFilter();
        _grid.CellBeginEdit += GridCellBeginEdit;
        _grid.CellValidating += GridCellValidating;
        _grid.CellValueChanged += GridCellValueChanged;
        _grid.EditingControlShowing += GridEditingControlShowing;
        _grid.CellEndEdit += (_, _) => { _gridTextGuard?.MarkSubmitted(); _gridBudget.Visible = false; };
        _grid.SelectionChanged += (_, _) =>
        {
            if (!_resourceModule) ShowCurrentFieldDetails();
        };
        _grid.KeyDown += GridKeyDown;
        _grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            SetStatus("输入值格式不正确，已保留原值。", true);
        };
        _saveButton.Click += (_, _) => SaveProject();
        _undoButton.Click += (_, _) => DraftUndo();
        _redoButton.Click += (_, _) => DraftRedo();
        _historyUndoButton.Click += (_, _) => HistoryUndo();
        _historyRedoButton.Click += (_, _) => HistoryRedo();
        _batchButton.Click += (_, _) => OpenBatchDialog();
        _importButton.Click += (_, _) => ImportCsv();
        _exportButton.Click += (_, _) => ExportCsv();
        _scanButton.Click += (_, _) => Rescan();
        _migrationButton.Click += (_, _) => RunMigrationWizard();
        _diagnosticsButton.Click += (_, _) => _diagnosticBox.Visible = !_diagnosticBox.Visible;
        _resourceAddButton.Click += (_, _) => MutateResourceAppend();
        _resourceReplaceButton.Click += (_, _) => MutateResourceReplace();
        _resourceDeleteButton.Click += (_, _) => MutateResourceDelete();
        _historyFilter.TextChanged += (_, _) => ApplyHistoryFilter();
        _profileSelector.SelectedIndexChanged += (_, _) => ProfileSelectionChanged();
        _variantSelector.SelectedIndexChanged += (_, _) => VariantSelectionChanged();
        _quickLookup.UnknownValueSubmitted += (_, id) =>
        {
            if (_resourceModule) return;
            SelectEntityById(id);
        };
        _quickLookup.TextUpdate += (_, _) =>
        {
            if (_selectingBusinessEntity || _businessKind != BusinessPageKind.ExtendData || _businessEditor.ExtendEditor is not { } panel) return;
            panel.SearchText = _quickLookup.Text;
            RefreshExtendEntityList();
        };
    }

    private void RunMigrationWizard()
    {
        if (!PreparePendingEdits()) return;
        using var sourceDialog = new FolderBrowserDialog { Description = "选择迁移源基底（源 → 目标）" };
        if (sourceDialog.ShowDialog(this) != DialogResult.OK) return;
        using var targetDialog = new FolderBrowserDialog { Description = "选择迁移目标基底（源 → 目标）" };
        if (targetDialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var sourcePath = MigrationService.ResolveProjectRoot(sourceDialog.SelectedPath);
            var targetPath = MigrationService.ResolveProjectRoot(targetDialog.SelectedPath);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "源基底和目标基底不能相同。", "一键迁移", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var sourceIsOpen = string.Equals(_workspace.RootPath, sourcePath, StringComparison.OrdinalIgnoreCase);
            var targetIsOpen = string.Equals(_workspace.RootPath, targetPath, StringComparison.OrdinalIgnoreCase);
            if ((sourceIsOpen || targetIsOpen) && HasPendingChanges && !ConfirmDiscardOrSave("迁移当前项目")) return;
            var service = new MigrationService(new ImageMigrationResourceConverter());
            var sourceProfile = sourceIsOpen ? _workspace.ActiveProfile.ProfileId : null;
            var sourceVariant = sourceIsOpen ? _workspace.ActiveProfile.VariantId : null;
            var targetProfile = targetIsOpen ? _workspace.ActiveProfile.ProfileId : null;
            var targetVariant = targetIsOpen ? _workspace.ActiveProfile.VariantId : null;
            var plan = MigrationProgressDialog.Run(this, "正在分析迁移差异", (progress, cancellationToken) =>
                service.Preview(sourcePath, targetPath, sourceProfile, sourceVariant, targetProfile, targetVariant, progress, cancellationToken));
            var changed = plan.WritableChanges.Count;
            var incompatible = plan.Changes.Count(change => change.Kind is MigrationChangeKind.Incompatible or MigrationChangeKind.MissingSourceTable or MigrationChangeKind.MissingTargetTable);
            var ignored = plan.IgnoredChanges.Count;
            var resources = plan.Changes.Count(change => change.Kind is MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged);
            var preview = string.Join(Environment.NewLine, plan.Changes.Take(18).Select(change =>
                $"{change.TableKey} ID={change.Id} {change.FieldName}: {change.DisplayValue(change.TargetValue)} -> {change.DisplayValue(change.SourceValue)}"));
            var message = $"源：{plan.SourceProfileId}/{plan.SourceVariantId}{Environment.NewLine}" +
                          $"目标：{plan.TargetProfileId}/{plan.TargetVariantId}{Environment.NewLine}" +
                          $"可迁移差异：{changed}（资源 {resources}）；不可映射/缺失：{incompatible}；整段忽略：{ignored}{Environment.NewLine}{Environment.NewLine}" +
                          (preview.Length == 0 ? "没有可展示的差异。" : preview) +
                          (plan.Changes.Count > 18 ? Environment.NewLine + "……其余差异请查看迁移报告。" : string.Empty) +
                          Environment.NewLine + Environment.NewLine +
                          "进入逐项选择后，默认全部跳过；目标目录会先创建备份。";
            if (MessageBox.Show(this, message, "迁移差异预览", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            using var review = new MigrationReviewDialog(plan);
            if (review.ShowDialog(this) != DialogResult.OK) return;
            if (review.Choices.Count == 0)
            {
                SetStatus("迁移已取消：未选择任何需要应用的差异。");
                return;
            }
            var choices = review.Choices.ToDictionary(pair => pair.Key, pair => pair.Value);
            var result = MigrationProgressDialog.Run(this, "正在执行迁移", (progress, cancellationToken) =>
                service.Apply(plan, choices, progress: progress, cancellationToken: cancellationToken));
            var resultText = string.Join(Environment.NewLine, result.Messages);
            resultText = $"已应用 {result.AppliedChanges:N0} 项，跳过 {result.SkippedChanges:N0} 项。" + Environment.NewLine + resultText;
            if (result.Success && incompatible > 0)
                resultText += Environment.NewLine + $"本次仅完成所选兼容项；仍有 {incompatible:N0} 项不可映射/缺失，未自动写入。具体原因已保存在迁移日志的 Unmapped 中。";
            if (result.Success && ignored > 0)
                resultText += Environment.NewLine + $"有 {ignored:N0} 个逻辑段按迁移策略或目标支持能力忽略，未写入目标。详情已保存在迁移日志的 Ignored 中。";
            MessageBox.Show(this, resultText + (result.BackupPath == null ? string.Empty : Environment.NewLine + $"备份：{result.BackupPath}"),
                result.Success ? "所选迁移项已完成" : "迁移失败", MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            if (result.Success && string.Equals(_workspace.RootPath, plan.TargetRoot, StringComparison.OrdinalIgnoreCase))
            {
                var report = _workspace.Open(plan.TargetRoot, plan.TargetProfileId, plan.TargetVariantId);
                ApplyOpenedProject(report, plan.TargetRoot);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("迁移已取消，目标未提交修改。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"迁移未完成：{ex.Message}", "一键迁移", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? GetSelectedResourceKey()
    {
        if (!_resourceModule || _grid.CurrentRow == null || !_grid.Columns.Contains("资源键"))
        {
            MessageBox.Show(this, "请先在形象设定页选择一个资源。", "资源操作", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        var key = Convert.ToString(_grid.CurrentRow.Cells["资源键"].Value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private void MutateResourceAppend()
    {
        var key = GetSelectedResourceKey();
        if (key == null || string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        using var dialog = new OpenFileDialog { Multiselect = true, Filter = "图片素材|*.bmp;*.png;*.jpg;*.jpeg;*.gif;*.tif;*.tiff;*.ico;*.webp|动画资源包|*.e5;*.bin|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var result = (_resourceAdapter ??= new ResourceCatalogAdapter()).Append(_workspace.RootPath, key, dialog.FileNames);
            ShowMutationResult(result);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "资源新增失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void MutateResourceReplace()
    {
        var key = GetSelectedResourceKey();
        if (key == null || string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        var idsText = PromptText("要替换的逻辑 ID（多个用逗号分隔）", "0");
        if (idsText == null) return;
        var ids = ParseIds(idsText);
        if (ids.Count == 0) return;
        using var dialog = new OpenFileDialog { Multiselect = true, Filter = "图片素材|*.bmp;*.png;*.jpg;*.jpeg;*.gif;*.tif;*.tiff;*.ico;*.webp|动画资源包|*.e5;*.bin|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FileNames.Length != ids.Count)
        {
            if (dialog.FileNames.Length > 0) MessageBox.Show(this, "素材数量必须与逻辑 ID 数量一致。", "资源替换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var result = (_resourceAdapter ??= new ResourceCatalogAdapter()).Replace(_workspace.RootPath, key, ids, dialog.FileNames);
            ShowMutationResult(result);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "资源替换失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void MutateResourceDelete()
    {
        var key = GetSelectedResourceKey();
        if (key == null || string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        var idsText = PromptText("要删除的逻辑 ID（多个用逗号分隔）", "0");
        if (idsText == null) return;
        var ids = ParseIds(idsText);
        if (ids.Count == 0) return;
        if (MessageBox.Show(this, "删除会使后续资源编号前移，外部 Data/策略/地图引用不会自动更新。继续？", "资源删除确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        try
        {
            var result = (_resourceAdapter ??= new ResourceCatalogAdapter()).Delete(_workspace.RootPath, key, ids);
            ShowMutationResult(result);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "资源删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ShowMutationResult(ImageManager.Core.MutationResult result)
    {
        MessageBox.Show(this, string.Join(Environment.NewLine, result.Diagnostics.DefaultIfEmpty(result.Summary)),
            result.Success ? "资源操作完成" : "资源操作失败", MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        if (result.Success) ShowResourceView();
    }

    private static IReadOnlyList<int> ParseIds(string text)
        => text.Split([',', '，', ';', '；', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1)
            .Where(id => id >= 0).Distinct().ToArray();

    private string? PromptText(string caption, string initial)
    {
        using var dialog = new Form { Text = caption, Width = 460, Height = 140, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var box = new TextBox { Dock = DockStyle.Top, Text = initial, Margin = new Padding(8) };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        panel.Controls.Add(cancel); panel.Controls.Add(ok);
        dialog.Controls.Add(panel); dialog.Controls.Add(box); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    private void OpenDefaultOrPrompt() => OpenProjectFromDialog();

    private void OpenProjectFromDialog()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择曹操传加强版 6.5 或 6.6 项目目录" };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenProject(dialog.SelectedPath);
    }

    private void OpenProject(string path)
    {
        if (!PreparePendingEdits()) return;
        if (HasPendingChanges && !ConfirmDiscardOrSave("切换项目")) return;
        try
        {
            var candidates = ProjectRootLocator.GetCandidates(path);
            if (candidates.Count > 1)
            {
                using var choose = new ProjectRootSelectionDialog(candidates);
                if (choose.ShowDialog(this) != DialogResult.OK) return;
                path = choose.SelectedRoot;
            }
            var loaded = ProjectLoadDialog.LoadWorkspace(this, path);
            if (loaded == null) return;
            _workspace = loaded;
            ApplyOpenedProject(loaded.LastScan!, loaded.RootPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开项目失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyOpenedProject(ScanReport report, string path)
    {
        _imageEditor.ClearProject();
        ResetBusinessEditors();
        _gridTextGuard?.Dispose(); _gridTextGuard = null; _guardedGridEditor = null;
        _editingValues.Clear();
        _currentDocument = null;
        _grid.DataSource = null;
        UpdateScanSummary(report);
        _diagnosticBox.Text = FormatScanDiagnostics(report, 20);
        SyncProfileSelectors();
        _bindingModule = true;
        try
        {
            _moduleList.Items.Clear();
            foreach (var module in _workspace.Modules) _moduleList.Items.Add(module.DisplayName);
            foreach (var tab in _moduleTabs.TabPages.Cast<TabPage>().ToArray()) tab.Dispose();
            _moduleTabs.TabPages.Clear();
            foreach (var module in _workspace.Modules)
                _moduleTabs.TabPages.Add(new TabPage(module.DisplayName) { Tag = module.Key });
            _moduleTabs.TabPages.Add(new TabPage("形象设定") { Tag = "__resources" });
            _moduleTabs.TabPages.Add(new TabPage("历史记录") { Tag = "__history" });
            _moduleTabs.TabPages.Add(new TabPage("扫描诊断") { Tag = "__scan" });
            if (_moduleTabs.TabPages.Count > 0) _moduleTabs.SelectedIndex = 0;
        }
        finally { _bindingModule = false; }
        if (_moduleTabs.TabPages.Count > 0)
        {
            _moduleTabs.SelectedIndex = 0;
            SelectModuleTab();
        }
        SetStatus($"已打开：{path}");
        RefreshDirtyState();
    }

    private void UpdateScanSummary(ScanReport report)
    {
        _scanLabel.Text = $"{report.ProfileDisplayName} / {report.VariantDisplayName} / {report.ProfileMatch?.MatchText} / {report.ProfileMatch?.DetectionMethod}：{report.Summary}";
        _shellTips.SetToolTip(_profileSelector, _scanLabel.Text);
        _shellTips.SetToolTip(_variantSelector, _scanLabel.Text);
        _shellTips.SetToolTip(_diagnosticsButton, _scanLabel.Text);
    }

    private string FormatScanDiagnostics(ScanReport report, int? limit = null)
        => $"程序：{Environment.ProcessPath}\r\n构建：{Application.ProductVersion}；布局：{_workspace.ActiveProfile.LayoutRevision}\r\n实际游戏目录：{_workspace.RootPath}\r\n" +
            _scanLabel.Text + Environment.NewLine + report.DisplayMode + Environment.NewLine +
            string.Join(Environment.NewLine, limit.HasValue ? report.Diagnostics.Take(limit.Value) : report.Diagnostics);

    private void SelectModuleTab()
    {
        if (_moduleTabs.SelectedTab?.Tag is not string key) return;
        if (_activeModuleKey == key) return;
        if (!PreparePendingEdits()) return;
        if (_activeModuleKey != null && _tableSelector.SelectedItem is TableChoice oldTable)
            _moduleTables[_activeModuleKey] = oldTable.Key;
        _activeModuleKey = key;
        RememberGridWidths();
        var layoutHost = _grid.Parent!;
        layoutHost.SuspendLayout();
        try
        {
        _businessKind = null;
        if (key != "__resources")
        {
            var oldPreview = _resourcePreview.Image;
            _resourcePreview.Image = null;
            oldPreview?.Dispose();
        }
        _resourceModule = key == "__resources";
        _resourcePreview.Visible = false;
        _resourceAddButton.Visible = false;
        _resourceReplaceButton.Visible = false;
        _resourceDeleteButton.Visible = false;
        _quickLookup.Visible = !_resourceModule && key is not "__history" and not "__scan";
        if (key == "__history")
        {
            _businessEditor.Visible = false; _imageEditor.Visible = false; _grid.Visible = true;
            _currentModule = null;
            _currentDocument = null;
            _tableSelector.Items.Clear();
            _tableSelector.Enabled = false;
            _entityList.Items.Clear();
            ShowHistoryView();
            return;
        }
        if (key == "__scan")
        {
            _businessEditor.Visible = false; _imageEditor.Visible = false; _grid.Visible = true;
            _currentModule = null;
            _currentDocument = null;
            _tableSelector.Items.Clear();
            _tableSelector.Enabled = false;
            _entityList.Items.Clear();
            ShowScanView();
            return;
        }
        if (_resourceModule)
        {
            _currentModule = null;
            _currentDocument = null;
            _tableSelector.Items.Clear();
            _tableSelector.Enabled = false;
            _entityList.Items.Clear();
            ShowResourceView();
            return;
        }
        var module = _workspace.Modules.FirstOrDefault(item => item.Key == key);
        if (module == null) return;
        _tableSelector.Enabled = true;
        _currentModule = module.Key;
        _tableSelector.Items.Clear();
        if (GetBusinessKind(module.Key) != null) _tableSelector.Items.Add(new TableChoice("__business", "业务详情（关联物理表）"));
        foreach (var tableKey in module.TableKeys)
        {
            var definition = _workspace.Definitions.FirstOrDefault(item => item.Key == tableKey);
            if (definition != null)
                _tableSelector.Items.Add(new TableChoice(tableKey, definition.DisplayName));
        }
        if (_tableSelector.Items.Count > 0)
        {
            _moduleTables.TryGetValue(key, out var savedTable);
            var selectedIndex = _tableSelector.Items.Cast<TableChoice>().ToList().FindIndex(choice => choice.Key == savedTable);
            _tableSelector.SelectedIndex = Math.Max(0, selectedIndex);
        }
        else ShowNoTable();
        }
        finally { layoutHost.ResumeLayout(true); }
    }

    private void SelectModule()
    {
        if (_moduleList.SelectedIndex < 0) return;
        if (_moduleList.SelectedIndex == _workspace.Modules.Count)
        {
            _currentModule = null;
            _currentDocument = null;
            _tableSelector.Items.Clear();
            _tableSelector.Enabled = false;
            ShowHistoryView();
            return;
        }
        if (_moduleList.SelectedIndex == _workspace.Modules.Count + 1)
        {
            _currentModule = null;
            _currentDocument = null;
            _tableSelector.Items.Clear();
            _tableSelector.Enabled = false;
            ShowScanView();
            return;
        }
        _tableSelector.Enabled = true;
        _currentModule = _workspace.Modules[_moduleList.SelectedIndex].Key;
        _tableSelector.Items.Clear();
        foreach (var key in _workspace.Modules[_moduleList.SelectedIndex].TableKeys)
        {
            var definition = _workspace.Definitions.FirstOrDefault(item => item.Key == key);
            if (definition != null)
                _tableSelector.Items.Add(new TableChoice(key, definition.DisplayName));
        }
        if (_tableSelector.Items.Count > 0) _tableSelector.SelectedIndex = 0;
        else ShowNoTable();
    }

    private void SelectTable()
    {
        if (_restoringTableSelection) return;
        if (!PreparePendingEdits())
        {
            _restoringTableSelection = true;
            try
            {
                var previous = _businessEditor.Visible ? "__business" : _currentDocument?.Definition.Key;
                _tableSelector.SelectedItem = _tableSelector.Items.Cast<object>().FirstOrDefault(item => item is TableChoice choice && choice.Key == previous);
            }
            finally { _restoringTableSelection = false; }
            return;
        }
        _resourceModule = false;
        _resourcePreview.Visible = false;
        if (_tableSelector.SelectedItem is TableChoice { Key: "__business" } && GetBusinessKind(_currentModule) is { } kind)
        {
            ShowBusinessView(kind);
            return;
        }
        _businessKind = null;
        _businessEditor.Visible = false;
        _imageEditor.Visible = false;
        _grid.Visible = true;
        if (_tableSelector.SelectedItem is TableChoice unavailableChoice &&
            _workspace.Definitions.FirstOrDefault(item => item.Key == unavailableChoice.Key) is { Capability: TableCapability.Unavailable } unavailable)
        {
            ShowNoTable();
            _diagnosticBox.Text = $"{unavailable.DisplayName}：未提供布局证据。{Environment.NewLine}{unavailable.CapabilityEvidence}";
            SetStatus($"{unavailable.DisplayName}不可用：{unavailable.CapabilityEvidence}", true);
            return;
        }
        if (_tableSelector.SelectedItem is not TableChoice choice || !_workspace.Documents.TryGetValue(choice.Key, out var document))
        {
            ShowNoTable();
            if (_tableSelector.SelectedItem is TableChoice missing && _workspace.Definitions.FirstOrDefault(item => item.Key == missing.Key) is { } definition)
            {
                _diagnosticBox.Text = $"{definition.DisplayName}未加载。{Environment.NewLine}请检查 {definition.FileName} 是否存在，以及文件长度是否覆盖 +0x{definition.DataOffset:X} 至 +0x{definition.EndOffsetExclusive:X}。";
                SetStatus($"{definition.DisplayName}未加载；请查看文件与扫描诊断。", true);
            }
            return;
        }
        _currentDocument = document;
        _historyFilter.Visible = false;
        _suppressGridEvents = true;
        try
        {
            RememberGridWidths();
            if (!ReferenceEquals(_grid.DataSource, document.Data)) _grid.DataSource = document.Data;
            _grid.ReadOnly = false;
            ConfigureReferenceColumns(document);
            if (_grid.Columns["ID"] is DataGridViewColumn idColumn)
            {
                idColumn.Frozen = true;
                idColumn.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(242, 245, 248), ForeColor = Color.DimGray };
                idColumn.ReadOnly = true;
            }
            PopulateEntityList(document);
            PopulateQuickLookup(document);
            ApplyEmptyFilter();
            ApplyGridWidths(document.Definition.Key);
        }
        finally { _suppressGridEvents = false; }
        SetStatus($"已读取 {document.Definition.DisplayName}：{document.Data.Rows.Count} 行");
    }

    private void PopulateEntityList(TableDocument document)
    {
        _entityList.BeginUpdate();
        try
        {
            _entityList.Items.Clear();
            if (!document.Data.Columns.Contains("ID")) return;
            var nameColumn = document.Data.Columns.Contains("名称") ? "名称" : null;
            foreach (DataRow row in document.Data.Rows)
            {
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                var name = nameColumn == null ? string.Empty : Convert.ToString(row[nameColumn], CultureInfo.InvariantCulture) ?? string.Empty;
                _entityList.Items.Add(new EntityChoice(id, name));
            }
            if (_entityList.Items.Count > 0) _entityList.SelectedIndex = 0;
        }
        finally { _entityList.EndUpdate(); }
    }

    private void PopulateQuickLookup(TableDocument document)
    {
        if (!document.Data.Columns.Contains("ID"))
        {
            _quickLookup.SetOptions(Array.Empty<IdNameOption>());
            return;
        }
        var nameColumn = document.Data.Columns.Contains("名称") ? "名称" : null;
        _quickLookup.SetOptions(document.Data.Rows.Cast<DataRow>().Select(row => new IdNameOption(
            Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture),
            nameColumn == null ? string.Empty : Convert.ToString(row[nameColumn], CultureInfo.InvariantCulture) ?? string.Empty)));
    }

    private void SelectEntity()
    {
        if (_refreshingResourceCategories || _selectingBusinessEntity) return;
        if (_resourceModule && _entityList.SelectedItem is ResourceChoice category)
        {
            _lastResourceCategory = category.Key;
            _imageEditor.ShowCategory(category.Key);
            return;
        }
        if (_businessEditor.Visible && _entityList.SelectedItem is EntityChoice businessEntity)
        {
            if (!_selectingBusinessEntity) SelectBusinessEntity(businessEntity.Id);
            return;
        }
        if (_resourceModule || _entityList.SelectedItem is not EntityChoice selected || _currentDocument == null) return;
        SelectEntityById(selected.Id);
    }

    private void SelectEntityById(int id)
    {
        if (_businessEditor.Visible) { SelectBusinessEntity(id); return; }
        if (_currentDocument == null || !_currentDocument.Data.Columns.Contains("ID")) return;
        if (!PreparePendingEdits()) return;
        var targetRow = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => row.DataBoundItem is DataRowView view &&
            Convert.ToInt32(view.Row["ID"], CultureInfo.InvariantCulture) == id);
        var rowIndex = targetRow?.Index ?? -1;
        if (rowIndex < 0)
        {
            SetStatus($"未找到 ID={id}；未知数字仍可保留并由物理表编辑。", true);
            return;
        }
        _suppressGridEvents = true;
        try
        {
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[rowIndex].Cells[0];
            _grid.Rows[rowIndex].Selected = true;
            if (rowIndex < _grid.RowCount) _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, rowIndex);
        }
        finally { _suppressGridEvents = false; }
    }

    private IEnumerable<TableDocument> ResolveReferenceDocuments(string referenceTable)
    {
        if (referenceTable.Equals("items", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in new[] { "items-low", "items-high", "items-extended" })
                if (_workspace.Documents.TryGetValue(key, out var item)) yield return item;
            yield break;
        }
        if (_workspace.Documents.TryGetValue(referenceTable, out var referenced)) yield return referenced;
    }

    private void ApplyEmptyFilter()
    {
        if (_currentDocument == null) return;
        var view = _currentDocument.Data.DefaultView;
        var expression = _hideEmpty.Checked ? NonEmptyRowFilter(_currentDocument.Data) : string.Empty;
        if (view.RowFilter == expression) return;
        var previous = _suppressGridEvents;
        _suppressGridEvents = true;
        try
        {
            view.RowFilter = expression;
        }
        finally { _suppressGridEvents = previous; }
    }

    private void ShowNoTable()
    {
        _currentDocument = null;
        _entityList.Items.Clear();
        _quickLookup.SetOptions(Array.Empty<IdNameOption>());
        _historyFilter.Visible = false;
        _grid.DataSource = null;
        SetStatus("当前模块没有可读取的表。", true);
    }

    private void ShowResourceView()
    {
        _businessEditor.Visible = false;
        _grid.Visible = false;
        _resourcePreview.Visible = false;
        _imageEditor.Visible = true;
        _historyFilter.Visible = false;
        _currentDocument = null;
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        try
        {
            var category = (_entityList.SelectedItem as ResourceChoice)?.Key ?? _lastResourceCategory;
            _imageEditor.LoadProject(_workspace.RootPath, category, usedIdsProvider: GetVerifiedUsedImageIds,
                sAssignmentReferencesProvider: GetCurrentSAssignmentReferences);
            RefreshResourceCategories();
            _diagnosticBox.Text = "选择左侧资源分类。新增、替换、删除、导入导出与历史由原形象管理器控件处理。删除导致编号前移时不会自动修正其他文件引用。";
            SetStatus("形象设定已载入。");
        }
        catch (Exception ex) { _diagnosticBox.Text = ex.Message; SetStatus("形象资源加载失败：" + ex.Message, true); }
    }

    private void RefreshResourceCategories()
    {
        if (!_resourceModule) return;
        var selected = (_entityList.SelectedItem as ResourceChoice)?.Key ?? _lastResourceCategory;
        _entityList.BeginUpdate();
        _refreshingResourceCategories = true;
        try
        {
            _entityList.Items.Clear();
            foreach (var category in _imageEditor.ProjectCategories)
                _entityList.Items.Add(new ResourceChoice(category.Key, category.Name));
            var match = _entityList.Items.Cast<ResourceChoice>().Select((item, index) => (item, index))
                .FirstOrDefault(pair => pair.item.Key == selected);
            if (_entityList.Items.Count > 0) _entityList.SelectedIndex = match.item == null ? 0 : match.index;
            if (_entityList.SelectedItem is ResourceChoice current) _lastResourceCategory = current.Key;
        }
        finally { _refreshingResourceCategories = false; _entityList.EndUpdate(); }
        if (_entityList.SelectedItem is ResourceChoice active) _imageEditor.ShowCategory(active.Key);
    }

    private IReadOnlySet<int>? GetVerifiedUsedImageIds(string category)
    {
        var key = category switch { "R" => "person-r-assign", "S" => "person-s-assign", _ => null };
        if (key == null || !_workspace.Documents.TryGetValue(key, out var document) ||
            document.Definition.Capability != TableCapability.Editable ||
            document.Definition.VerificationLevel == VerificationLevel.InheritedUnverified ||
            string.IsNullOrWhiteSpace(document.Definition.CapabilityEvidence)) return null;
        var name = category == "R" ? "R形象ID" : "S形象ID";
        var field = document.Definition.Fields.FirstOrDefault(field => field.Name == name);
        if (field == null || !document.Data.Columns.Contains(name) || document.Definition.DataOffset < 0 ||
            document.Definition.EndOffsetExclusive > document.OriginalFileBytes.LongLength) return null;
        if (category == "S")
        {
            // Gallery IDs are physical Unit numbers. Project the current draft's compact
            // assignments here, before crossing into the resource editor.
            var assignments = new List<ImageManager.Core.CharacterAssignment>();
            Dictionary<int, int>? jobs = null;
            foreach (DataRow row in document.Data.Rows)
            {
                if (!int.TryParse(Convert.ToString(row[name], CultureInfo.InvariantCulture), out var sId) ||
                    sId < 0 || sId >= ushort.MaxValue) return null;
                var personId = row.Table.Columns.Contains("ID") ? Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture)
                    : document.Definition.BeginId + document.Data.Rows.IndexOf(row);
                var job = 0;
                if (sId == 0)
                {
                    if (jobs == null)
                    {
                        if (!_workspace.Documents.TryGetValue("persons", out var persons) ||
                            persons.Definition.Capability != TableCapability.Editable ||
                            persons.Definition.VerificationLevel == VerificationLevel.InheritedUnverified ||
                            (persons.Definition.VerificationLevel is not (VerificationLevel.BaselineVerified or VerificationLevel.VariantVerified) &&
                                string.IsNullOrWhiteSpace(persons.Definition.CapabilityEvidence)) ||
                            !persons.Data.Columns.Contains("兵种")) return null;
                        jobs = new Dictionary<int, int>();
                        for (var index = 0; index < persons.Data.Rows.Count; index++)
                        {
                            var person = persons.Data.Rows[index];
                            var id = persons.Data.Columns.Contains("ID") ? Convert.ToInt32(person["ID"], CultureInfo.InvariantCulture)
                                : persons.Definition.BeginId + index;
                            if (int.TryParse(Convert.ToString(person["兵种"], CultureInfo.InvariantCulture), out var value)) jobs[id] = value;
                        }
                    }
                    if (!jobs.TryGetValue(personId, out job) || job is < 0 or > 79) return null;
                }
                assignments.Add(new ImageManager.Core.CharacterAssignment(personId, string.Empty, job, 0, sId));
            }
            return new ImageManager.Core.SPhysicalMappingService().ResolveUsedPhysicalIds(assignments);
        }
        var result = new HashSet<int>();
        foreach (DataRow row in document.Data.Rows)
        {
            if (int.TryParse(Convert.ToString(row[name], CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id) && id >= Math.Max(0, field.Minimum) && id <= field.Maximum && id < ushort.MaxValue)
                result.Add(id);
        }
        return result;
    }

    private UnifiedHistoryService CreateUnifiedHistory()
        => new(_workspace, _resourceAdapter ??= new ResourceCatalogAdapter());

    private void ShowHistoryView()
    {
        _historyFilter.Visible = true;
        var table = new DataTable("历史记录");
        foreach (var name in new[] { "时间", "来源", "状态", "操作", "摘要", "文件", "操作ID", "详情", "修改前指纹", "修改后指纹" })
            table.Columns.Add(name);
        _historyEntries.Clear();
        try
        {
            foreach (var entry in CreateUnifiedHistory().List())
            {
                _historyEntries[entry.Source + ":" + entry.OperationId] = entry;
                table.Rows.Add(entry.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), entry.Source,
                    entry.Status, entry.Operation, entry.Summary, string.Join("、", entry.Targets), entry.OperationId,
                    entry.Details, entry.BeforeSha, entry.AfterSha);
            }
        }
        catch (Exception ex) { SetStatus("历史读取失败：" + ex.Message, true); }
        _suppressGridEvents = true;
        try
        {
            _grid.DataSource = table;
            _grid.ReadOnly = true;
            ApplyGridWidths("__history");
            foreach (var hidden in new[] { "操作ID", "详情", "修改前指纹", "修改后指纹" }) _grid.Columns[hidden].Visible = false;
            _grid.Columns["摘要"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _diagnosticBox.Text = "选择记录后可撤回或重做。数据与形象记录保留各自的文件指纹检查。";
        }
        finally { _suppressGridEvents = false; }
        ApplyHistoryFilter();
        SetStatus($"数据与形象历史：{table.Rows.Count} 条。选择记录可查看详情或恢复。 ");
    }

    private void ApplyHistoryFilter()
    {
        if (_grid.DataSource is not DataTable table || table.TableName != "历史记录") return;
        var value = _historyFilter.Text.Trim();
        if (value.Length == 0)
        {
            table.DefaultView.RowFilter = string.Empty;
            return;
        }
        var escaped = value.Replace("'", "''", StringComparison.Ordinal)
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("*", "[*]", StringComparison.Ordinal);
        table.DefaultView.RowFilter = string.Join(" OR ", table.Columns.Cast<DataColumn>().Select(column => $"[{column.ColumnName}] LIKE '%{escaped}%'"));
    }

    private void ShowScanView()
    {
        _historyFilter.Visible = false;
        var table = new DataTable("扫描诊断");
        table.Columns.Add("文件");
        table.Columns.Add("存在");
        table.Columns.Add("文件长度");
        table.Columns.Add("SHA-256");
        table.Columns.Add("说明");
        if (_workspace.LastScan != null)
        {
            foreach (var file in _workspace.LastScan.Files)
            {
                table.Rows.Add(file.RelativePath, file.Exists ? "是" : "否", file.ActualLength.ToString("N0", CultureInfo.InvariantCulture), file.Sha256, file.Message);
            }
            foreach (var definition in _workspace.Definitions)
            {
                table.Rows.Add(
                    $"表：{definition.DisplayName}",
                    _workspace.Documents.ContainsKey(definition.Key) ? "是" : "否",
                    $"{definition.RowCount} x {definition.RowSize}",
                    string.Empty,
                    $"codec={definition.RecordCodecKey}；验证={definition.VerificationLevel}；来源={definition.LayoutSource}");
            }
            _diagnosticBox.Text = FormatScanDiagnostics(_workspace.LastScan);
        }
        _suppressGridEvents = true;
        try
        {
            _grid.DataSource = table;
            _grid.ReadOnly = true;
            ApplyGridWidths("__scan");
        }
        finally { _suppressGridEvents = false; }
        SetStatus("扫描诊断：扫描状态不会改变读写权限。");
    }

    private void GridCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_suppressGridEvents || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Rows[e.RowIndex].DataBoundItem is not DataRowView view) return;
        _editingValues.Clear();
        var column = _grid.Columns[e.ColumnIndex].DataPropertyName;
        _editingValues[(view.Row, column)] = view.Row[column];
    }

    private int PhysicalRowIndex(int displayIndex)
    {
        if (_currentDocument == null || displayIndex < 0 || displayIndex >= _grid.Rows.Count) return -1;
        return _grid.Rows[displayIndex].DataBoundItem is DataRowView view
            ? _currentDocument.Data.Rows.IndexOf(view.Row) : -1;
    }

    private bool PreparePendingEdits()
    {
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return true;
        try
        {
        if (_grid.IsCurrentCellInEditMode && _gridTextGuard is { HasPendingChanges: true } && !_gridTextGuard.Prepare())
        { _grid.EditingControl?.Focus(); return false; }
        if (!_grid.EndEdit()) { SetStatus("当前表格编辑无效，请先修正标出的单元格。", true); return false; }
        if (_grid.DataSource != null && BindingContext?[_grid.DataSource] is CurrencyManager manager) manager.EndCurrentEdit();
            return _businessEditor.PreparePendingEdits();
        }
        catch (Exception ex) { SetStatus($"当前输入无法提交：{ex.Message}", true); return false; }
    }

    private SaveResult CommitPendingEdits()
    {
        if (!PreparePendingEdits()) return new SaveResult(false, 0, 0, 0, null, ["当前输入尚未通过验证，草稿已保留。"]);
        var previous = _suppressGridEvents;
        _suppressGridEvents = true;
        try
        {
            var result = _workspace.Save();
            if (result.Saved)
            {
                try
                {
                    foreach (var editor in _businessEditors.Values) editor.AcceptSavedChanges();
                    RefreshSavedNames();
                }
                catch (Exception ex)
                {
                    return result with { Messages = [result.Messages.FirstOrDefault() + $" 界面刷新未完成：{ex.Message}；可重新读取项目刷新显示。"] };
                }
            }
            return result;
        }
        finally { _suppressGridEvents = previous; }
    }

    private void GridEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (e.Control is not TextBoxBase box || _currentDocument == null || _grid.CurrentCell == null) return;
        var property = _grid.Columns[_grid.CurrentCell.ColumnIndex].DataPropertyName;
        var field = _currentDocument.Definition.Fields.FirstOrDefault(field => field.Name == property);
        if (field?.Kind != FieldKind.FixedString)
        {
            _gridTextGuard?.Dispose(); _gridTextGuard = null; _guardedGridEditor = null; _gridBudget.Visible = false;
            return;
        }
        if (_guardedGridEditor != box || _gridTextGuard == null)
        {
            _gridTextGuard?.Dispose(); _guardedGridEditor = box; _gridTextGuard = new ByteTextInputGuard(box);
            _gridTextGuard.StateChanged += (_, _) => { RefreshGridBudget(); RefreshDirtyState(); };
            _gridTextGuard.InputRejected += (_, _) => SetStatus(_gridTextGuard?.RejectionMessage ?? "输入被拒绝", true);
        }
        box.MaxLength = 0;
        box.AllowDrop = false;
        var constraint = TextFieldConstraints.Get(_currentDocument.Definition, field);
        var rowIndex = PhysicalRowIndex(_grid.CurrentCell.RowIndex);
        if (rowIndex >= 0)
        {
            var offset = checked((int)(_currentDocument.Definition.DataOffset + (long)rowIndex * _currentDocument.Definition.RowSize + field.Offset));
            constraint = constraint.ForOriginalSlot(_currentDocument.OriginalFileBytes.AsSpan(offset, field.Size));
        }
        _gridTextGuard.Bind(constraint, box.Text);
        _shellTips.SetToolTip(_gridBudget, $"{ByteTextEditor.EncodingHint}\r\n物理槽 {constraint.StorageBytes} 字节；{constraint.Evidence}");
        RefreshGridBudget();
    }

    private void RefreshGridBudget()
    {
        if (_gridTextGuard?.Validation is not { } result) return;
        _gridBudget.Visible = _grid.Visible && _grid.IsCurrentCellInEditMode;
        _gridBudget.Text = (result.CanEncode
            ? $"已用 {result.ByteCount} / {result.Capacity} 字节 · 剩余 {result.RemainingBytes} 字节"
            : result.Error) + $" · {ByteTextEditor.EncodingHint}" + (_gridTextGuard.RejectionMessage != null ? " · 本次输入已整次拒绝" : string.Empty);
        _gridBudget.Height = TextRenderer.MeasureText(_gridBudget.Text, _gridBudget.Font,
            new Size(Math.Max(1, _gridBudget.ClientSize.Width), int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
        _gridBudget.ForeColor = !result.CanEncode || _gridTextGuard.RejectionMessage != null ? Color.Firebrick
            : result.RemainingBytes <= result.Capacity / 10 ? Color.DarkOrange : Color.DimGray;
    }

    private void ShowCurrentFieldDetails()
    {
        if (!_suppressGridEvents && _grid.DataSource is DataTable { TableName: "历史记录" } && _grid.CurrentRow != null)
        {
            _diagnosticBox.Text = Convert.ToString(_grid.CurrentRow.Cells["详情"].Value, CultureInfo.InvariantCulture) +
                Environment.NewLine + "修改前指纹：" + Convert.ToString(_grid.CurrentRow.Cells["修改前指纹"].Value, CultureInfo.InvariantCulture) +
                Environment.NewLine + "修改后指纹：" + Convert.ToString(_grid.CurrentRow.Cells["修改后指纹"].Value, CultureInfo.InvariantCulture);
            return;
        }
        if (_suppressGridEvents || _currentDocument == null || _grid.CurrentCell == null) return;
        var propertyName = _grid.Columns[_grid.CurrentCell.ColumnIndex].DataPropertyName;
        var field = _currentDocument.Definition.Fields.FirstOrDefault(candidate => candidate.Name == propertyName);
        if (field == null) return;
        var reference = string.IsNullOrWhiteSpace(field.ReferenceTable) ? "无" : field.ReferenceTable;
        var physical = _currentDocument.Definition.RecordCodecKey == "item-66-15-v1" && field.Name is "宝物类型" or "装备特效号"
            ? "由行类别动态映射到 +0x11/+0x12"
            : $"行内 +0x{field.Offset:X}，{field.Size} 字节";
        _diagnosticBox.Text = $"{_workspace.ActiveProfile.Profile.DisplayName} / {_workspace.ActiveProfile.Variant.DisplayName}\r\n" +
                              $"表：{_currentDocument.Definition.DisplayName}；codec={_currentDocument.Definition.RecordCodecKey}；验证={_currentDocument.Definition.VerificationLevel}\r\n" +
                              $"字段：{field.Name}；{physical}；范围={field.Minimum}..{field.Maximum}；引用={reference}";
    }

    private void GridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressGridEvents || _currentDocument == null || e.RowIndex < 0 || e.ColumnIndex <= 0) return;
        var column = _grid.Columns[e.ColumnIndex].DataPropertyName;
        var editing = _editingValues.Keys.FirstOrDefault(key => key.Column == column);
        if (editing.Row == null) return;
        var physical = _currentDocument.Data.Rows.IndexOf(editing.Row);
        if (physical < 0) return;
        try { _workspace.RecordCellEdit(_currentDocument, physical, column, _editingValues[editing], editing.Row[column]); }
        catch (Exception ex) { SetStatus($"单元格修改未提交：{ex.Message}", true); }
        finally { _editingValues.Remove(editing); }
        RefreshDirtyState();
    }

    private void GridCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_suppressGridEvents || _currentDocument == null || e.RowIndex < 0 || e.ColumnIndex <= 0) return;
        var column = _grid.Columns[e.ColumnIndex];
        var field = _currentDocument.Data.Columns[column.DataPropertyName]?.ExtendedProperties["Field"] as TableFieldDefinition;
        if (field == null) return;
        try
        {
            var text = Convert.ToString(e.FormattedValue, CultureInfo.InvariantCulture) ?? string.Empty;
            if (field.Kind is FieldKind.UInt8 or FieldKind.UInt16) BinaryValueCodec.ParseInteger(text, field.Minimum, field.Maximum, field.Name);
            _workspace.ValidateCellValue(_currentDocument, PhysicalRowIndex(e.RowIndex), column.DataPropertyName, text);
            _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ErrorText = ex.Message;
            SetStatus(ex.Message, true);
        }
    }

    private void GridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Delete || _currentDocument == null || _grid.ReadOnly) return;
        if (_grid.CurrentCell is not { RowIndex: >= 0, ColumnIndex: > 0 } current) return;
        _suppressGridEvents = true;
        try
        {
            var column = _grid.Columns[current.ColumnIndex];
            if (column.ReadOnly) return;
            var field = _currentDocument.Data.Columns[column.DataPropertyName]?.ExtendedProperties["Field"] as TableFieldDefinition;
            var oldValue = current.Value;
            object newValue = field?.Kind == FieldKind.FixedString ? string.Empty : 0;
            var physical = PhysicalRowIndex(current.RowIndex);
            _workspace.ValidateCellValue(_currentDocument, physical, column.DataPropertyName, newValue);
            current.Value = newValue;
            _workspace.RecordCellEdit(_currentDocument, physical, column.DataPropertyName, oldValue, newValue);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { _suppressGridEvents = false; }
        e.Handled = true;
        e.SuppressKeyPress = true;
        RefreshDirtyState();
    }

    private void DraftUndo()
    {
        if (!PreparePendingEdits()) return;
        string? message;
        try { message = _workspace.Commands.Undo(); }
        catch (Exception ex) { SetStatus($"撤回未完成，命令仍保留：{ex.Message}", true); return; }
        if (message == null) { SetStatus("没有可撤回的未保存编辑。", true); return; }
        _grid.Refresh();
        RefreshDirtyState();
        RefreshBusinessSelection();
        SetStatus($"已撤回：{message}");
    }

    private void DraftRedo()
    {
        if (!PreparePendingEdits()) return;
        string? message;
        try { message = _workspace.Commands.Redo(); }
        catch (Exception ex) { SetStatus($"前进未完成，命令仍保留：{ex.Message}", true); return; }
        if (message == null) { SetStatus("没有可前进的未保存编辑。", true); return; }
        _grid.Refresh();
        RefreshDirtyState();
        RefreshBusinessSelection();
        SetStatus($"已前进：{message}");
    }

    private void SaveProject()
    {
        if (!PreparePendingEdits()) return;
        SaveResult result;
        try { result = CommitPendingEdits(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus(ex.Message, true);
            return;
        }
        SetStatus(result.Messages.FirstOrDefault() ?? "保存完成", !result.Saved);
        RefreshDirtyState();
        if (!result.Saved) MessageBox.Show(this, string.Join(Environment.NewLine, result.Messages), "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else if (result.History != null) _diagnosticBox.Text = $"历史记录：{result.History.Summary}{Environment.NewLine}" + string.Join(Environment.NewLine, result.History.Tables.Take(12).Select(change => $"{change.TableName} ID={change.Id} {change.FieldName}: {change.OldValue} -> {change.NewValue}"));
    }

    private void HistoryUndo() => RestoreHistory(redo: false);

    private void HistoryRedo() => RestoreHistory(redo: true);

    private void RestoreHistory(bool redo)
    {
        if (!PreparePendingEdits()) return;
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        if (HasPendingChanges && !ConfirmDiscardOrSave(redo ? "重做已保存历史" : "撤回已保存历史")) return;
        try
        {
            UnifiedHistoryEntry? entry = null;
            if (_grid.DataSource is DataTable { TableName: "历史记录" } && _grid.CurrentRow != null)
            {
                var source = Convert.ToString(_grid.CurrentRow.Cells["来源"].Value, CultureInfo.InvariantCulture);
                var id = Convert.ToString(_grid.CurrentRow.Cells["操作ID"].Value, CultureInfo.InvariantCulture);
                _historyEntries.TryGetValue(source + ":" + id, out entry);
            }
            if (entry != null && !(redo ? entry.CanRedo : entry.CanUndo)) entry = null;
            entry ??= CreateUnifiedHistory().FindDefault(redo);
            if (entry == null) { SetStatus(redo ? "没有可重做的历史记录。" : "没有可撤回的历史记录。"); return; }
            var result = CreateUnifiedHistory().Restore(entry, redo);
            if (result.Success)
            {
                _resourceAdapter?.Invalidate(_workspace.RootPath);
                if (entry.Source == "形象") _workspace.ReloadLoadedDocuments();
                RefreshCurrentView();
            }
            SetStatus(result.Message, !result.Success);
            if (!result.Success) MessageBox.Show(this, result.Message, "历史操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { SetStatus("历史操作失败：" + ex.Message, true); }
    }

    private void OpenBatchDialog()
    {
        if (_currentDocument == null) return;
        if (!PreparePendingEdits()) return;
        var selected = _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index).Select(row => PhysicalRowIndex(row.Index)).Where(index => index >= 0).Distinct().ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "请先选择至少一行。", "批量修改", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dialog = new BatchDialog(_currentDocument, selected, _workspace.ActiveProfile, _workspace);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _suppressGridEvents = true;
            try { _workspace.ApplyBatchMultiple(_currentDocument, selected, dialog.ColumnNames, dialog.Operation, dialog.InputValue); }
            finally { _suppressGridEvents = false; }
            _grid.Refresh();
            RefreshDirtyState();
            SetStatus($"已批量修改 {selected.Count} 行。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "批量修改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportLegacyCsv()
    {
        if (!PreparePendingEdits()) return;
        if (_businessEditor.Visible && _businessKind is { } kind)
        {
            if (!_businessEditor.Flush()) return;
            using var folder = new FolderBrowserDialog { Description = "选择当前业务页的物理表 CSV 目录" };
            if (folder.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var business = new BusinessDataService(_workspace);
                var allowed = business.GetAvailableTableKeys(kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var definitions = _workspace.Definitions.Where(definition => allowed.Contains(definition.Key)).ToArray();
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(folder.SelectedPath, "*.csv"))
                {
                    var context = CsvService.ReadContext(path);
                    string key;
                    if (context != null)
                    {
                        key = context.TableKey;
                        if (!allowed.Contains(key)) continue;
                    }
                    else
                    {
                        try { key = CsvDisplayNameRegistry.ResolveTableKey(Path.GetFileName(path), definitions); }
                        catch (InvalidDataException) { continue; }
                    }
                    if (!files.TryAdd(key, path)) throw new InvalidDataException($"目录包含重复的物理表 CSV：{key}。");
                }
                if (files.Count == 0) throw new InvalidDataException("所选目录没有当前页对应的 CSV 文件。");
                var result = business.ImportPageCsvAtomic(kind, files);
                RefreshBusinessSelection(); RefreshDirtyState();
                SetStatus($"已原子导入 {result.Tables.Count} 张物理表，{result.ChangedCells} 个字段进入草稿。");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "业务页 CSV 导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return;
        }
        if (_currentDocument == null) return;
        using var dialog = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _suppressGridEvents = true;
            CsvImportResult result;
            try { result = _workspace.ImportCsv(_currentDocument, dialog.FileName); }
            finally { _suppressGridEvents = false; }
            _grid.Refresh();
            RefreshDirtyState();
            SetStatus($"CSV 导入完成：{result.ImportedRows} 行，{result.ChangedCells} 个单元格已修改。");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ExportLegacyCsv()
    {
        if (!PreparePendingEdits()) return;
        if (_businessEditor.Visible && _businessKind is { } kind)
        {
            if (!_businessEditor.Flush()) return;
            using var folder = new FolderBrowserDialog { Description = "选择导出目录，每张物理表生成独立 CSV" };
            if (folder.ShowDialog(this) != DialogResult.OK) return;
            try { var result = new BusinessDataService(_workspace).ExportPageCsv(kind, folder.SelectedPath); SetStatus($"已导出 {result.Count} 张物理表 CSV。"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "业务页 CSV 导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return;
        }
        if (_currentDocument == null) return;
        using var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = CsvDisplayNameRegistry.GetFileName(_currentDocument.Definition) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _workspace.ExportCsv(_currentDocument, dialog.FileName); SetStatus($"CSV 已导出：{dialog.FileName}"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void FindInCurrentTable()
    {
        if (_currentDocument == null) return;
        if (!PreparePendingEdits()) return;
        using var dialog = new FindDialog(_currentDocument, replaceMode: false);
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dialog.FindText)) return;

        var columnNames = dialog.ColumnName == "全部字段"
            ? _currentDocument.Data.Columns.Cast<DataColumn>().Where(column => column.ColumnName != "ID").Select(column => column.ColumnName).ToArray()
            : [dialog.ColumnName];
        var count = _grid.Rows.Count;
        var start = _grid.CurrentCell?.RowIndex ?? -1;
        var first = count == 0 ? 0 : (start + 1 + count) % count;
        var rowIndexes = Enumerable.Range(0, count).Select(offset => (first + offset) % count).ToArray();
        foreach (var rowIndex in rowIndexes)
        {
            if (_hideEmpty.Checked && !_grid.Rows[rowIndex].Visible) continue;
            if (_grid.Rows[rowIndex].DataBoundItem is not DataRowView view) continue;
            var match = columnNames.FirstOrDefault(column =>
                Convert.ToString(view.Row[column], CultureInfo.InvariantCulture)
                    ?.Contains(dialog.FindText, StringComparison.OrdinalIgnoreCase) == true);
            if (match == null) continue;
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[rowIndex].Cells[match];
            _grid.Rows[rowIndex].Selected = true;
            _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, rowIndex);
            SetStatus($"已找到：ID={view.Row["ID"]} / {match}");
            return;
        }
        SetStatus("未找到匹配内容。", true);
    }

    private void FindReplaceInCurrentTable()
    {
        if (_currentDocument == null) return;
        if (!PreparePendingEdits()) return;
        using var dialog = new FindDialog(_currentDocument, replaceMode: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var rows = _grid.Rows.Cast<DataGridViewRow>()
                .Where(row => row.Visible).Select(row => PhysicalRowIndex(row.Index)).Where(index => index >= 0)
                .ToArray();
            _suppressGridEvents = true;
            int changed;
            try { changed = _workspace.ApplyFindReplace(_currentDocument, rows, dialog.ColumnName, dialog.FindText, dialog.ReplaceText); }
            finally { _suppressGridEvents = false; }
            _grid.Refresh();
            RefreshDirtyState();
            SetStatus(changed == 0 ? "没有匹配内容。" : $"查找替换完成：{changed} 行。", changed == 0);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "查找替换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Rescan()
    {
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        if (!PreparePendingEdits()) return;
        if (HasPendingChanges && !ConfirmDiscardOrSave("重新扫描")) return;
        var report = _workspace.Rescan();
        UpdateScanSummary(report);
        _diagnosticBox.Text = FormatScanDiagnostics(report, 20);
        SyncProfileSelectors();
        SetStatus("扫描完成，扫描结果不会改变读写权限。 ");
    }

    private void RefreshCurrentView()
    {
        if (_moduleTabs.SelectedTab?.Tag as string == "__history") { ShowHistoryView(); RefreshDirtyState(); return; }
        if (_resourceModule) { ShowResourceView(); RefreshDirtyState(); return; }
        if (_businessEditor.Visible) { RefreshBusinessSelection(); RefreshDirtyState(); return; }
        var key = (_tableSelector.SelectedItem as TableChoice)?.Key;
        if (!string.IsNullOrWhiteSpace(key) && _workspace.Documents.TryGetValue(key, out var document))
        {
            RememberGridWidths();
            var previous = _suppressGridEvents;
            _suppressGridEvents = true;
            try
            {
                // Reload callers have already validated the old editor. Do
                // not validate an old row against the replacement document.
                _grid.CancelEdit();
                _editingValues.Clear();
                _currentDocument = document;
                _grid.DataSource = document.Data;
                _grid.ReadOnly = false;
                ConfigureReferenceColumns(document);
                ApplyEmptyFilter();
                ApplyGridWidths(key);
            }
            finally { _suppressGridEvents = previous; }
        }
        RefreshDirtyState();
    }

    private void RefreshDirtyState()
    {
        var dirty = HasPendingChanges;
        _dirtyLabel.Text = dirty ? "● 有未保存修改" : "无未保存修改";
        _dirtyLabel.ForeColor = dirty ? Color.DarkOrange : Color.SeaGreen;
        // Text editors flush on validation/save. Keep Save available while a
        // project is open so a focused text edit can be committed directly.
        _saveButton.Enabled = !string.IsNullOrWhiteSpace(_workspace.RootPath);
        _undoButton.Enabled = _workspace.Commands.CanUndo;
        _redoButton.Enabled = _workspace.Commands.CanRedo;
    }

    private void SetStatus(string text, bool error = false)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    private bool ConfirmDiscardOrSave(string operation)
    {
        var result = MessageBox.Show(this, $"当前存在未保存修改。是否先保存后再{operation}？", operation, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Yes)
        {
            try
            {
                var save = CommitPendingEdits();
                RefreshDirtyState();
                if (!save.Saved) MessageBox.Show(this, string.Join(Environment.NewLine, save.Messages), "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return save.Saved;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
        if (result != DialogResult.No) return false;
        DiscardAllEditorInputs();
        _workspace.ReloadLoadedDocuments();
        RefreshCurrentView();
        return true;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!PreparePendingEdits()) { e.Cancel = true; return; }
        if (!HasPendingChanges) return;
        var result = MessageBox.Show(this, "当前存在未保存修改。请选择保存并退出、不保存退出或取消。", "退出 data 管理器", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) { e.Cancel = true; return; }
        if (result == DialogResult.Yes)
        {
            try
            {
                var save = CommitPendingEdits();
                if (!save.Saved)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, string.Join(Environment.NewLine, save.Messages), "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var panel in _businessEditors.Values.Distinct()) panel.Dispose();
            _businessEditors.Clear();
            _imageEditor.Dispose();
            _resourceAdapter?.Dispose();
            _gridTextGuard?.Dispose();
            _shellTips.Dispose();
            ResetReferenceChoices();
            _referenceLookup.Dispose();
            _resourcePreview.Image?.Dispose();
            _resourcePreview.Image = null;
            _applicationIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_imageEditor.Visible && _imageEditor.ExecuteShortcut(keyData)) return true;
        var ctrl = keyData.HasFlag(Keys.Control);
        var alt = keyData.HasFlag(Keys.Alt);
        var shift = keyData.HasFlag(Keys.Shift);
        var key = keyData & Keys.KeyCode;
        if (ctrl && alt && key == Keys.Z) { HistoryUndo(); return true; }
        if (ctrl && alt && key == Keys.Y) { HistoryRedo(); return true; }
        if (ctrl && key == Keys.S) { SaveProject(); return true; }
        if (ctrl && !alt && key is Keys.Z or Keys.Y)
        {
            var redoText = key == Keys.Y || shift;
            if (_businessEditor.ContainsFocus && _businessEditor.TryUndoFocusedText(redoText)) return true;
            if (_grid.IsCurrentCellInEditMode && _guardedGridEditor?.ContainsFocus == true && _gridTextGuard?.Undo(redoText) == true) return true;
        }
        if (ctrl && shift && key == Keys.Z) { DraftRedo(); return true; }
        if (ctrl && key == Keys.Z) { DraftUndo(); return true; }
        if (ctrl && key == Keys.Y) { DraftRedo(); return true; }
        if (ctrl && key == Keys.A && _grid.Focused)
        {
            _grid.SelectAll();
            return true;
        }
        if (ctrl && key == Keys.B) { OpenBatchDialog(); return true; }
        if (ctrl && key == Keys.F) { FindInCurrentTable(); return true; }
        if (ctrl && key == Keys.H) { FindReplaceInCurrentTable(); return true; }
        if (ctrl && key == Keys.E) { ExportCsv(); return true; }
        if (ctrl && key == Keys.I) { ImportCsv(); return true; }
        if (ctrl && key == Keys.R)
        {
            if (!PreparePendingEdits()) return true;
            if (!HasPendingChanges || ConfirmDiscardOrSave("重新读取"))
            {
                _resourceAdapter?.Invalidate(_workspace.RootPath);
                DiscardAllEditorInputs(); _workspace.ReloadLoadedDocuments(); RefreshCurrentView();
            }
            return true;
        }
        if (key == Keys.F5) { Rescan(); return true; }
        if (key == Keys.Escape && _grid.ContainsFocus)
        {
            if (_grid.IsCurrentCellInEditMode) _grid.CancelEdit();
            else _grid.ClearSelection();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private sealed record TableChoice(string Key, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record ReferenceChoice(int Value, string Display);
    private sealed record EntityChoice(int Id, string Name)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Name)
            ? $"{Id.ToString(CultureInfo.InvariantCulture)}：未命名"
            : $"{Id.ToString(CultureInfo.InvariantCulture)}：{Name}";
    }

    private sealed record ResourceChoice(string Key, string Name)
    {
        public override string ToString() => Name;
    }
}
