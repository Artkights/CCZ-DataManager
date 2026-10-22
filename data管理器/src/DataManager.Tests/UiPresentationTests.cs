using System.Data;
using System.Reflection;
using DataManager.App;
using DataManager.Images;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class UiPresentationTests
{
    public static void Run(string? sampleRoot = null)
    {
        CheckApplicationIcon();
        CheckSelectionAndBinding();
        CheckSourceLabels();
        var help = BuiltInDocumentation.ReadText();
        TestSupport.Assert(help.Contains("导出参考图", StringComparison.Ordinal) && help.Contains("单文件版本", StringComparison.Ordinal),
            "使用说明没有嵌入程序集，单 EXE 无法打开帮助");
        if (sampleRoot != null) CheckBusinessPageBinding(sampleRoot);
        Console.WriteLine("UI_PRESENTATION_TESTS_OK");
    }

    private static void CheckApplicationIcon()
    {
        const string resourceName = "DataManager.App.Assets.About.Doro-white.ico";
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName);
        TestSupport.Assert(stream != null && stream.Length > 0, "Doro-white.ico 没有嵌入 DataManager.App 程序集");
        using var expected = new Icon(stream!);
        using var form = new MainForm(openDefaultProject: false);
        TestSupport.Assert(form.Icon != null && form.Icon.Size == expected.Size,
            "主窗口没有使用嵌入的 Doro-white.ico 图标");
    }

    private static void CheckBusinessPageBinding(string sampleRoot)
    {
        TestSupport.Assert(Directory.Exists(sampleRoot), "真实界面测试基底不存在：" + sampleRoot);
        using var form = new MainForm(openDefaultProject: false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false
        };
        form.LoadProjectForSmokeTest(sampleRoot);
        form.Show(); form.SelectModuleForSmokeTest("人物");
        foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
        {
            form.ClientSize = size; form.PerformLayout();
            Application.DoEvents(); Application.DoEvents();
            var editor = form.BusinessEditorForSmokeTest;
            var boundCombos = Descendants(editor).OfType<SearchableIdComboBox>().Where(combo => combo.Visible).ToArray();
            TestSupport.Assert(boundCombos.Length >= 7, "真实人物页没有创建兵种/装备/头像/R/S下拉框");
            foreach (var combo in boundCombos.Where(combo => !combo.Focused))
                TestSupport.Assert(combo.SelectionLength == 0,
                    $"真实人物页 {size} 下程序绑定值仍全选：{combo.Text}，选区 {combo.SelectionStart}/{combo.SelectionLength}");
            foreach (var label in Descendants(editor).OfType<Label>().Where(label => label.Visible &&
                         label.Text is "初始HP" or "初始MP" or "暴击模式" or "特殊槽位" or "普通类型"))
            {
                var width = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine).Width;
                TestSupport.Assert(label.ClientSize.Width >= width,
                    $"真实人物页 {size} 下标签横向不足：{label.Text}，实际 {label.ClientSize.Width}，所需 {width}");
            }
        }
        form.EntityListForSmokeTest.SelectedIndex = 1;
        Application.DoEvents(); Application.DoEvents();
        TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "只读界面绑定或清理选区意外产生草稿");
        TestSupport.Assert(Descendants(form.BusinessEditorForSmokeTest).OfType<SearchableIdComboBox>()
            .Where(combo => combo.Visible && !combo.Focused).All(combo => combo.SelectionLength == 0),
            "切换真实人物后的程序绑定仍留下默认全选");
        form.Close();
        Console.WriteLine("REAL_PERSON_BINDING_PRESENTATION_OK " + sampleRoot);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void CheckSelectionAndBinding()
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(800, 500) };
        var sink = new Button { Text = "焦点", Location = new Point(5, 5), Size = new Size(70, 25) };
        var data = new DataTable(); data.Columns.Add("名称"); data.Columns.Add("数值", typeof(int));
        data.Rows.Add("君主", 100); data.Rows.Add("步兵", 95);
        var plain = new DataGridView { DataSource = data, BindingContext = new BindingContext(), Location = new Point(5, 40), Size = new Size(350, 200), AllowUserToAddRows = false };
        var preserved = new DataGridView { DataSource = data.Copy(), BindingContext = new BindingContext(), Location = new Point(380, 40), Size = new Size(350, 200), AllowUserToAddRows = false };
        SelectionAppearance.Configure(plain);
        SelectionAppearance.Configure(preserved, preserveCurrentRow: true);
        var combo = new SearchableIdComboBox { Location = new Point(5, 260), Width = 240 };
        combo.SetOptions([new IdNameOption(12, "头像"), new IdNameOption(120, "将领")]);
        combo.SetBoundValue("12：头像");
        form.Controls.AddRange([sink, plain, preserved, combo]); form.ActiveControl = sink;
        form.Show(); sink.Focus(); Application.DoEvents();
        TestSupport.Assert(plain.CurrentCell == null && plain.SelectedCells.Count == 0, "普通详情表打开后仍默认选中首行");
        TestSupport.Assert(combo.SelectionLength == 0, "程序绑定的下拉文字被默认全选");
        TestSupport.Assert(plain.ColumnHeadersDefaultCellStyle.SelectionBackColor == plain.ColumnHeadersDefaultCellStyle.BackColor,
            "普通表头仍使用蓝色选择背景");
        preserved.CurrentCell = preserved.Rows[1].Cells[0]; preserved.Rows[1].Selected = true;
        plain.Focus(); plain.CurrentCell = plain.Rows[1].Cells[0]; plain.Rows[1].Selected = true;
        Application.DoEvents();
        TestSupport.Assert(plain.DefaultCellStyle.SelectionBackColor == SystemColors.Highlight, "鼠标/键盘进入表格后丢失正常选择反馈");
        sink.Focus(); Application.DoEvents();
        TestSupport.Assert(plain.CurrentCell?.RowIndex == 1, "表格失焦破坏了用户当前单元格");
        TestSupport.Assert(SystemInformation.HighContrast || preserved.DefaultCellStyle.SelectionBackColor != SystemColors.Highlight,
            "辅助分组失焦仍显示为蓝色大块");
        TestSupport.Assert(preserved.CurrentCell?.RowIndex == 1, "失焦清理破坏了辅助分组当前行联动");
        combo.Focus(); combo.SelectionStart = 1; combo.SelectionLength = 3;
        combo.SetOptions([new IdNameOption(12, "头像"), new IdNameOption(120, "将领")], preserveEditSelection: true);
        combo.SetBoundValue("12：头像", preserveEditSelection: true);
        TestSupport.Assert(combo.SelectionStart == 1 && combo.SelectionLength == 3, "异步资源选项回填破坏了用户主动选择的文字");
        combo.SetBoundValue("120：将领");
        TestSupport.Assert(combo.SelectionLength == 0 && combo.TryGetSelectedId(out var id) && id == 120, "下拉重新绑定产生选择残留或 ID 错误");
        form.Close();
    }

    private static void CheckSourceLabels()
    {
        ImageResourceDescriptor Descriptor(string key, ResourceKind kind, ImageCategory category, params string[] names)
            => new(key, key, category, kind, names.Select(name => Path.Combine("F:\\项目\\E5", name)).ToArray(), 1, true, true, false, "", "");
        var resources = new[]
        {
            Descriptor("Item.e5", ResourceKind.E5Indexed, ImageCategory.ItemIcon, "Item.e5"),
            Descriptor("Weather.e5", ResourceKind.WeatherSpecial, ImageCategory.Dynamic, "Weather.e5"),
            Descriptor("Pmap.e5", ResourceKind.PmapSpecial, ImageCategory.Scene, "Pmap.e5"),
            Descriptor("SComposite", ResourceKind.SComposite, ImageCategory.CharacterS, "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5")
        };
        var build = typeof(IntegratedResourcePanel).GetMethod("BuildProjectCategories", BindingFlags.Static | BindingFlags.NonPublic)!;
        var categories = ((IReadOnlyList<(string Key, string Name)>)build.Invoke(null, [resources])!).ToDictionary(category => category.Key, category => category.Name);
        TestSupport.Assert(categories["Item"] == "宝物图标（Item.e5）", "宝物来源没有使用项目当前真实容器");
        TestSupport.Assert(categories["Resource:Weather.e5"] == "天气图片（Weather.e5）", "天气名称缺少出处文件");
        TestSupport.Assert(categories["Resource:Pmap.e5"] == "战场地形（Pmap.e5）", "战场图片没有改名为战场地形");
        TestSupport.Assert(new[] { "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" }.All(file => categories["S"].Contains(file, StringComparison.Ordinal)),
            "S 形象组合来源丢失其中的物理文件");
        var dll = Descriptor("Itemicon.dll", ResourceKind.DllBitmap, ImageCategory.ItemIcon, "Itemicon.dll");
        categories = ((IReadOnlyList<(string Key, string Name)>)build.Invoke(null, [new[] { dll }])!).ToDictionary(category => category.Key, category => category.Name);
        TestSupport.Assert(categories["Item"] == "宝物图标（Itemicon.dll）", "6.5 宝物来源被硬编码为 6.6 E5 档案");
    }
}
