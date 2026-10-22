using ImageManager.Core;

namespace ImageManager.App;

public sealed class MainForm : Form
{
    private readonly ProjectProfileService _profiles = new();
    private readonly ImageCatalogService _catalog = new();
    private readonly HistoryService _history;
    private readonly ImageMutationService _mutations;
    private readonly ReferenceAtlasService _atlases = new();
    private readonly ImagePreviewService _previews = new();
    private readonly RsImportService _rsImports = new();
    private readonly CharacterAssignmentService _assignments = new();
    private readonly BatchRenameService _renames;
    private readonly ToolStripTextBox _pathBox = new() { AutoSize = false, Width = 520, ReadOnly = true };
    private readonly ToolStripLabel _versionLabel = new("未打开项目");
    private readonly ToolStripTextBox _searchBox = new() { AutoSize = false, Width = 180 };
    private readonly TabControl _mainTabs = new() { Dock = DockStyle.Fill };
    private readonly TabControl _characterTabs = new() { Dock = DockStyle.Fill };
    private readonly DataAssignmentPage _dataPage;
    private readonly ImageGridPage _rPage;
    private readonly ImageGridPage _sPage;
    private readonly ImageGridPage _portraitPage;
    private readonly ImageGridPage _itemPage;
    private readonly StrategyPage _strategyPage;
    private readonly ImageGridPage _scenePage;
    private readonly ImageGridPage _otherPage;
    private readonly HistoryPage _historyPage;
    private ProjectProfile? _project;

    public MainForm()
    {
        Text = "形象管理器";
        Width = 1460;
        Height = 920;
        MinimumSize = new Size(1080, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var appRoot = FindApplicationRoot();
        _history = new HistoryService(Path.Combine(appRoot, "形象管理器_备份"));
        _mutations = new ImageMutationService(_history);
        _renames = new BatchRenameService(_history);

        _dataPage = new DataAssignmentPage(_assignments, _mutations, _catalog);
        _rPage = NewGridPage("R 形象", ImagePageCapabilities.CharacterRs);
        _sPage = NewGridPage("S 形象", ImagePageCapabilities.CharacterS);
        _portraitPage = NewGridPage("人物头像", ImagePageCapabilities.Standard);
        _itemPage = NewGridPage("宝物图标", ImagePageCapabilities.ItemIcon);
        _strategyPage = new StrategyPage(NewGridPage("策略图标", ImagePageCapabilities.Standard), _catalog);
        _scenePage = NewGridPage("场景图片", ImagePageCapabilities.Standard);
        _otherPage = NewGridPage("其他图片", ImagePageCapabilities.Standard);
        _historyPage = new HistoryPage(_history);

        Controls.Add(_mainTabs);
        Controls.Add(BuildTopBar());
        BuildTabs();
        Shown += (_, _) => TryOpenInitialProject();
    }

    private ImageGridPage NewGridPage(string title, ImagePageCapabilities capabilities)
    {
        var page = new ImageGridPage(title, _catalog, _mutations, _atlases, _rsImports, _renames, _previews, capabilities);
        page.ProjectStructureChanged += ReloadProject;
        return page;
    }

    private ToolStrip BuildTopBar()
    {
        var bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 5, 6, 5) };
        var open = new ToolStripButton("打开项目");
        open.Click += (_, _) => OpenProjectDialog();
        var refresh = new ToolStripButton("刷新");
        refresh.Click += (_, _) => ReloadProject();
        var history = new ToolStripButton("历史记录");
        history.Click += (_, _) => _mainTabs.SelectedIndex = _mainTabs.TabPages.Count - 1;
        _searchBox.ToolTipText = "搜索当前页面的名称或编号";
        _searchBox.TextChanged += (_, _) => CurrentGridPage()?.SetSearch(_searchBox.Text);
        _versionLabel.IsLink = true;
        _versionLabel.ToolTipText = "点击查看项目识别与格式诊断";
        _versionLabel.Click += (_, _) => ShowProjectDiagnostics();
        bar.Items.AddRange([open, new ToolStripSeparator(), new ToolStripLabel("路径"), _pathBox,
            new ToolStripSeparator(), _versionLabel, refresh, new ToolStripSeparator(), new ToolStripLabel("搜索"),
            _searchBox, history]);
        return bar;
    }

    private void BuildTabs()
    {
        _characterTabs.TabPages.Add(new TabPage("Data") { Controls = { _dataPage } });
        _characterTabs.TabPages.Add(new TabPage("R 形象") { Controls = { _rPage } });
        _characterTabs.TabPages.Add(new TabPage("S 形象") { Controls = { _sPage } });
        _mainTabs.TabPages.Add(new TabPage("人物形象") { Controls = { _characterTabs } });
        _mainTabs.TabPages.Add(new TabPage("人物头像") { Controls = { _portraitPage } });
        _mainTabs.TabPages.Add(new TabPage("宝物图标") { Controls = { _itemPage } });
        _mainTabs.TabPages.Add(new TabPage("策略图标") { Controls = { _strategyPage } });
        _mainTabs.TabPages.Add(new TabPage("场景图片") { Controls = { _scenePage } });
        _mainTabs.TabPages.Add(new TabPage("其他图片") { Controls = { _otherPage } });
        _mainTabs.TabPages.Add(new TabPage("历史记录") { Controls = { _historyPage } });
        _mainTabs.SelectedIndexChanged += (_, _) => ActivateCurrentGridPage();
        _characterTabs.SelectedIndexChanged += (_, _) => ActivateCurrentGridPage();
    }

    private void ActivateCurrentGridPage()
    {
        _searchBox.Text = string.Empty;
        if (CurrentGridPage() is not { } page) return;
        page.SetSearch(string.Empty);
        page.ActivateGallery();
    }

    private void OpenProjectDialog()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 Ekd5.exe 和图片资源的 MOD 项目目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenProject(dialog.SelectedPath);
    }

    private void TryOpenInitialProject()
    {
        var candidate = Directory.GetParent(FindApplicationRoot())?.FullName;
        if (candidate != null && File.Exists(Path.Combine(candidate, "Ekd5.exe"))) OpenProject(candidate);
    }

    private void OpenProject(string path)
    {
        try
        {
            UseWaitCursor = true;
            _project = _profiles.Detect(path);
            _pathBox.Text = _project.RootPath;
            _versionLabel.Text = _project.VersionText + (_project.IsReadOnly ? "（只读）" : string.Empty) +
                                 $" · 诊断 {_project.Diagnostics.Count}";
            _versionLabel.ForeColor = _project.IsReadOnly ? Color.Firebrick : Color.DarkGreen;
            var resources = _catalog.BuildCatalog(_project);
            _dataPage.LoadProject(_project, resources);
            var usedR = _dataPage.UsedRIds;
            var usedS = _dataPage.UsedSPhysicalIds;
            _rPage.LoadProject(_project, resources.Where(item => item.Category == ImageCategory.CharacterR), usedR);
            _sPage.LoadProject(_project, resources.Where(item => item.Category == ImageCategory.CharacterS), usedS);
            _portraitPage.LoadProject(_project, resources.Where(item => item.Category == ImageCategory.Portrait));
            _itemPage.LoadProject(_project, resources.Where(item => item.Category == ImageCategory.ItemIcon));
            _strategyPage.LoadProject(_project, resources.Where(item => item.Category == ImageCategory.StrategyIcon), resources);
            _scenePage.LoadProject(_project, resources.Where(item => item.Category == ImageCategory.Scene));
            _otherPage.LoadProject(_project, resources.Where(item => item.Category is ImageCategory.Range or ImageCategory.Interface
                or ImageCategory.Dynamic or ImageCategory.HalfBody or ImageCategory.StrategyAnimation or ImageCategory.Discovered or ImageCategory.Experimental));
            _historyPage.LoadProject(_project);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开项目失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    public void LoadProjectForSmokeTest(string path) => OpenProject(path);

    private void ReloadProject()
    {
        if (_project != null) OpenProject(_project.RootPath);
    }

    private void ShowProjectDiagnostics()
    {
        if (_project == null) return;
        var text = _project.Diagnostics.Count == 0 ? "没有项目诊断。" : string.Join(Environment.NewLine, _project.Diagnostics);
        MessageBox.Show(this, text, $"{_project.DisplayName} - 项目诊断", MessageBoxButtons.OK,
            _project.IsReadOnly ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z) && _project != null) { _historyPage.UndoLatest(); return true; }
        if (keyData == (Keys.Control | Keys.Y) && _project != null) { _historyPage.RedoLatest(); return true; }
        if (_characterTabs.SelectedIndex == 0 && keyData == (Keys.Control | Keys.S)) { _dataPage.SaveAssignments(); return true; }
        if (CurrentGridPage() is { } page && page.ExecuteShortcut(keyData)) return true;
        if (keyData == Keys.F5) { ReloadProject(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private ImageGridPage? CurrentGridPage()
    {
        if (_mainTabs.SelectedIndex == 0) return _characterTabs.SelectedIndex switch { 1 => _rPage, 2 => _sPage, _ => null };
        return _mainTabs.SelectedIndex switch { 1 => _portraitPage, 2 => _itemPage, 3 => _strategyPage.ImageGrid, 4 => _scenePage, 5 => _otherPage, _ => null };
    }

    private static string FindApplicationRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (directory.Name.Equals("形象管理器", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
            directory = directory.Parent;
        }
        return AppContext.BaseDirectory;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _previews.Dispose();
        base.Dispose(disposing);
    }
}
