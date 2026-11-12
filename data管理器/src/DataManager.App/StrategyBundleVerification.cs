using System.Diagnostics;
using System.Drawing.Imaging;
using DataManager.Core.Domain;

namespace DataManager.App;

/// <summary>Checks the strategy UI hosted by the actual published executable without editing the project.</summary>
internal static class StrategyBundleVerification
{
    internal static object Verify(string root, string reportPath)
    {
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            Size = new Size(1280, 800), ShowInTaskbar = false
        };
        form.Show();
        form.LoadProjectForSmokeTest(root);
        form.SelectModuleForSmokeTest("策略");
        Pump();
        var workspace = form.WorkspaceForSmokeTest;
        var document = workspace.GetDocument(StrategyEffectClassService.TableKey);
        if (!document.Definition.IsWriteSupported || document.Data.Rows.Count != 144)
            throw new InvalidDataException("发布包未识别已验证的144行策略效果类表。");
        var page = form.BusinessEditorForSmokeTest.DetailTabs.SelectedTab!;
        var labels = Descendants(page).OfType<Label>().ToArray();
        if (!labels.Any(label => label.Text == "类型索引") || !labels.Any(label => label.Text == "效果索引"))
            throw new InvalidDataException("策略索引标签不符合当前界面约定。");
        var extension = Descendants(page).FirstOrDefault(control => control.Name == "Flags_strategy-extension");
        var extensionFlags = extension == null
            ? Array.Empty<string>()
            : Descendants(extension).OfType<CheckBox>().Select(check => check.Text).ToArray();
        if (workspace.ActiveProfile.ProfileId == "ccz-6.6")
        {
            var checks = extension == null ? Array.Empty<CheckBox>() : Descendants(extension).OfType<CheckBox>().ToArray();
            if (checks.Length != 2 || checks[0].Text != "策略模仿" || checks[1].Text != "辅助穿透" ||
                checks.Any(check => !check.Checked))
                throw new InvalidDataException("6.6 策略扩展位没有按 bit0/bit1 两个可写复选框显示。");
        }
        else if (extension != null)
            throw new InvalidDataException("6.4/6.5 不应显示逐策略扩展位。");
        var effect = Descendants(page).Single(control => control.Name == "StrategyEffectClassField");
        var combo = Descendants(effect).OfType<SearchableIdComboBox>().Single();
        var choices = StrategyEffectClassService.GetOptions(workspace, 0);
        if (!combo.Enabled || !combo.Options.Select(option => option.Display)
            .SequenceEqual(choices.Select(option => $"{option.Id}：{option.Name}")))
            throw new InvalidDataException("效果下拉列表未正确绑定本引擎效果名称。");
        var screenshots = new List<string>();
        void Snapshot(string suffix)
        {
            var path = Path.Combine(Path.GetDirectoryName(reportPath)!,
                Path.GetFileNameWithoutExtension(reportPath) + "-strategy-" + suffix + ".png");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path, ImageFormat.Png);
            screenshots.Add(path);
        }
        Snapshot("cold");
        var scroll = Ancestors(effect).OfType<ScrollableControl>().First(control => control.AutoScroll);
        scroll.ScrollControlIntoView(effect); Pump();
        if (!scroll.RectangleToScreen(scroll.ClientRectangle).IntersectsWith(effect.RectangleToScreen(effect.ClientRectangle)))
            throw new InvalidDataException("效果类无法滚动到可见区域。");
        Snapshot("effects");
        var typeSection = Descendants(page).Single(control => control.Name == "StrategyTypeAndExtensionFields");
        var typeScroll = Ancestors(typeSection).OfType<ScrollableControl>().First(control => control.AutoScroll);
        typeScroll.ScrollControlIntoView(typeSection); Pump();
        Snapshot("extension");
        if (workspace.IsDirty || form.BusinessEditorForSmokeTest.HasPendingChanges)
            throw new InvalidDataException("只读策略验收产生了修改草稿。");
        return new
        {
            Rows = document.Data.Rows.Count, document.Definition.DataOffset,
            Dropdown = combo.Options.Select(option => option.Display).ToArray(),
            ExtensionFlags = extensionFlags,
            ColdDirectNavigation = true, workspace.IsDirty, Screenshots = screenshots
        };
    }

    private static void Pump()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 400) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private static IEnumerable<Control> Ancestors(Control control)
    {
        for (var parent = control.Parent; parent != null; parent = parent.Parent) yield return parent;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
