using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Images;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

/// <summary>Runs the real integrated gallery, modal editor and immediate-save transaction.</summary>
internal static class ImageWritebackIntegrationTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(string repository)
    {
        var source = Path.Combine(repository, "基底", "加强版6.5未加密版");
        var originalHashes = new[] { "Data.e5", "Star.e5", "Imsg.e5", "Ekd5.exe" }
            .ToDictionary(name => name, name => HashService.Sha256(Path.Combine(source, name)));
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            var colors = Enumerable.Range(0, 256).Select(i => Color.FromArgb(i, i, i)).ToArray();
            colors[0] = Color.Magenta; colors[1] = Color.Black; colors[2] = Color.White;
            var palette = colors.SelectMany(color => new[] { color.B, color.R, color.G }).ToArray();
            File.WriteAllBytes(Path.Combine(root, "Spalet.e5"), Archive([palette]));
            File.WriteAllBytes(Path.Combine(root, "Pmapobj.e5"), Archive([
                Enumerable.Repeat((byte)1, 48 * 64 * 20).ToArray(),
                Enumerable.Repeat((byte)2, 48 * 64 * 20).ToArray()]));
            foreach (var spec in new[] { ("mov", 48, 11), ("atk", 64, 12), ("spc", 48, 5) })
                File.WriteAllBytes(Path.Combine(root, "Unit_" + spec.Item1 + ".e5"), Archive(
                    Enumerable.Range(1, 242).Select(_ => Enumerable.Repeat((byte)1,
                        spec.Item2 * spec.Item2 * spec.Item3).ToArray()).ToArray()));

            using var form = new MainForm(openDefaultProject: false)
            {
                StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000),
                ShowInTaskbar = false, Size = new(1250, 900)
            };
            form.Show();
            form.LoadProjectForSmokeTest(root);
            form.SelectModuleForSmokeTest("__resources");
            var panel = Descendants(form).OfType<IntegratedResourcePanel>().Single();
            panel.ShowCategory("R"); Wait(panel);
            var page = CurrentPage(panel);
            Select(page, 0);
            var rPath = Path.Combine(root, "Pmapobj.e5");
            var rBefore = File.ReadAllBytes(rPath);
            var events = 0;
            page.ResourcesChanged += _ => events++;
            Edit(page, editor => Erase(editor, "Pmapobj", new(5, 5)));
            Wait(panel);
            TestSupport.Assert(events == 1, "R像素真实提交没有通知资源缓存刷新。");
            var expectedR = E5Archive.Parse(rPath, rBefore).ReadDecoded(1);
            expectedR[5 * 48 + 5] = 0;
            AssertEntry(rPath, 1, expectedR, "R黑色橡皮擦没有写为透明索引。");
            TestSupport.Assert(E5Archive.OpenFresh(rPath).ReadStored(2).SequenceEqual(
                E5Archive.Parse(rPath, rBefore).ReadStored(2)), "R正面编辑覆盖了背面。");
            TestSupport.Assert(Status(page).Contains("保存", StringComparison.Ordinal), "R真实保存没有显示成功。");

            var rAfter = File.ReadAllBytes(rPath);
            var history = new ResourceHistoryAdapter();
            var entries = history.List(root);
            var operation = entries.Single(entry => entry.CanUndo).OperationId;
            Edit(page, _ => { });
            Wait(panel);
            TestSupport.Assert(File.ReadAllBytes(rPath).SequenceEqual(rAfter) && events == 1 &&
                history.List(root).Count == entries.Count, "无变化像素确认改写了文件、刷新缓存或产生成功历史。");
            TestSupport.Assert(Status(page).Contains("未产生", StringComparison.Ordinal), "无变化确认没有给出明确提示。");

            panel.ShowCategory("S"); Wait(panel);
            page = CurrentPage(panel); Select(page, 242);
            var atkPath = Path.Combine(root, "Unit_atk.e5");
            var atkBefore = File.ReadAllBytes(atkPath);
            var movBefore = File.ReadAllBytes(Path.Combine(root, "Unit_mov.e5"));
            var spcBefore = File.ReadAllBytes(Path.Combine(root, "Unit_spc.e5"));
            Edit(page, editor => Erase(editor, "Unit_atk", new(7, 64 * 6 + 9)));
            Wait(panel);
            var expectedAtk = E5Archive.Parse(atkPath, atkBefore).ReadDecoded(242);
            expectedAtk[(64 * 6 + 9) * 64 + 7] = 0;
            AssertEntry(atkPath, 242, expectedAtk, "S物理242攻击中间帧擦除没有真实写回。");
            var beforeArchive = E5Archive.Parse(atkPath, atkBefore);
            var afterArchive = E5Archive.OpenFresh(atkPath);
            TestSupport.Assert(Enumerable.Range(1, 241).All(id => beforeArchive.ReadStored(id)
                .SequenceEqual(afterArchive.ReadStored(id))), "S修改写到了其他人物或兵种物理槽。");
            // Decoded bytes are the invariant for a no-op companion strip; compression may differ.
            foreach (var (name, before) in new[] { ("Unit_mov.e5", movBefore), ("Unit_spc.e5", spcBefore) })
            {
                var path = Path.Combine(root, name);
                TestSupport.Assert(E5Archive.OpenFresh(path).ReadDecoded(242).SequenceEqual(
                    E5Archive.Parse(path, before).ReadDecoded(242)), "S攻击编辑改变了其他动作帧。");
            }

            panel.ShowCategory("R"); Wait(panel);
            AssertEntry(rPath, 1, expectedR, "切换图库后R修改丢失。");
            panel.ClearProject(); panel.LoadProject(root, "R"); Wait(panel);
            AssertEntry(rPath, 1, expectedR, "重开项目后R修改丢失。");
            AssertEntry(atkPath, 242, expectedAtk, "重开项目后S修改丢失。");

            // Restore the latest S operation before the original R operation.
            var sOperation = history.List(root).Last(entry => entry.CanUndo && entry.Targets.Any(path =>
                Path.GetFileName(path).Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase))).OperationId;
            TestSupport.Assert(history.Undo(root, sOperation).Success && File.ReadAllBytes(atkPath).SequenceEqual(atkBefore),
                "S真实界面编辑撤回未恢复物理文件。");
            TestSupport.Assert(history.Redo(root, sOperation).Success, "S真实界面编辑重做失败。");
            AssertEntry(atkPath, 242, expectedAtk, "S真实界面编辑重做未恢复像素。");
            TestSupport.Assert(history.Undo(root, operation).Success && File.ReadAllBytes(rPath).SequenceEqual(rBefore),
                "R真实界面编辑撤回未恢复物理文件。");
            TestSupport.Assert(history.Redo(root, operation).Success, "R真实界面编辑重做失败。");
            AssertEntry(rPath, 1, expectedR, "R真实界面编辑重做未恢复像素。");
            form.Close();
            Console.WriteLine("IMAGE_WRITEBACK_INTEGRATION_OK: integrated R/S modal eraser → immediate save → fresh disk decode; no-change/history; switch/reopen; undo/redo.");
        }
        finally
        {
            foreach (var pair in originalHashes)
                TestSupport.Assert(HashService.Sha256(Path.Combine(source, pair.Key)) == pair.Value, "集成测试修改了原始基底：" + pair.Key);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static void Select(ImageGridPage page, int id)
    {
        var assets = (IEnumerable<LogicalImageAsset>)page.GetType().GetField("_assets", Members)!.GetValue(page)!;
        var selected = assets.Single(asset => asset.LogicalId == id);
        Descendants(page).OfType<VirtualImageGallery>().Single().SelectItem(selected.StablePhysicalId);
        Invoke(page, "ActivatePreview", selected);
    }

    private static void Erase(PixelEditorDialog editor, string sourceName, Point local)
    {
        var canvas = Descendants(editor).Single(control => control.Name == "PixelCanvas");
        var sections = (IReadOnlyList<PixelEditorSection>)canvas.GetType().GetField("_sections", Members)!.GetValue(canvas)!;
        var section = sections.First(item => Path.GetFileName(item.SourceFile).Contains(sourceName, StringComparison.OrdinalIgnoreCase));
        canvas.GetType().GetProperty("Zoom", Members)!.SetValue(canvas, 1d);
        var toolType = canvas.GetType().Assembly.GetType("ImageManager.App.PixelTool")!;
        canvas.GetType().GetProperty("Tool", Members)!.SetValue(canvas, Enum.Parse(toolType, "Eraser"));
        var point = new Point(section.Bounds.X + local.X, section.Bounds.Y + local.Y);
        Invoke(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
        Invoke(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
    }

    private static void Edit(ImageGridPage page, Action<PixelEditorDialog> action)
    {
        Exception? callbackError = null;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            var editor = Application.OpenForms.OfType<PixelEditorDialog>().FirstOrDefault();
            if (editor == null)
            {
                if (elapsed.Elapsed < TimeSpan.FromSeconds(20)) return;
                timer.Stop();
                callbackError = new TimeoutException("集成入口没有打开像素编辑窗口。");
                foreach (var dialog in Application.OpenForms.Cast<Form>().Where(form => form.Modal).ToArray()) dialog.Close();
                return;
            }
            timer.Stop();
            try
            {
                action(editor);
                Descendants(editor).OfType<Button>().Single(button => button.Text.StartsWith("写回当前", StringComparison.Ordinal)).PerformClick();
            }
            catch (Exception error) { callbackError = error; editor.DialogResult = DialogResult.Cancel; }
        };
        timer.Start();
        Invoke(page, "PixelEdit");
        if (callbackError != null) ExceptionDispatchInfo.Capture(callbackError).Throw();
    }

    private static string Status(ImageGridPage page) => ((Label)page.GetType().GetField("_status", Members)!.GetValue(page)!).Text;
    private static ImageGridPage CurrentPage(IntegratedResourcePanel panel) => Descendants(panel).OfType<ImageGridPage>().Single(page => page.Visible);
    private static void AssertEntry(string path, int id, byte[] expected, string message)
        => TestSupport.Assert(E5Archive.OpenFresh(path).ReadDecoded(id).SequenceEqual(expected), message);
    private static void Wait(IntegratedResourcePanel panel)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("集成资源加载超时。");
            Application.DoEvents(); Thread.Sleep(5);
        } while (!panel.LoadingTask.IsCompleted);
        panel.LoadingTask.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static object? Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method, Members)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static byte[] Archive(IReadOnlyList<byte[]> payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Count * 12;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = bytes.AsSpan(E5Archive.IndexOffset + index * 12, 12);
            BinaryPrimitives.WriteInt32BigEndian(entry, payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }
}
