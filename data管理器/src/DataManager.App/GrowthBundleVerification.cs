using System.Drawing.Imaging;

namespace DataManager.App;

/// <summary>Exercises the integrated views inside the published executable, without saving a project.</summary>
internal static class GrowthBundleVerification
{
    internal static object Verify(string root, string reportPath)
    {
        var previousContext = SynchronizationContext.Current;
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false
        };
        try
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            form.Show(); form.LoadProjectForSmokeTest(root);
            form.SelectModuleForSmokeTest("__engine");
            form.EngineSubTabsForSmokeTest.SelectedIndex = 3; Pump();
            if (!form.EngineSubTabsForSmokeTest.TabPages.Cast<TabPage>().Select(page => page.Text)
                .SequenceEqual(new[] { "特效管理", "宝物特效", "兵种特效", "质变设定", "常量" }))
                throw new InvalidDataException("发布程序的引擎子页顺序错误。");
            var draft = form.GrowthForSmokeTest;
            if (!draft.CanEditRules || !draft.CanEditTiers || draft.Slots.Count == 0)
                throw new InvalidDataException("发布程序的成长设定不可编辑：" + draft.RulesReadOnlyReason + draft.TiersReadOnlyReason);
            var screenshots = new List<object>();
            foreach (var size in new[] { new Size(1100, 650), new Size(1280, 720), new Size(1920, 1080) })
            {
                form.Size = size;
                form.SelectModuleForSmokeTest("__engine"); form.EngineSubTabsForSmokeTest.SelectedIndex = 3; Pump();
                var transform = form.TransformForSmokeTest;
                if (!transform.Visible || transform.RulesForSmokeTest.Columns.Count != 2 || form.EntityListForSmokeTest.Items.Count != draft.Slots.Count)
                    throw new InvalidDataException("发布程序的质变规则和引用列表没有绑定。");
                Capture("质变", transform, size);
                form.SelectModuleForSmokeTest("兵种"); Pump();
                var tier = form.BusinessEditorForSmokeTest.TierEditorForSmokeTest;
                if (tier == null || !tier.Visible || tier.GridForSmokeTest.Columns.Count != 3 || tier.GridForSmokeTest.Rows.Count != draft.TierEntries.Count)
                    throw new InvalidDataException("发布程序的全局三列档位没有绑定。");
                Capture("档位", tier, size);
            }
            form.SelectModuleForSmokeTest("__engine"); form.EngineSubTabsForSmokeTest.SelectedIndex = 4; Pump();
            if (!form.ConstantsForSmokeTest.Visible || form.ConstantsForSmokeTest.GridForSmokeTest.Rows.Count != Core.Scanner.EngineConstantService.Definitions.Count + 5)
                throw new InvalidDataException("发布程序的常量及专用设定条目不完整。");
            if (form.WorkspaceForSmokeTest.IsDirty) throw new InvalidDataException("发布程序的只读整合验收产生了草稿。");
            return new { BuildVersion = Application.ProductVersion, form.DeviceDpi, Sizes = screenshots,
                Effects = draft.Slots.Count, Tiers = draft.TierEntries.Count, Constants = Core.Scanner.EngineConstantService.Definitions.Count,
                SourceUnchanged = true };

            void Capture(string name, Control panel, Size size)
            {
                foreach (var button in Descendants(panel).OfType<Button>())
                    for (Control? parent = button.Parent; parent != null; parent = parent.Parent)
                        if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)))
                            throw new InvalidDataException($"发布程序按钮超出视口：{name}/{button.Text}/{size}");
                var output = Path.Combine(Path.GetDirectoryName(reportPath)!, Path.GetFileNameWithoutExtension(reportPath) + $"-{name}-{size.Width}x{size.Height}.png");
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(output, ImageFormat.Png);
                screenshots.Add(new { Page = name, Path = output, Width = bitmap.Width, Height = bitmap.Height });
            }
        }
        finally { form.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Pump()
    {
        for (var i = 0; i < 12; i++)
        {
            if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.DoEvents(); Thread.Sleep(10);
        }
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }
}
