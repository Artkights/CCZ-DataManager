using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DataManager.App;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineAssignmentUiTests
{
    public static void Run(string sourceRoot, string outputDirectory)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            Directory.CreateDirectory(outputDirectory);
            RunSelectionContractChecks();
            RunValueModeChecks();
            using var form = new MainForm(openDefaultProject: false) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
            form.Show();
            form.LoadProjectForSmokeTest(copy);
            form.SelectModuleForSmokeTest("__engine");
            var scanDeadline = DateTime.UtcNow.AddSeconds(60);
            do { Pump(); } while (form.EngineScanPendingForSmokeTest && DateTime.UtcNow < scanDeadline);
            Pump();
            TestSupport.Assert(!form.EngineScanPendingForSmokeTest, "后台引擎扫描没有及时完成。");
            var subpages = form.EngineSubTabsForSmokeTest;
            TestSupport.Assert(subpages.TabPages.Count == 4, "引擎扫描必须保留四个子页（公式设定已改为顶级页）。");
            TestSupport.Assert(subpages.TabPages.Cast<TabPage>()
                    .All(page => page.Text != "战斗公式"), "引擎扫描仍包含战斗公式子页。");
            TestSupport.Assert(subpages.Visible, "引擎扫描子页导航不可见。");
            var effectSelection = form.EntityListForSmokeTest.SelectedItem?.ToString();
            var detail = form.EngineDetailForSmokeTest;
            var choiceCatalog = form.EngineAssignmentDraftForSmokeTest!.Catalog;
            var itemChoices = detail.ItemIdComboForSmokeTest.Items.Cast<EngineEffectChoiceItem>()
                .Where(choice => !choice.Fallback).ToArray();
            var jobChoices = detail.PersonalIdComboForSmokeTest.Items.Cast<EngineEffectChoiceItem>()
                .Where(choice => !choice.Fallback).ToArray();
            var expectedItemIds = choiceCatalog.KnownItemSelectors
                .Concat(choiceCatalog.Effects.SelectMany(effect => effect.ItemSelectors))
                .Where(id => id is >= 0 and < 255).Distinct().Order().ToArray();
            var expectedJobIds = choiceCatalog.Effects
                .Where(effect => effect.PersonalEffectId is >= 0 and < 255)
                .Select(effect => effect.PersonalEffectId!.Value).Distinct().Order().ToArray();
            TestSupport.Assert(itemChoices.Select(choice => choice.Id).SequenceEqual(expectedItemIds),
                "宝物特效号下拉数量与基底数据不一致。");
            TestSupport.Assert(jobChoices.Select(choice => choice.Id).SequenceEqual(expectedJobIds),
                "兵种特效号下拉数量与基底数据不一致。");
            TestSupport.Assert(itemChoices.All(choice => choice.ToString().StartsWith(choice.Id + "：", StringComparison.Ordinal)) &&
                jobChoices.All(choice => choice.ToString().StartsWith(choice.Id + "：", StringComparison.Ordinal)),
                "特效号下拉没有使用“立即数：名称”格式。");
            TestSupport.Assert(itemChoices.Any(choice => choice.Name != "未分配"),
                "宝物特效号下拉没有显示宝物名称。");
            TestSupport.Assert(jobChoices.Any(choice => choice.Name != "未分配"),
                "兵种特效号下拉没有显示兵种名称。");
            TestSupport.Assert(detail.CallFieldLabelsForSmokeTest.Contains("兵种特效号", StringComparison.Ordinal) &&
                !detail.CallFieldLabelsForSmokeTest.Contains("个人/兵种", StringComparison.Ordinal),
                "调用点栏位没有改成“兵种特效号”。");
            TestSupport.Assert(detail.CallsGridForSmokeTest.Columns[7].HeaderText == "兵种特效号",
                "调用点表头没有改成“兵种特效号”。");
            Console.WriteLine($"ENGINE_EFFECT_CHOICES_OK {Path.GetFileName(sourceRoot)} items={itemChoices.Length} jobs={jobChoices.Length}");
            Console.WriteLine("ENGINE_EFFECT_CHOICES_SAMPLE item=" +
                itemChoices.FirstOrDefault(choice => choice.Name != "未分配") +
                " job=" + jobChoices.FirstOrDefault(choice => choice.Name != "未分配"));
            subpages.SelectedIndex = 1;
            Pump();
            var items = form.EngineItemsForSmokeTest;
            TestSupport.Assert(items.Visible && !form.EngineDetailForSmokeTest.Visible, "宝物子页可见性不正确。");
            TestSupport.Assert(form.EntityListForSmokeTest.Items.Count > 100, "宝物页左列没有同步。");
            TestSupport.Assert(items.SharedForSmokeTest.Contains("当前装备特效号：", StringComparison.Ordinal), "宝物页没有显示共享匹配号。");
            if (Path.GetFileName(sourceRoot).Contains("国庆", StringComparison.Ordinal))
                TestSupport.Assert(!items.SharedForSmokeTest.Contains("165：", StringComparison.Ordinal), "共享装备号错误地纳入独立namespace中的消耗品165。");
            AssertColumns(items);
            var itemSelection = form.EntityListForSmokeTest.SelectedItem?.ToString();
            items.FilterForSmokeTest.Text = "HP";
            var filteredRows = items.PoolGridForSmokeTest.Rows.Count;
            var draft = form.EngineAssignmentDraftForSmokeTest!;
            var service = new EngineEffectAssignmentService();
            var effect = draft.Catalog.Effects.FirstOrDefault(effect => effect.CanBindItem && effect.ItemSelectors.Count == 1 && effect.ItemSelectors[0] != 255);
            if (effect != null)
            {
                var result = service.StageItemUnbind(draft, effect.Key);
                TestSupport.Assert(result.Success && draft.HasPendingChanges, "UI分配草稿没有建立。");
            }
            subpages.SelectedIndex = 2;
            Pump();
            var jobs = form.EngineJobsForSmokeTest;
            TestSupport.Assert(jobs.Visible && !items.Visible, "兵种子页可见性不正确。");
            TestSupport.Assert(form.EntityListForSmokeTest.Items.Count == draft.Catalog.Jobs.Count, "兵种页左列没有同步。");
            AssertColumns(jobs);
            TestSupport.Assert(jobs.PoolGridForSmokeTest.Rows.Count > 0, "兵种特效池为空。");
            foreach (DataGridViewRow row in jobs.PoolGridForSmokeTest.Rows)
            {
                var data = (EngineAssignmentDisplayRow)row.Tag!;
                TestSupport.Assert(!data.Full || data.Name.EndsWith("*0", StringComparison.Ordinal), "兵种满槽标识不正确。");
                if (data.Full && data.Selectable) TestSupport.Assert(row.Cells[1].Style.ForeColor == Color.Firebrick, "*0条目没有红色提示。");
                if (!data.Selectable) TestSupport.Assert(row.DefaultCellStyle.ForeColor == Color.Gray, "未验证条目没有灰色提示。");
                if (!data.Selectable) TestSupport.Assert(!string.IsNullOrWhiteSpace(data.ReadOnlyReason), "兵种灰行没有真实只读原因。");
                var binding = draft.Catalog.Effects.Single(effect => effect.Key == data.Key);
                if (binding.SharedJobValue && binding.JobSlots.Count > 0)
                    TestSupport.Assert(data.Value == "共用N=" + binding.JobSlots[0].Value, "共享N兵种池没有显示本行实际N。");
            }
            subpages.SelectedIndex = 1;
            Pump();
            TestSupport.Assert(form.EntityListForSmokeTest.SelectedItem?.ToString() == itemSelection, "切回宝物页丢失左列选择。");
            TestSupport.Assert(items.FilterForSmokeTest.Text == "HP" && items.PoolGridForSmokeTest.Rows.Count == filteredRows, "切换子页丢失筛选状态。");
            TestSupport.Assert(effect == null || draft.HasPendingChanges, "切换子页丢失草稿。");
            items.FilterForSmokeTest.Clear();
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
            {
                form.Size = size;
                if (form.Size != size) SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
                foreach (var page in new[] { 1, 2 })
                {
                    subpages.SelectedIndex = page;
                    form.PerformLayout();
                    Pump(10);
                    var panel = page == 1 ? items : jobs;
                    TestSupport.Assert(subpages.Bounds.Bottom <= panel.Bounds.Top || !subpages.Bounds.IntersectsWith(panel.Bounds), "三子页导航被Fill面板覆盖。");
                    TestSupport.Assert(panel.AssignedGridForSmokeTest.Height >= 65, $"{size} 已分配表被压缩。");
                    TestSupport.Assert(panel.PoolGridForSmokeTest.Height >= 65, $"{size} 特效池被压缩。");
                    TestSupport.Assert(panel.DetailsForSmokeTest.Height >= panel.Font.Height * 2, $"{size} 来源详情被压缩。");
                    TestSupport.Assert(panel.PoolGridForSmokeTest.Columns.Cast<DataGridViewColumn>().Sum(column => column.Width) <= panel.PoolGridForSmokeTest.ClientSize.Width + 2, $"{size} 四列出现水平溢出。");
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(outputDirectory, $"{(page == 1 ? "宝物" : "兵种")}-特效分配-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                    Console.WriteLine($"ASSIGNMENT_UI {page}/{size.Width}x{size.Height}: assigned={panel.AssignedGridForSmokeTest.Size} pool={panel.PoolGridForSmokeTest.Size} detail={panel.DetailsForSmokeTest.Size}");
                }
            }
            subpages.SelectedIndex = 2;
            var grayPoolRows = jobs.PoolGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Where(row => row.Tag is EngineAssignmentDisplayRow { Selectable: false }).ToArray();
            TestSupport.Assert(grayPoolRows.Length > 0, "真实兵种池未保留原生能力的灰色只读条目。");
            jobs.PoolGridForSmokeTest.FirstDisplayedScrollingRowIndex = Math.Max(0,
                jobs.PoolGridForSmokeTest.RowCount - jobs.PoolGridForSmokeTest.DisplayedRowCount(false));
            Pump(10);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(outputDirectory, "兵种-灰色能力池-1920x1080.png"), ImageFormat.Png);
            }
            draft.Clear();
            VerifyLiveSelectionFlow(form);
            draft.Clear();
            subpages.SelectedIndex = 0;
            Pump();
            TestSupport.Assert(form.EntityListForSmokeTest.SelectedItem?.ToString() == effectSelection, "切回管理页丢失特效选择。");
            form.Close();
            File.WriteAllText(Path.Combine(outputDirectory, "UI验收结果.txt"),
                "ENGINE_ASSIGNMENT_UI_TESTS_OK\r\n1280x720 / 1920x1080\r\n四列、选择与筛选保持、鼠标/键盘选择预处理、取消无草稿、未知条目清选择、开关/数值/混合模式、独立槽新值必填、单槽复用全行N、继承来源卸下影响范围。\r\n");
            Console.WriteLine("ENGINE_ASSIGNMENT_UI_TESTS_OK");
        }
        finally { Directory.Delete(copy, true); }
    }

    private static void RunSelectionContractChecks()
    {
        using var host = new Form { Location = new Point(-30000, -30000), ShowInTaskbar = false };
        using var panel = new EngineAssignmentPanel(true);
        host.Controls.Add(panel); host.Show();
        var full = new EngineAssignmentDisplayRow("full", "effect:1", "1", "满槽效果 *0", "双击后填写", "说明", "来源和影响", true, true);
        var available = full with { SelectionKey = "free", Key = "effect:2", Name = "空槽效果 *2", Full = false };
        const string rawReason = "未找到：尚未识别完整的装备绑定点。";
        var unknown = full with { SelectionKey = "unknown", Key = "effect:3", Selectable = false, ReadOnlyReason = rawReason };
        var preparationCalls = 0; var staged = 0; var allow = false;
        panel.PreparePoolSelection = _ => { preparationCalls++; return allow; };
        panel.BindRequested += (_, _) => staged++;
        panel.BindEntity("测试兵种", "来源", [], [full, available, unknown], false);
        Pump();
        TestSupport.Assert(preparationCalls == 0, "初始填表不应弹出替换槽提示。");
        SelectRow(panel.PoolGridForSmokeTest, 0); Pump();
        TestSupport.Assert(preparationCalls == 1 && staged == 0, "选中满槽条目必须准备而不stage。");
        TestSupport.Assert(panel.PoolGridForSmokeTest.SelectedRows.Count == 0 && panel.PreparedPoolKeyForSmokeTest == null, "取消满槽选择后仍有有效选择。");
        allow = true;
        SelectRow(panel.PoolGridForSmokeTest, 0); Pump();
        TestSupport.Assert(preparationCalls == 2 && staged == 0 && panel.PreparedPoolKeyForSmokeTest == "full", "满槽确认不应产生草稿。");
        // CurrentCell transitions are the same path used by keyboard arrows.
        SelectRow(panel.PoolGridForSmokeTest, 1); Pump();
        TestSupport.Assert(preparationCalls == 3 && staged == 0, "键盘切换选择没有进入准备流程。");
        panel.BindSelectedForSmokeTest();
        TestSupport.Assert(staged == 1 && preparationCalls == 3, "双击应复用确认后只stage一次。");
        SelectRow(panel.PoolGridForSmokeTest, 2); Pump();
        TestSupport.Assert(panel.PoolGridForSmokeTest.SelectedRows.Count == 0 && panel.PreparedPoolKeyForSmokeTest == null, "未找到条目没有清有效选择。");
        TestSupport.Assert(panel.DetailsForSmokeTest.Text.StartsWith(rawReason, StringComparison.Ordinal), "未找到提示没有保留原文。");
        using var blank = new EngineAssignmentValueDialog("新槽", null);
        TestSupport.Assert(blank.ValueForSmokeTest.Text.Length == 0 && !blank.CanAcceptForSmokeTest, "新槽不应默认N=1或允许空白提交。");
        blank.ValueForSmokeTest.Text = "256";
        TestSupport.Assert(!blank.CanAcceptForSmokeTest, "超出单字节值仍可提交。");
        blank.ValueForSmokeTest.Text = "0";
        TestSupport.Assert(blank.CanAcceptForSmokeTest, "显式输入0应可提交。");
        panel.BindEntity("开关模式", "来源", [full with { CanEditValue = false, Value = "开关／不读取装备数值" }], [], false);
        TestSupport.Assert(!panel.ValueButtonForSmokeTest.Enabled, "开关模式条目仍允许修改共享N。");
        host.Close();
    }

    private static void RunValueModeChecks()
    {
        var item = new EngineEffectItemEntity(16, "测试宝物", 37, "武器", 50, "Data.e5 +0x16", [16]) { ValueFileOffset = 100 };
        var point = new EngineEffectCallSite { ValueMode = 1 };
        var effect = new EngineEffectAssignmentEffect { BindingPoints = [point] };
        var info = MainForm.DescribeItemValue(effect, item);
        TestSupport.Assert(info.Display == "开关／不读取装备数值" && !info.Editable, "模式1错误地显示或允许修改共享N。");
        info = MainForm.DescribeItemValue(effect with { BindingPoints = [new EngineEffectCallSite { ValueMode = 0 }] }, item);
        TestSupport.Assert(info.Display == "N=50" && info.Editable && info.Explanation.Contains(item.ValueSource, StringComparison.Ordinal), "模式0缺少数值或来源。");
        info = MainForm.DescribeItemValue(effect with { BindingPoints = [point, new EngineEffectCallSite { ValueMode = 0 }] }, item);
        TestSupport.Assert(info.Display.Contains("混合", StringComparison.Ordinal) && info.Explanation.Contains("仅模式0", StringComparison.Ordinal), "混合值模式没有说明读取差异。");
    }

    private static void VerifyLiveSelectionFlow(MainForm form)
    {
        var tabs = form.EngineSubTabsForSmokeTest;
        var draft = form.EngineAssignmentDraftForSmokeTest!;
        tabs.SelectedIndex = 2; tabs.SelectedIndex = 1; Pump();
        var panel = form.EngineItemsForSmokeTest;
        panel.FilterForSmokeTest.Clear();
        var occupied = panel.PoolGridForSmokeTest.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => row.Tag is EngineAssignmentDisplayRow { Full: true, Selectable: true });
        if (occupied != null)
        {
            var messages = new List<string>();
            form.AssignmentConfirmationForSmokeTest = (_, text) => { messages.Add(text); return false; };
            SelectRow(panel.PoolGridForSmokeTest, occupied.Index); Pump();
            TestSupport.Assert(messages.Count == 1 && messages[0].Contains("该特效已分配给：", StringComparison.Ordinal), "选中占用条目没有立即提示全部owner。");
            TestSupport.Assert(!draft.HasPendingChanges && panel.PoolGridForSmokeTest.SelectedRows.Count == 0, "取消转移后产生了草稿或残留选择。");
            form.AssignmentConfirmationForSmokeTest = (_, text) => { messages.Add(text); return true; };
            SelectRow(panel.PoolGridForSmokeTest, occupied.Index); Pump();
            TestSupport.Assert(!draft.HasPendingChanges, "转移确认阶段不应stage。");
            panel.BindSelectedForSmokeTest(); Pump();
            TestSupport.Assert(draft.HasPendingChanges, "双击没有stage已准备的转移。");
            draft.Clear();
        }
        tabs.SelectedIndex = 2; Pump();
        var jobs = form.EngineJobsForSmokeTest;
        var full = jobs.PoolGridForSmokeTest.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => row.Tag is EngineAssignmentDisplayRow { Full: true, Selectable: true });
        if (full != null)
        {
            var selectedEffect = draft.Catalog.Effects.Single(effect => effect.Key == ((EngineAssignmentDisplayRow)full.Tag!).Key);
            var choices = 0;
            form.AssignmentSlotChoiceForSmokeTest = (_, _) => { choices++; return null; };
            SelectRow(jobs.PoolGridForSmokeTest, full.Index); Pump();
            TestSupport.Assert(choices == 1 && !draft.HasPendingChanges && jobs.PoolGridForSmokeTest.SelectedRows.Count == 0, "选中*0后取消没有保持干净状态。");
            form.AssignmentSlotChoiceForSmokeTest = (_, _) => { choices++; return selectedEffect.JobSlots.Count - 1; };
            SelectRow(jobs.PoolGridForSmokeTest, full.Index); Pump();
            TestSupport.Assert(choices == 2 && !draft.HasPendingChanges, "选择替换槽时提前stage。");
            var sawBlank = false;
            var valueRequests = 0;
            form.AssignmentValueForSmokeTest = (_, current) => { valueRequests++; sawBlank = current == null; return 19; };
            jobs.BindSelectedForSmokeTest(); Pump();
            TestSupport.Assert(draft.HasPendingChanges, "双击未暂存真实兵种槽。");
            if (selectedEffect.SharedJobValue)
                TestSupport.Assert(valueRequests == 0 && draft.JobTargets[selectedEffect.Key][0].Value == selectedEffect.JobSlots[0].Value,
                    "单兵种槽绑定未复用全行N，或不必要地要求输入新N。");
            else TestSupport.Assert(sawBlank && valueRequests == 1, "独立槽新值没有要求空白填写。");
            draft.Clear();
        }
        var inheritedEffect = draft.Catalog.Effects.FirstOrDefault(effect => effect.CanBindJob && effect.JobSlots.Any(slot => !slot.IsFree && slot.AffectedJobIds.Any(id => id != slot.JobId)));
        if (inheritedEffect != null)
        {
            var inheritedSlot = inheritedEffect.JobSlots.First(slot => !slot.IsFree && slot.AffectedJobIds.Any(id => id != slot.JobId));
            var inheritedId = inheritedSlot.AffectedJobIds.First(id => id != inheritedSlot.JobId);
            form.SelectEngineAssignmentForSmokeTest(inheritedId); Pump();
            var selected = jobs.AssignedGridForSmokeTest.Rows.Cast<DataGridViewRow>().First(row => row.Tag is EngineAssignmentDisplayRow data && data.Key == inheritedEffect.Key && data.Slot == inheritedSlot.Slot);
            SelectRow(jobs.AssignedGridForSmokeTest, selected.Index);
            string? removalText = null;
            form.AssignmentConfirmationForSmokeTest = (_, text) => { removalText = text; return false; };
            jobs.UnbindSelectedForSmokeTest();
            TestSupport.Assert(removalText?.Contains("通过继承", StringComparison.Ordinal) == true && removalText.Contains("当前受影响兵种", StringComparison.Ordinal) && !draft.HasPendingChanges, "卸下继承槽前没有展示范围或取消后仍产生草稿。");
        }
    }

    private static void SelectRow(DataGridView grid, int index)
    {
        grid.ClearSelection(); grid.CurrentCell = grid.Rows[index].Cells[0]; grid.Rows[index].Selected = true;
    }
    private static void AssertColumns(EngineAssignmentPanel panel)
    {
        foreach (var grid in new[] { panel.AssignedGridForSmokeTest, panel.PoolGridForSmokeTest })
            TestSupport.Assert(grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText).SequenceEqual(new[] { "特效号", "特效名", "特效值", "特效说明" }), "分配页没有保持四列约定。");
    }
    private static void Pump(int rounds = 6) { for (var i = 0; i < rounds; i++) { Application.DoEvents(); Thread.Sleep(8); } }
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
