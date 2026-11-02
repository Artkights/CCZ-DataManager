using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Editing;
using DataManager.Core.Domain;
using DataManager.Images;

namespace DataManager.Tests;

internal static class DraftPreviewTests
{
    public static void CheckRangePicker()
    {
        using var form = new Form { Size = new Size(500, 180), Location = new Point(-32000, -32000),
            StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
        using var picker = new RangePreviewPicker(new SearchableIdComboBox()) { Width = 300, Height = 44 };
        form.Controls.Add(picker); form.Show();
        var calls = 0;
        picker.SetPreviewLoader((id, _) =>
        {
            calls++;
            if (id == 255) return Task.FromResult<Bitmap?>(null);
            var image = new Bitmap(8, 8);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(id == 0 ? Color.Red : Color.Blue);
            return Task.FromResult<Bitmap?>(image);
        });
        picker.Combo.Text = "0";
        WaitFor(() => Image()?.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "范围初始预览未加载");
        picker.Combo.Focus(); picker.Combo.Text = "1";
        WaitFor(() => Image()?.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(), "范围键入后仍需失焦才能预览");
        TestSupport.Assert(picker.IsPreviewVisible && !picker.PreviewBox.Bounds.IntersectsWith(picker.Combo.Bounds),
            "范围预览未常显于输入框旁或遮住了输入文字");
        var last = Image();
        foreach (var invalid in new[] { "", "-1", "999999", "搜索" })
        {
            picker.Combo.Text = invalid; Application.DoEvents();
            TestSupport.Assert(ReferenceEquals(last, Image()) && picker.IsPreviewVisible, "范围无效中间输入清除了最近有效预览");
        }
        picker.Combo.Text = "1"; picker.RefreshPreview();
        TestSupport.Assert(calls == 2, "范围同一编号重复解码");
        picker.Combo.Text = "255";
        WaitFor(() => Image() == null && Field<string?>(picker.PreviewBox, "_status") == "无对应图片", "缺少范围图片时没有明确状态");
        var stale = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker.Combo.Text = "2";
        picker.SetPreviewLoader((id, _) => id == 2 ? stale.Task : current.Task);
        picker.Combo.Text = "3";
        var currentImage = new Bitmap(8, 8);
        current.SetResult(currentImage);
        WaitFor(() => ReferenceEquals(currentImage, Image()), "范围当前请求未显示");
        var staleImage = new Bitmap(8, 8);
        stale.SetResult(staleImage);
        WaitFor(() => IsDisposed(staleImage), "范围迟到旧请求没有释放位图");
        TestSupport.Assert(ReferenceEquals(currentImage, Image()), "范围迟到旧请求覆盖了新图");
        Console.WriteLine("LIVE_RANGE_DRAFT_PREVIEW_OK");
        Bitmap? Image() => Field<Bitmap?>(picker.PreviewBox, "_image");
    }

    public static void CheckProject(BusinessEditorPanel panel, ProjectWorkspace workspace, ResourceCatalogAdapter adapter, int strategyId)
    {
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo, "通用草稿预览测试存在初始修改");
        var tested = new List<string>();
        foreach (var target in new[]
        {
            (BusinessPageKind.Items, 0, "", "图标", "图标", "宝物图标 "),
            (BusinessPageKind.Strategies, strategyId, "strategies", "策略图标", "策略图标", "策略图标 "),
            (BusinessPageKind.Strategies, strategyId, "strategy-small-animation", "动画ID", "小动画", "小动画 "),
            (BusinessPageKind.Strategies, strategyId, "strategy-big-animation", "动画ID", "大动画", "大动画 ")
        })
        {
            var (kind, entityId, tableKey, fieldName, label, prefix) = target;
            panel.Bind(kind, entityId);
            if (kind == BusinessPageKind.Items)
                tableKey = new[] { "items-low", "items-high", "items-extended" }.First(key =>
                    workspace.Documents.TryGetValue(key, out var data) && entityId >= data.Definition.BeginId &&
                    entityId < data.Definition.BeginId + data.Data.Rows.Count);
            var document = workspace.GetDocument(tableKey);
            var row = document.Data.Rows[entityId - document.Definition.BeginId];
            var original = Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture);
            var editor = LabeledEditor(panel, label);
            if (!editor.Enabled || editor is TextBox { ReadOnly: true }) continue;
            // NumericUpDown.Text's programmatic setter validates immediately;
            // native typing changes its child text box until Enter/validation.
            var input = editor is NumericUpDown ? Descendants(editor).OfType<TextBox>().Single() : editor;
            var next = kind == BusinessPageKind.Items
                ? adapter.GetIdOptions(workspace.RootPath, ResourceBindingKind.ItemIcon).First(option => option.Id != original).Id
                : fieldName == "策略图标" ? (original == 0 ? 1 : 0) : 255;
            if (next == original) next = 0;
            var card = Descendants(panel).OfType<ResourcePreviewCard>().Single(item => item.Text.StartsWith(prefix, StringComparison.Ordinal));
            WaitFor(() => card.HasLoadedResource, label + "初始预览未加载");
            var originalText = editor.Text;
            var cards = Descendants(panel).OfType<ResourcePreviewCard>().ToArray();
            input.Text = next.ToString(CultureInfo.InvariantCulture);
            WaitFor(() => card.Text == prefix + next && card.HasLoadedResource, label + "键入后预览没有即时刷新");
            TestSupport.Assert(Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture) == original &&
                !workspace.IsDirty && !workspace.Commands.CanUndo && panel.HasPendingChanges,
                label + $"仅预览就提前提交了草稿，或没有识别待提交输入：row={row[fieldName]}, original={original}, dirty={workspace.IsDirty}, undo={workspace.Commands.CanUndo}, pending={panel.HasPendingChanges}");
            TestSupport.Assert(cards.SequenceEqual(Descendants(panel).OfType<ResourcePreviewCard>()), label + "输入重建了预览控件");
            var picture = Descendants(card).OfType<PictureBox>().Single();
            var validImage = picture.Image;
            foreach (var invalid in new[] { "", "-1", "2147483647", "搜索" })
            {
                input.Text = invalid; Application.DoEvents();
                TestSupport.Assert(ReferenceEquals(validImage, picture.Image) && card.Text == prefix + next && !workspace.IsDirty,
                    label + "无效输入没有保留最近有效预览");
                if (editor is NumericUpDown)
                {
                    var prepared = panel.PreparePendingEdits();
                    TestSupport.Assert(!prepared && input.Text == invalid &&
                        Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture) == original &&
                        !workspace.IsDirty && !workspace.Commands.CanUndo && panel.HasPendingChanges,
                        label + $"无效资源编号没有阻止查询/保存，或验证静默更正了输入：{invalid}; prepared={prepared}; input={input.Text}; row={row[fieldName]}; original={original}; dirty={workspace.IsDirty}; undo={workspace.Commands.CanUndo}; pending={panel.HasPendingChanges}");
                    var form = panel.FindForm()!;
                    using var queryInput = new TextBox { Width = 180 };
                    using var queryButton = new Button { Text = "查询", CausesValidation = false };
                    form.Controls.Add(queryInput); form.Controls.Add(queryButton);
                    input.Focus(); input.Text = invalid;
                    queryInput.Focus(); Application.DoEvents();
                    TestSupport.Assert(input.Text == invalid && !workspace.IsDirty &&
                        Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture) == original,
                        label + $"转到查询输入框时原生验证静默更正了无效编号：{invalid}; input={input.Text}; row={row[fieldName]}; dirty={workspace.IsDirty}");
                    input.Focus(); input.Text = invalid;
                    queryButton.Focus(); Application.DoEvents();
                    TestSupport.Assert(input.Text == invalid && !panel.PreparePendingEdits() &&
                        !workspace.IsDirty && !workspace.Commands.CanUndo &&
                        Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture) == original,
                        label + "点击无需验证的查询按钮时静默更正/提交了无效编号：" + invalid);
                }
            }
            input.Text = originalText;
            TestSupport.Assert(panel.Flush() && !workspace.IsDirty && !workspace.Commands.CanUndo,
                label + "恢复原输入仍产生草稿");
            tested.Add(label);
        }
        CheckDefaultSJob(panel, workspace);
        Console.WriteLine($"ALL_FIELD_DRAFT_PREVIEWS_OK {workspace.ActiveProfile.VariantId}: {string.Join('/', tested)}");
    }

    private static void CheckDefaultSJob(BusinessEditorPanel panel, ProjectWorkspace workspace)
    {
        panel.Bind(BusinessPageKind.Persons, 0);
        if (!Descendants(panel).OfType<Label>().Any(item => item.Text == "S形象编号")) return;
        var sEditor = LabeledEditor(panel, "S形象编号");
        if (!sEditor.Enabled) return;
        var jobEditor = LabeledEditor(panel, "兵种");
        var originalS = sEditor.Text; var originalJob = jobEditor.Text;
        var jobs = (SearchableIdComboBox)jobEditor;
        jobs.TryGetSelectedId(out var job);
        var nextJob = jobs.Options.First(option => option.Id != job).Id;
        sEditor.Text = "0";
        jobEditor.Text = nextJob.ToString(CultureInfo.InvariantCulture);
        var card = Descendants(panel).OfType<ResourcePreviewCard>().Single(item => item.Text.StartsWith("S ", StringComparison.Ordinal));
        WaitFor(() => card.HasLoadedResource && Field<string?>(card, "_key")!.Contains($"|S:0:{nextJob}:mov:0", StringComparison.Ordinal),
            "默认S没有使用尚未提交的兵种输入");
        var key = Field<string?>(card, "_key");
        jobEditor.Text = "搜索";
        var face = LabeledEditor(panel, "头像");
        var faceText = face.Text;
        if (face is SearchableIdComboBox portrait && portrait.Enabled)
        {
            portrait.TryGetSelectedId(out var faceId);
            portrait.Text = portrait.Options.First(option => option.Id != faceId).Id.ToString(CultureInfo.InvariantCulture);
            TestSupport.Assert(Field<string?>(card, "_key") == key, "其他图片字段刷新时丢失了兵种最近有效输入");
            portrait.Text = faceText;
        }
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo, "默认S预览产生了草稿写入");
        sEditor.Text = originalS; jobEditor.Text = originalJob;
        TestSupport.Assert(panel.Flush() && !workspace.IsDirty, "默认S测试恢复输入失败");
    }

    private static Control LabeledEditor(Control panel, string label)
    {
        var matched = Descendants(panel).OfType<Label>().Single(item => item.Text == label);
        var layout = (TableLayoutPanel)matched.Parent!;
        var position = layout.GetPositionFromControl(matched);
        var adjacent = layout.GetControlFromPosition(position.Column + 1, position.Row);
        return adjacent == null || adjacent is Label ? layout.GetControlFromPosition(position.Column, position.Row + 1)! : adjacent;
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static bool IsDisposed(Bitmap image)
    {
        try { _ = image.GetPixel(0, 0); return false; }
        catch (ArgumentException) { return true; }
    }

    private static void WaitFor(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(5); }
        TestSupport.Assert(condition(), message);
    }
}
