using Ccz.ManagerUi;

namespace RangeManager.App;

public enum RangePage { Attack, Penetration, Extension }

public sealed class MainForm : Form
{
    private readonly RangeManagementPanel _management = new();
    private TabControl _tabs => _management.Tabs;
    private RangeEditorPanel _attack => _management.AttackEditor;
    private RangeEditorPanel _pierce => _management.PierceEditor;
    private RangeExtensionPanel _extension => _management.ExtensionEditor;
    private readonly Label _dirty = new() { AutoSize = true, ForeColor = Color.DarkOrange };
    private readonly Label _status = new() { AutoSize = true, AutoEllipsis = true };
    private readonly Button _save = ManagerUiStyle.CreateButton("保存");
    private readonly Button _undo = ManagerUiStyle.CreateButton("撤回");
    private readonly Button _redo = ManagerUiStyle.CreateButton("前进");
    private RangeEditSession? _session;
    private Icon? _applicationIcon;
    private bool _closingForTest;

    public MainForm(bool openDefaultBase = true)
    {
        Text = $"攻击穿透范围管理器 - 曹操传加强版 6.4/6.5/6.6 · {Application.ProductVersion.Split('+')[0]}";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1500;
        Height = 900;
        MinimumSize = new Size(1100, 650);
        KeyPreview = true;
        using var icon = typeof(MainForm).Assembly.GetManifestResourceStream("RangeManager.App.Doro-white.ico");
        if (icon != null) Icon = _applicationIcon = new Icon(icon);
        BuildLayout();
        FormClosing += (_, e) => { if (!_closingForTest && !PrepareLeave()) e.Cancel = true; };
        if (openDefaultBase)
            Shown += (_, _) =>
            {
                if (_session == null && FindDefaultBase() is { } root) LoadBase(root, silent: true);
            };
    }

    public string LastEditDebug { get; private set; } = "";
    public RangeEditSession? SessionForTest => _session;
    public TabControl TabsForTest => _tabs;
    public RangeEditorPanel AttackEditorForTest => _attack;
    public RangeEditorPanel PierceEditorForTest => _pierce;
    public RangeExtensionPanel ExtensionEditorForTest => _extension;

    private void BuildLayout()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("文件");
        file.DropDownItems.Add("打开基底目录…", null, (_, _) => OpenBaseDialog());
        file.DropDownItems.Add("保存全部修改", null, (_, _) => SaveAll());
        file.DropDownItems.Add("重新载入", null, (_, _) => ReloadBase());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("退出", null, (_, _) => Close());
        var edit = new ToolStripMenuItem("编辑");
        edit.DropDownItems.Add("撤回", null, (_, _) => Undo());
        edit.DropDownItems.Add("前进", null, (_, _) => Redo());
        var help = new ToolStripMenuItem("帮助");
        help.DropDownItems.Add("诊断详情", null, (_, _) => ShowDiagnostics());
        help.DropDownItems.Add("使用说明", null, (_, _) => ShowHelp());
        help.DropDownItems.Add("关于", null, (_, _) => MessageBox.Show(this,
            $"攻击穿透范围管理器\n版本 {Application.ProductVersion.Split('+')[0]}\n\n.NET 8 Windows Forms\n真实范围与 Hitarea.e5 / Effarea.e5 同步保存。",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.AddRange([file, edit, help]);
        MainMenuStrip = menu;

        var toolbar = ManagerUiStyle.CreateToolbar();
        toolbar.Controls.AddRange([_save, _undo, _redo,
            ManagerUiStyle.CreateButton("重新扫描", (_, _) => ReloadBase()),
            ManagerUiStyle.CreateButton("诊断详情", (_, _) => ShowDiagnostics()),
            ManagerUiStyle.CreateButton("打开基底", (_, _) => OpenBaseDialog())]);
        _save.Click += (_, _) => SaveAll();
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();

        var state = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 1, 8, 3) };
        _dirty.Margin = new Padding(0, 0, 12, 0);
        state.Controls.AddRange([_dirty, _status]);

        Controls.Add(_management);
        Controls.Add(state);
        Controls.Add(toolbar);
        Controls.Add(menu);
        void FitShell() => ManagerUiStyle.FitShellText(this, toolbar, _tabs.Parent!, _tabs);
        FontChanged += (_, _) => FitShell();
        HandleCreated += (_, _) => FitShell();
        FitShell();
        RefreshState();
    }

    private static string? FindDefaultBase()
    {
        foreach (string parent in new[] { Application.StartupPath, Directory.GetCurrentDirectory() })
        foreach (string name in new[] { "加强版6.5未加密版", "洗隋", "复刻版6.6山贼" })
        {
            string candidate = Path.Combine(parent, "基底", name);
            if (File.Exists(Path.Combine(candidate, "Ekd5.exe"))) return candidate;
        }
        return null;
    }

    private void OpenBaseDialog()
    {
        if (!PrepareLeave()) return;
        using var dialog = new FolderBrowserDialog { Description = "选择包含 Ekd5.exe 的真实基底目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadBase(dialog.SelectedPath);
    }

    private void LoadBase(string root, bool silent = false)
    {
        try
        {
            var next = new RangeEditSession(root);
            var previous = _session;
            if (previous != null) previous.Changed -= SessionChanged;
            _session = next;
            _session.Changed += SessionChanged;
            _management.Bind(next);
            previous?.Dispose();
            RefreshState();
        }
        catch (Exception ex)
        {
            _status.Text = "打开基底失败：" + ex.Message;
            if (!silent) MessageBox.Show(this, ex.Message, "打开基底失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SessionChanged(object? sender, EventArgs e) => RefreshState();

    private void RefreshState()
    {
        _save.Enabled = _session?.IsDirty == true;
        _undo.Enabled = _session?.CanUndo == true;
        _redo.Enabled = _session?.CanRedo == true;
        _dirty.Text = _session?.IsDirty == true ? "存在未保存修改" : "无未保存修改";
        _status.Text = _session == null ? "未打开基底" : $"基底 {_session.Game.VersionText}  ·  {_session.Game.RootPath}";
    }

    private bool SaveAll(bool silent = false)
    {
        if (_session == null) return false;
        try
        {
            if (!_management.TryCommitPendingEdits(out var error)) throw new InvalidDataException(error);
            _session.Save();
            _attack.RefreshBindings();
            _pierce.RefreshBindings();
            _extension.RefreshMapping();
            RefreshState();
            _status.Text = "已保存真实范围及对应 E5 图片 · " + _session.Game.RootPath;
            return true;
        }
        catch (Exception ex)
        {
            LastEditDebug = ex.ToString();
            _status.Text = "保存失败：" + ex.Message;
            if (!silent) MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void ReloadBase()
    {
        if (_session != null && PrepareLeave()) LoadBase(_session.Game.RootPath);
    }

    private void Undo() { try { _session?.Undo(); } catch (Exception ex) { _status.Text = ex.Message; } }
    private void Redo() { try { _session?.Redo(); } catch (Exception ex) { _status.Text = ex.Message; } }

    private bool PrepareLeave()
    {
        if (_session?.IsDirty != true) return true;
        var choice = MessageBox.Show(this, "当前基底有未保存修改，是否保存？", "未保存修改",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return choice == DialogResult.No || choice == DialogResult.Yes && SaveAll();
    }

    private void ShowDiagnostics() => ShowTextWindow("诊断详情", _session?.Diagnostics ?? "尚未打开基底。");

    private void ShowHelp() => ShowTextWindow("使用说明",
        "打开基底后，上方显示攻击范围、穿透范围和拓展范围三个页签。\r\n\r\n" +
        "左侧为当前基底真实范围编号，点击编号后，右侧按真实生成元绘制。蓝色为自身，红色为攻击，绿色为穿透，黑色为底色；穿透页红框标明预览主目标。\r\n\r\n" +
        "可直接修改下方生成元文字；左键选中格子，右键取消格子。镜像范围会同步修改对称格；方向范围随自身到主目标的方向展开。多组范围通过“编辑组”选择。共享定义的编号会显示同步影响提示。\r\n\r\n" +
        "预览目标默认 (1,0)，调整目标只改变预览上下文。自动网格完整包含当前范围，也可自定义行列和格像素；超出窗口时滚动查看。\r\n\r\n" +
        "新增在引擎支持的范围中分配可用编号。“删除”清空范围并保留编号，防止其他编号或游戏引用错位；“恢复本项”可恢复原始定义，新增项恢复后移除。修改、撤回、前进均在草稿中进行，保存时同时写入真实范围和 E5 图片。\r\n\r\n" +
        "拓展范围页显示当前 EXE 的完整攻击目录（原生 0x00..0x25，另含已安装的管理扩展），并编辑 0x41CEAB 的原生 33 项映射及 CCZRNG03 管理尾部。下一级值允许 0x00..0xFE，0xFF 为保留哨兵；范围图优先读取对应 Hitarea.e5，缺图时依据 EXE 定义生成内存预览。保存前会校验三基底的映射函数和特效判定代码身份。格式无效的值保留在当前编号下，需修正后保存。特殊范围与不支持写入的条目会给出原因。");

    private void ShowTextWindow(string title, string text)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent,
            Size = new Size(880, 640), MinimumSize = new Size(560, 360) };
        dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Both, WordWrap = true, Text = text, BackColor = SystemColors.Window });
        dialog.ShowDialog(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { SaveAll(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void OpenBaseForTest(string root) => LoadBase(root, silent: true);
    // 2 is accepted as the former shape-library index for existing command-line scripts.
    public void SelectPageForTest(int page) => _tabs.SelectedIndex = page switch { 1 => 1, 2 => 2, _ => 0 };
    public bool RunShapeEditSmoke(int id, string cellsText)
    {
        _tabs.SelectedIndex = 0;
        bool result = _attack.EditTextForTest(id, cellsText);
        LastEditDebug = $"attack={id} text=[{cellsText}] valid={result} error=[{_attack.ValidationMessage}]";
        return result;
    }
    public bool SaveForTest() => SaveAll(silent: true);
    public void CloseForTest() { _closingForTest = true; Close(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            if (_session != null) _session.Changed -= SessionChanged;
            _management.Bind(null);
            _session?.Dispose();
            _applicationIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
