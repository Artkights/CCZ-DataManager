using System.Globalization;
using DataManager.Core.Models;
using ImageManager.App;

namespace DataManager.App;

public sealed partial class EngineEffectDetailPanel
{
    private readonly TableLayoutPanel _root = new();
    private readonly TableLayoutPanel _header = new();
    private readonly Label _title = new();
    private readonly Label _suffixLabel = new();
    private readonly Label _stateLabel = new();
    private readonly Button _writeButton = new() { Text = "写入引擎", AutoSize = true, Enabled = false };
    private readonly Button _revertButton = new() { Text = "放弃修改", AutoSize = true, Enabled = false };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _bodyViewport = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private readonly TableLayoutPanel _bodyLayout = new();
    private readonly RowStyle _textRow = new(SizeType.Absolute, 182);
    private readonly RowStyle _callRow = new(SizeType.Absolute, 140);
    private readonly Label _emptyLabel = new();
    private Label _callSectionLabel = new();
    private readonly TableLayoutPanel _textGrid = new();
    private readonly ComboBox _suffixCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ByteTextEditor _nameEditor = new() { CompactBudget = true };
    private readonly ByteTextEditor _descriptionEditor = new();
    private readonly TableLayoutPanel _callGrid = new();
    private readonly ComboBox _entryCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _functionEntryLabel = new();
    private readonly ComboBox _valueModeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _stackingCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _itemIdCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _personalIdCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolTip _callTips = new() { AutoPopDelay = 30000 };
    private readonly TabControl _callsTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _effectCallsPage = new("调用点（0）");
    private readonly TabPage _unassignedCallsPage = new("未归属调用（0）");
    private readonly TabPage _internalCallsPage = new("引擎内部（0）");
    private readonly TabPage _derivedCallsPage = new("派生/表驱动/循环（0）");
    private readonly TabPage _referencesPage = new("跨文件引用（0）");
    private readonly TabPage _callPathsPage = new("调用链（0）");
    private readonly TabPage _evidencePage = new("反汇编证据");
    private readonly DataGridView _callsGrid = new BufferedCallsGrid();
    private readonly DataGridView _unassignedGrid = new BufferedCallsGrid();
    private readonly DataGridView _internalGrid = new BufferedCallsGrid();
    private readonly DataGridView _derivedGrid = new BufferedCallsGrid();
    private readonly DataGridView _referenceGrid = new BufferedCallsGrid();
    private readonly DataGridView _callPathsGrid = new BufferedCallsGrid();
    private readonly Label _callsEmpty = new();
    private readonly Label _unassignedEmpty = new();
    private readonly Label _internalEmpty = new();
    private readonly Label _derivedEmpty = new();
    private readonly Label _callPathsEmpty = new();
    private readonly TextBox _referenceSummary = new();
    private readonly Label _referencesEmpty = new();
    private readonly TextBox _evidenceBox = new();
    private readonly Button _writeCallButton = new() { Text = "写入此调用", AutoSize = true, Enabled = false };
    private readonly Button _writeDerivedButton = new() { Text = "写入此调用", AutoSize = true, Enabled = false };

    private void BuildLayout()
    {
        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 1;
        _root.RowCount = 2;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.Padding = Padding.Empty;

        BuildHeader();
        BuildBody();
        _root.Controls.Add(_header, 0, 0);
        _root.Controls.Add(_body, 0, 1);

        _emptyLabel.Dock = DockStyle.Fill;
        _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        _emptyLabel.ForeColor = Color.DimGray;
        _emptyLabel.BackColor = Color.White;
        _emptyLabel.Visible = false;

        Controls.Add(_root);
        FitSections();
    }

    private void BuildHeader()
    {
        _header.Dock = DockStyle.Fill;
        _header.AutoSize = true;
        _header.ColumnCount = 2;
        _header.RowCount = 1;
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.BackColor = Color.FromArgb(245, 247, 250);
        _header.Padding = new Padding(8, 4, 8, 4);
        _header.Margin = Padding.Empty;

        _title.Dock = DockStyle.Fill;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.AutoEllipsis = true;
        _title.Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
        _title.Margin = new Padding(0, 0, 12, 0);
        _suffixLabel.AutoSize = true;
        _suffixLabel.ForeColor = Color.DimGray;
        _suffixLabel.TextAlign = ContentAlignment.MiddleLeft;
        _suffixLabel.Margin = new Padding(0, 0, 12, 0);
        _stateLabel.AutoSize = true;
        _stateLabel.ForeColor = Color.DimGray;
        _stateLabel.TextAlign = ContentAlignment.MiddleLeft;
        _stateLabel.Margin = new Padding(0, 0, 12, 0);

        var text = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        text.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        text.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        text.Controls.Add(_title, 0, 0);
        text.Controls.Add(_suffixLabel, 1, 0);
        text.Controls.Add(_stateLabel, 2, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Right
        };
        _writeButton.Margin = new Padding(6, 0, 0, 0);
        _revertButton.Margin = new Padding(6, 0, 0, 0);
        buttons.Controls.Add(_writeButton);
        buttons.Controls.Add(_revertButton);

        _header.Controls.Add(text, 0, 0);
        _header.Controls.Add(buttons, 1, 0);
    }

    private void BuildBody()
    {
        _bodyLayout.Dock = DockStyle.None;
        _bodyLayout.Location = Point.Empty;
        _bodyLayout.ColumnCount = 1;
        _bodyLayout.RowCount = 3;
        _bodyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bodyLayout.RowStyles.Add(_textRow);
        _bodyLayout.RowStyles.Add(_callRow);
        _bodyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _bodyLayout.Padding = new Padding(8, 4, 8, 8);

        _bodyViewport.Controls.Add(_bodyLayout);
        _body.Controls.Add(_bodyViewport);
        _body.Controls.Add(_emptyLabel);
        _emptyLabel.BringToFront();
        _bodyLayout.Controls.Add(BuildTextSection(), 0, 0);
        _bodyLayout.Controls.Add(BuildCallSection(), 0, 1);
        _bodyLayout.Controls.Add(BuildCallsSection(), 0, 2);
        _bodyViewport.SizeChanged += (_, _) => LayoutBodyCanvas();
    }

    private Control BuildTextSection()
    {
        var section = CreateSection("文本", out var content, out _);
        _textGrid.Dock = DockStyle.Fill;
        _textGrid.ColumnCount = 2;
        _textGrid.RowCount = 3;
        _textGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        _textGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _textGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        _textGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        _textGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _textGrid.Margin = Padding.Empty;
        _textGrid.Padding = Padding.Empty;

        _nameEditor.Dock = DockStyle.Fill;
        _nameEditor.ShowEncodingHint = false;
        _nameEditor.Margin = new Padding(0, 1, 0, 1);
        _suffixCombo.Dock = DockStyle.Fill;
        _suffixCombo.MaximumSize = new Size(420, 0);
        _suffixCombo.Margin = new Padding(0, 1, 0, 1);
        _descriptionEditor.Dock = DockStyle.Fill;
        _descriptionEditor.ShowEncodingHint = false;
        _descriptionEditor.Editor.Multiline = true;
        _descriptionEditor.Editor.ScrollBars = ScrollBars.Vertical;
        _descriptionEditor.Margin = new Padding(0, 1, 0, 1);

        _textGrid.Controls.Add(CreateFieldLabel("名称"), 0, 0);
        _textGrid.Controls.Add(_nameEditor, 1, 0);
        _textGrid.Controls.Add(CreateFieldLabel("后缀"), 0, 1);
        _textGrid.Controls.Add(_suffixCombo, 1, 1);
        _textGrid.Controls.Add(CreateFieldLabel("说明"), 0, 2);
        _textGrid.Controls.Add(_descriptionEditor, 1, 2);
        content.Controls.Add(_textGrid);
        return section;
    }

    private Control BuildCallSection()
    {
        var section = CreateSection("当前调用点", out var content, out var title);
        _callSectionLabel = title;
        _callGrid.Dock = DockStyle.Fill;
        _callGrid.ColumnCount = 4;
        _callGrid.RowCount = 3;
        _callGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        _callGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _callGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        _callGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 3; row++) _callGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        _callGrid.Margin = Padding.Empty;
        _callGrid.Padding = Padding.Empty;

        _entryCombo.Dock = DockStyle.Fill;
        _entryCombo.Margin = new Padding(0, 2, 8, 2);
        _functionEntryLabel.Dock = DockStyle.Fill;
        _functionEntryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _functionEntryLabel.ForeColor = Color.DimGray;
        _functionEntryLabel.Margin = new Padding(0, 2, 0, 2);
        _valueModeCombo.Dock = DockStyle.Fill;
        _valueModeCombo.Margin = new Padding(0, 2, 8, 2);
        _stackingCombo.Dock = DockStyle.Fill;
        _stackingCombo.Margin = new Padding(0, 2, 0, 2);
        _itemIdCombo.Dock = DockStyle.Fill;
        _itemIdCombo.Margin = new Padding(0, 2, 8, 2);
        _personalIdCombo.Dock = DockStyle.Fill;
        _personalIdCombo.Margin = new Padding(0, 2, 0, 2);
        _callTips.SetToolTip(_itemIdCombo, "宝物装备特效号（selector）；后缀是拥有该特效号的宝物名称，多个用 / 分隔。");
        _callTips.SetToolTip(_personalIdCombo, "这是特效行号，人物槽和兵种槽共用；不是兵种ID。分配结果看宝物/兵种特效页。");

        _callGrid.Controls.Add(CreateFieldLabel("判定入口"), 0, 0);
        _callGrid.Controls.Add(_entryCombo, 1, 0);
        _callGrid.Controls.Add(CreateFieldLabel("函数入口"), 2, 0);
        _callGrid.Controls.Add(_functionEntryLabel, 3, 0);
        _callGrid.Controls.Add(CreateFieldLabel("效果值模式"), 0, 1);
        _callGrid.Controls.Add(_valueModeCombo, 1, 1);
        _callGrid.Controls.Add(CreateFieldLabel("叠加方式"), 2, 1);
        _callGrid.Controls.Add(_stackingCombo, 3, 1);
        _callGrid.Controls.Add(CreateFieldLabel("装备特效号"), 0, 2);
        _callGrid.Controls.Add(_itemIdCombo, 1, 2);
        _callGrid.Controls.Add(CreateFieldLabel("兵种特效号"), 2, 2);
        _callGrid.Controls.Add(_personalIdCombo, 3, 2);

        content.Controls.Add(_callGrid);
        return section;
    }

    private Control BuildCallsSection()
    {
        var section = CreateSection("调用点与证据", out var content, out _);
        _callsTabs.Dock = DockStyle.Fill;
        _callsTabs.TabPages.Add(_effectCallsPage);
        _callsTabs.TabPages.Add(_unassignedCallsPage);
        _callsTabs.TabPages.Add(_internalCallsPage);
        _callsTabs.TabPages.Add(_derivedCallsPage);
        _callsTabs.TabPages.Add(_referencesPage);
        _callsTabs.TabPages.Add(_callPathsPage);
        _callsTabs.TabPages.Add(_evidencePage);
        BuildEffectCallsPage();
        BuildUnassignedCallsPage();
        BuildInternalCallsPage();
        BuildDerivedCallsPage();
        BuildReferencesPage();
        BuildCallPathsPage();
        BuildEvidencePage();
        content.Controls.Add(_callsTabs);
        return section;
    }

    private void BuildEffectCallsPage()
    {
        ConfigureCallGrid(_callsGrid, [("调用点", 14), ("函数入口", 12), ("桩类型", 10), ("判定入口", 10),
            ("叠加方式", 9), ("效果值模式", 9), ("装备特效号", 9), ("兵种特效号", 11), ("状态", 9)]);
        var host = new Panel { Dock = DockStyle.Fill };
        _callsEmpty.Dock = DockStyle.Fill;
        _callsEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _callsEmpty.ForeColor = Color.DimGray;
        _callsEmpty.Text = "无静态调用点";
        _callsEmpty.Visible = false;
        _callsEmpty.BackColor = Color.White;
        host.Controls.Add(_callsGrid);
        host.Controls.Add(_callsEmpty);
        _effectCallsPage.Controls.Add(host);
    }

    private void BuildUnassignedCallsPage()
    {
        var page = _unassignedCallsPage;
        page.Padding = new Padding(2);
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(2, 3, 2, 2),
            WrapContents = false
        };
        _writeCallButton.Enabled = false;
        _writeCallButton.Margin = new Padding(0, 0, 8, 0);
        var hint = new Label
        {
            Text = "动态/上层参数/计算桩；选中后查看证据，可写字段在“写入此调用”中编辑。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 6, 0, 0)
        };
        toolbar.Controls.Add(_writeCallButton);
        toolbar.Controls.Add(hint);
        var host = new Panel { Dock = DockStyle.Fill };
        ConfigureCallGrid(_unassignedGrid, [("调用点", 14), ("函数入口", 12), ("桩类型", 11),
            ("判定入口", 10), ("参数摘要", 26), ("状态/原因", 27)]);
        _unassignedGrid.SelectionChanged += (_, _) => UnassignedSelectionChanged();
        _unassignedEmpty.Dock = DockStyle.Fill;
        _unassignedEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _unassignedEmpty.ForeColor = Color.DimGray;
        _unassignedEmpty.Text = "没有未归属调用";
        _unassignedEmpty.Visible = false;
        _unassignedEmpty.BackColor = Color.White;
        host.Controls.Add(_unassignedGrid);
        host.Controls.Add(_unassignedEmpty);
        page.Controls.Add(host);
        page.Controls.Add(toolbar);
    }

    private void BuildInternalCallsPage()
    {
        var page = _internalCallsPage;
        page.Padding = new Padding(2);
        var host = new Panel { Dock = DockStyle.Fill };
        ConfigureCallGrid(_internalGrid, [("调用点", 16), ("函数入口", 14), ("判定入口", 12), ("内部原因", 58)]);
        _internalGrid.SelectionChanged += (_, _) => InternalSelectionChanged();
        _internalEmpty.Dock = DockStyle.Fill;
        _internalEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _internalEmpty.ForeColor = Color.DimGray;
        _internalEmpty.Text = "没有引擎内部调用";
        _internalEmpty.Visible = false;
        _internalEmpty.BackColor = Color.White;
        host.Controls.Add(_internalGrid);
        host.Controls.Add(_internalEmpty);
        page.Controls.Add(host);
    }

    private void BuildDerivedCallsPage()
    {
        var page = _derivedCallsPage;
        page.Padding = new Padding(2);
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(2, 3, 2, 2),
            WrapContents = false
        };
        _writeDerivedButton.Enabled = false;
        _writeDerivedButton.Margin = new Padding(0, 0, 8, 0);
        var hint = new Label
        {
            Text = "包装桩派生、循环/静态表展开与局部基址循环；表项装备号可写，其余常量只读。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 6, 0, 0)
        };
        toolbar.Controls.Add(_writeDerivedButton);
        toolbar.Controls.Add(hint);
        var host = new Panel { Dock = DockStyle.Fill };
        ConfigureCallGrid(_derivedGrid, [("调用者调用", 14), ("桩内调用", 13), ("桩函数", 12),
            ("参数摘要", 27), ("状态", 12), ("写入约束", 22)]);
        _derivedGrid.SelectionChanged += (_, _) => DerivedSelectionChanged();
        _derivedEmpty.Dock = DockStyle.Fill;
        _derivedEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _derivedEmpty.ForeColor = Color.DimGray;
        _derivedEmpty.Text = "没有派生调用";
        _derivedEmpty.Visible = false;
        _derivedEmpty.BackColor = Color.White;
        host.Controls.Add(_derivedGrid);
        host.Controls.Add(_derivedEmpty);
        page.Controls.Add(host);
        page.Controls.Add(toolbar);
    }

    private void BuildReferencesPage()
    {
        var page = _referencesPage;
        page.Padding = new Padding(2);
        page.BackColor = Color.White;
        var host = new Panel { Dock = DockStyle.Fill };
        ConfigureCallGrid(_referenceGrid, [("人物 ID", 10), ("人物", 22), ("槽", 8),
            ("效果值", 12), ("经验", 12), ("剩余回合", 12)]);
        _referencesEmpty.Dock = DockStyle.Fill;
        _referencesEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _referencesEmpty.ForeColor = Color.DimGray;
        _referencesEmpty.Text = "没有人物槽引用";
        _referencesEmpty.Visible = false;
        _referencesEmpty.BackColor = Color.White;
        host.Controls.Add(_referenceGrid);
        host.Controls.Add(_referencesEmpty);

        _referenceSummary.Dock = DockStyle.Top;
        _referenceSummary.Height = 52;
        _referenceSummary.Multiline = true;
        _referenceSummary.ReadOnly = true;
        _referenceSummary.ScrollBars = ScrollBars.Vertical;
        _referenceSummary.WordWrap = true;
        _referenceSummary.BorderStyle = BorderStyle.None;
        _referenceSummary.BackColor = Color.FromArgb(245, 247, 250);
        _referenceSummary.ForeColor = Color.DimGray;
        _referenceSummary.Text = "只读汇总 Effect.e5 人物槽与 Effectmsg.e5 成长参数；写入请到“拓展数据”页。";

        page.Controls.Add(host);
        page.Controls.Add(_referenceSummary);
    }

    private void BuildCallPathsPage()
    {
        var page = _callPathsPage;
        page.Padding = new Padding(2);
        page.BackColor = Color.White;
        var host = new Panel { Dock = DockStyle.Fill };
        ConfigureCallGrid(_callPathsGrid, [("路径", 6), ("深度", 6), ("根函数", 12),
            ("状态", 10), ("调用链摘要", 66)]);
        _callPathsEmpty.Dock = DockStyle.Fill;
        _callPathsEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _callPathsEmpty.ForeColor = Color.DimGray;
        _callPathsEmpty.Text = "没有 E8 调用链";
        _callPathsEmpty.Visible = false;
        _callPathsEmpty.BackColor = Color.White;
        host.Controls.Add(_callPathsGrid);
        host.Controls.Add(_callPathsEmpty);
        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(4, 2, 4, 2),
            Text = "沿 E8 调用图上溯到无调用者的 E8 根；调用环、未知调用者与深度上限会单独标注。",
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        page.Controls.Add(host);
        page.Controls.Add(hint);
    }

    private void BuildEvidencePage()
    {
        _evidenceBox.Dock = DockStyle.Fill;
        _evidenceBox.Multiline = true;
        _evidenceBox.ReadOnly = true;
        _evidenceBox.ScrollBars = ScrollBars.Both;
        _evidenceBox.WordWrap = false;
        _evidenceBox.BackColor = Color.White;
        _evidenceBox.Font = new Font(FontFamily.GenericMonospace, Math.Max(8.5f, Font.Size - 0.5f));
        _evidenceBox.Text = "选择调用点后显示反汇编证据。";
        _evidencePage.Padding = new Padding(2);
        _evidencePage.Controls.Add(_evidenceBox);
    }

    private static void ConfigureCallGrid(DataGridView grid, IReadOnlyList<(string Name, float Weight)> columns)
    {
        grid.Dock = DockStyle.Fill;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.Margin = Padding.Empty;
        SelectionAppearance.Configure(grid, preserveCurrentRow: true);
        foreach (var (name, weight) in columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = name,
                FillWeight = weight,
                MinimumWidth = 64,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
    }

    private Control CreateSection(string title, out Panel content, out Label titleLabel)
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6),
            Padding = new Padding(8, 4, 8, 6),
            BackColor = Color.White
        };
        titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = title,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(245, 247, 250),
            Padding = new Padding(4, 0, 0, 0),
            Tag = "SectionTitle"
        };
        content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        host.Controls.Add(content);
        host.Controls.Add(titleLabel);
        return host;
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = false,
        Margin = new Padding(0, 0, 6, 0)
    };

    private void FitSections()
    {
        var fontHeight = Font.Height;
        var titleHeight = Math.Max(22, fontHeight + 6);
        foreach (var label in Descendants(this).OfType<Label>().Where(label => Equals(label.Tag, "SectionTitle")))
            label.Height = titleHeight;
        var nameRow = Math.Max(36, fontHeight + 20);
        var suffixRow = Math.Max(30, fontHeight + 12);
        var descriptionRow = Math.Max(56, fontHeight * 3 + 12);
        if (_textGrid.RowStyles.Count >= 2)
        {
            _textGrid.RowStyles[0].Height = nameRow;
            _textGrid.RowStyles[1].Height = suffixRow;
        }
        _textRow.Height = titleHeight + 16 + nameRow + suffixRow + descriptionRow;
        var callRowHeight = Math.Max(30, fontHeight + 12);
        _callRow.Height = titleHeight + 16 + 3 * callRowHeight;
        _referenceSummary.Height = Math.Max(52, fontHeight * 3);
        _title.Height = Math.Max(24, fontHeight + 8);
        LayoutBodyCanvas();
    }

    private void LayoutBodyCanvas()
    {
        var width = _bodyViewport.ClientSize.Width;
        var height = _bodyViewport.ClientSize.Height;
        if (width <= 0 || height <= 0) return;
        var minimumGrid = Math.Max(120, (int)Math.Round(150 * Math.Max(1f, DeviceDpi / 96f)));
        var content = _textRow.Height + _callRow.Height + minimumGrid + _bodyLayout.Padding.Vertical;
        var targetHeight = Math.Max(height, (int)Math.Ceiling(content));
        var targetWidth = targetHeight > height ? Math.Max(1, width - SystemInformation.VerticalScrollBarWidth) : width;
        if (_bodyLayout.Size != new Size(targetWidth, targetHeight))
            _bodyLayout.Size = new Size(targetWidth, targetHeight);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class BufferedCallsGrid : DataGridView
    {
        public BufferedCallsGrid() => DoubleBuffered = true;
    }

    internal static string StackingText(int? value) => value switch
    {
        0 => "叠加",
        1 => "不叠加",
        2 => "装备优先",
        3 => "待测试",
        _ => "动态"
    };

    internal static string ValueModeText(int? value) => value switch
    {
        0 => "读数值",
        1 => "返回开关",
        null => "动态",
        _ => value.Value.ToString(CultureInfo.InvariantCulture)
    };

    internal static string StatusText(EngineEffectCallStatus status) => status switch
    {
        EngineEffectCallStatus.Static => "静态",
        EngineEffectCallStatus.Dynamic => "动态",
        _ => "未解析"
    };

    private static string CallStatusText(EngineEffectCallSite call)
        => call.IsReachable == false ? $"{StatusText(call.Status)}·不可达" : StatusText(call.Status);

    private sealed record SuffixChoice(byte Value, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record EntryChoice(uint Target, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record ModeChoice(int Value, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record StackingChoice(int Value, string Display)
    {
        public override string ToString() => Display;
    }
}
