using System.Drawing.Imaging;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Scanner;
using TransformManager.App;
using TierManager.App;

namespace DataManager.Tests;

internal static class GrowthIntegrationUiTests
{
    public static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var sourceHash = HashService.Sha256(Path.Combine(source, "Ekd5.exe"));
        var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        try
        {
            using var form = new MainForm(false) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000) };
            form.Show(); form.LoadProjectForSmokeTest(copy);
            var workspace = form.WorkspaceForSmokeTest;
            var draft = form.GrowthForSmokeTest;
            TestSupport.Assert(draft.CanEditRules && draft.CanEditTiers,
                $"标准基底增长设定不可写：{draft.RulesReadOnlyReason} / {draft.TiersReadOnlyReason}");
            form.SelectModuleForSmokeTest("__engine");
            form.EngineSubTabsForSmokeTest.SelectedIndex = 3; Pump();
            TestSupport.Assert(form.EngineSubTabsForSmokeTest.TabPages.Cast<TabPage>().Select(page => page.Text)
                .SequenceEqual(new[] { "特效管理", "宝物特效", "兵种特效", "质变设定", "常量" }), "引擎子页顺序错误。");
            var transform = form.TransformForSmokeTest;
            TestSupport.Assert(transform.Visible && form.EntityListForSmokeTest.Items.Count == draft.Slots.Count, "质变左侧列表未绑定特效号。");
            var selected = draft.Slots.First();
            var beforeRules = selected.Stages.ToList();
            transform.AddForSmokeTest.PerformClick(); Pump();
            TestSupport.Assert(draft.HasRuleChanges && workspace.Commands.CanUndo, "质变新增没有进入可撤销草稿。");
            draft.SetRules(selected.Id, [new RuleStage(5, 17), new RuleStage(9, 23)]); Pump();
            workspace.Commands.Undo(); workspace.Commands.Redo();
            TestSupport.Assert(draft.Slots.First(slot => slot.Id == selected.Id).Stages.Count == 2, "规则撤销重做未恢复模型。");
            form.SelectModuleForSmokeTest("兵种"); Pump();
            var tiers = form.BusinessEditorForSmokeTest.TierEditorForSmokeTest;
            TestSupport.Assert(tiers != null && tiers.Visible && tiers.GridForSmokeTest.Columns.Count == 3, "兵种页未显示精简三列档位。");
            var entries = draft.TierEntries.Select(Clone).ToList();
            var originalLabel = entries[0].Label;
            entries[0].Label = "中"; draft.SetTiers(entries); Pump();
            TestSupport.Assert(tiers!.GridForSmokeTest.Rows[0].Cells[2].Value?.ToString() == "中", "全局档位草稿没有刷新到兵种页。");
            var anotherJob = form.EntityListForSmokeTest.Items.Count > 1 ? 1 : 0;
            form.EntityListForSmokeTest.SelectedIndex = anotherJob; Pump();
            TestSupport.Assert(form.BusinessEditorForSmokeTest.TierEditorForSmokeTest!.GridForSmokeTest.Rows[0].Cells[2].Value?.ToString() == "中", "切换兵种丢失全局档位草稿。");
            entries[0].Label = "中文";
            try { draft.SetTiers(entries); throw new InvalidOperationException("超过2个GBK字节未拒绝。"); }
            catch (InvalidDataException) { }
            entries[0].Label = "AB"; draft.SetTiers(entries);
            var constant = new EngineConstantService().Read(workspace).First(value => value.SignatureMatches && value.Definition.Id == "money-cap");
            TestSupport.Assert(form.StageConstantForSmokeTest(new(constant.Definition.Id, constant.NumericValue!.Value - 1, null)), "常量不能加入统一草稿。");
            var originalBytes = File.ReadAllBytes(Path.Combine(copy, "Ekd5.exe"));
            var failure = new FailAfterAccept(); workspace.RegisterDraftParticipant(failure);
            TestSupport.Assert(!form.CommitIntegratedForSmokeTest() && HashService.Sha256(Path.Combine(copy, "Ekd5.exe")) == sourceHash &&
                draft.HasPendingChanges && ((IWorkspaceDraftParticipant)form).HasPendingChanges && draft.TierEntries[0].Label == "AB",
                "提交后故障没有整体恢复文件和两种草稿。");
            workspace.UnregisterDraftParticipant(failure);
            var externallyChanged = originalBytes.ToArray(); externallyChanged[^1] ^= 1;
            File.WriteAllBytes(Path.Combine(copy, "Ekd5.exe"), externallyChanged);
            TestSupport.Assert(!form.CommitIntegratedForSmokeTest() && File.ReadAllBytes(Path.Combine(copy, "Ekd5.exe")).SequenceEqual(externallyChanged),
                "外部修改没有拒绝保存或被覆盖。");
            File.WriteAllBytes(Path.Combine(copy, "Ekd5.exe"), originalBytes);
            TestSupport.Assert(form.CommitIntegratedForSmokeTest(), "主程序统一保存失败。");
            TestSupport.Assert(!workspace.IsDirty && !draft.HasPendingChanges, "保存后残留草稿。");
            TestSupport.Assert(new EngineConstantService().Read(workspace).Single(value => value.Definition.Id == constant.Definition.Id).NumericValue == constant.NumericValue - 1,
                "同次保存未提交常量。");
            TestSupport.Assert(draft.Slots.Single(slot => slot.Id == selected.Id).Stages.SequenceEqual(new[] { new RuleStage(9, 23), new RuleStage(5, 17) }) && draft.TierEntries[0].Label == "AB",
                "保存重读质变或档位不一致。");
            var afterHash = HashService.Sha256(Path.Combine(copy, "Ekd5.exe"));
            TestSupport.RestoreSavedHistory(form, redo: false);
            TestSupport.Assert(HashService.Sha256(Path.Combine(copy, "Ekd5.exe")) == sourceHash && draft.TierEntries[0].Label == originalLabel,
                "统一保存历史撤回没有恢复增长与常量。");
            TestSupport.RestoreSavedHistory(form, redo: true);
            TestSupport.Assert(HashService.Sha256(Path.Combine(copy, "Ekd5.exe")) == afterHash && draft.TierEntries[0].Label == "AB", "历史前进没有刷新增长模型。");
            foreach (var factor in new[] { 1f, 1.25f, 1.5f })
            {
                form.Font = new Font("Microsoft YaHei", 9 * factor);
                foreach (var size in new[] { new Size(1100, 650), new Size(1280, 720), new Size(1920, 1080) })
                {
                    form.Size = size;
                    form.SelectModuleForSmokeTest("__engine"); form.EngineSubTabsForSmokeTest.SelectedIndex = 3; Pump();
                    AssertButtons(form.TransformForSmokeTest);
                    Capture(form, output, $"质变-{size.Width}x{size.Height}-字号{factor * 100:0}.png");
                    form.SelectModuleForSmokeTest("兵种"); Pump();
                    Capture(form, output, $"档位-{size.Width}x{size.Height}-字号{factor * 100:0}.png");
                    AssertButtons(form.BusinessEditorForSmokeTest.TierEditorForSmokeTest!);
                    var tierGrid = form.BusinessEditorForSmokeTest.TierEditorForSmokeTest!.GridForSmokeTest;
                    TestSupport.Assert(tierGrid.ClientSize.Height >= tierGrid.ColumnHeadersHeight + tierGrid.RowTemplate.Height,
                        $"档位表高度不足以显示表头及一行：{size}/{factor}, {tierGrid.ClientSize.Height}/{tierGrid.ColumnHeadersHeight}+{tierGrid.RowTemplate.Height}");
                    var pair = Descendants(form.BusinessEditorForSmokeTest).OfType<TableLayoutPanel>().Single(control => control.Name == "JobLearningAndTiers");
                    TestSupport.Assert(pair.GetRowHeights().Max() - pair.GetRowHeights().Min() <= 1, "兵种右侧没有上下等分。");
                }
            }
            Console.WriteLine($"GROWTH_INTEGRATION_UI_OK {Path.GetFileName(source)} rules={draft.Slots.Count} tiers={draft.TierEntries.Count}");
        }
        finally
        {
            TestSupport.Assert(HashService.Sha256(Path.Combine(source, "Ekd5.exe")) == sourceHash, "集成验收改动了原基底。");
            try { Directory.Delete(copy, true); } catch (IOException) { }
        }
    }
    private sealed class FailAfterAccept : IWorkspaceDraftParticipant
    {
        public bool HasPendingChanges => true;
        public void AcceptSaved() => throw new IOException("注入统一保存完成阶段故障。");
        public void Discard() { }
    }
    private static TierEntry Clone(TierEntry row) => new() { Ordinal = row.Ordinal, Threshold = row.Threshold, Label = row.Label, OriginalReturn = row.OriginalReturn };
    private static void Pump() { for (var i = 0; i < 12; i++) { TestSupport.PumpUi(); Thread.Sleep(10); } }
    private static IEnumerable<Control> Descendants(Control control) { foreach (Control child in control.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void AssertButtons(Control panel)
    {
        foreach (var button in Descendants(panel).OfType<Button>())
        {
            var bounds = panel.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            TestSupport.Assert(button.Visible && panel.ClientRectangle.Contains(bounds) && button.ClientSize.Width >= button.PreferredSize.Width - 2,
                $"增长面板按钮被裁切：{button.Text}, {bounds}/{panel.ClientRectangle}");
            for (Control? parent = button.Parent; parent != null; parent = parent.Parent)
            {
                var visible = parent.RectangleToScreen(parent.ClientRectangle);
                TestSupport.Assert(visible.Contains(button.RectangleToScreen(button.ClientRectangle)),
                    $"增长面板按钮超出视口：{button.Text}, parent={parent.Name}/{parent.GetType().Name}, button={button.RectangleToScreen(button.ClientRectangle)}, viewport={visible}");
            }
        }
    }
    private static void Capture(Form form, string output, string name)
    {
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new(Point.Empty, image.Size));
        image.Save(Path.Combine(output, name), ImageFormat.Png);
        File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(name) + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new { WindowWidth = form.Width, WindowHeight = form.Height,
                ClientWidth = form.ClientSize.Width, ClientHeight = form.ClientSize.Height, form.DeviceDpi, FontPoints = form.Font.SizeInPoints }));
    }
}
