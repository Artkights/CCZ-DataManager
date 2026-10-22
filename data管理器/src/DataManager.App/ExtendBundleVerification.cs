using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;

namespace DataManager.App;

/// <summary>Exercises the delivered executable against an isolated copy of a real project.</summary>
internal static class ExtendBundleVerification
{
    internal static int Run(string[] args)
    {
        if (args.Length != 3) return 2;
        var reportPath = Path.GetFullPath(args[1]);
        var source = ProjectRootLocator.Resolve(args[2]);
        if (reportPath.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var copy = Path.Combine(Path.GetTempPath(), "DataManager-extend-bundle-" + Guid.NewGuid().ToString("N"));
        var results = new List<object>();
        var sourceFiles = new[] { "Data.e5", "Star.e5", "Imsg.e5", "Ekd5.exe", "Effect.e5", "Effectmsg.e5", "MeffStudy.e5", "Forge.e5", "Item.e5" }
            .Select(name => ProjectFileLocator.Resolve(source, name)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hashes = sourceFiles.ToDictionary(path => path, path => HashService.Sha256(path));
        var report = new Dictionary<string, object?>
        {
            ["Schema"] = "ccz.extend-bundle-verification.v1", ["Timestamp"] = DateTimeOffset.Now,
            ["Executable"] = Environment.ProcessPath, ["BuildVersion"] = Application.ProductVersion,
            ["Runtime"] = RuntimeInformation.FrameworkDescription, ["SourceRoot"] = source,
            ["Checks"] = results,
            ["DpiNote"] = "Screenshots use 1280x720 and 1920x1080 client sizes plus 125%/150% font scaling. They do not simulate a Windows per-monitor DPI transition.",
            ["EngineConsumption"] = "Offline file editing verified; in-game EXS/load behavior is not exercised."
        };
        var logPath = reportPath + ".log";
        File.WriteAllText(logPath, string.Empty);
        void Stage(string stage) => File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {stage}{Environment.NewLine}");
        MainForm? form = null;
        try
        {
            Stage("copy-source");
            Directory.CreateDirectory(copy);
            foreach (var path in sourceFiles)
            {
                var target = Path.Combine(copy, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
            form = new MainForm(openDefaultProject: false)
            {
                Location = new Point(-30000, -30000), StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false, MaximumSize = new Size(4096, 2160), ClientSize = new Size(1280, 720)
            };
            form.Show(); form.LoadProjectForSmokeTest(copy); form.SelectModuleForSmokeTest("拓展数据");
            Application.DoEvents();
            Stage("loaded");
            var editor = form.BusinessEditorForSmokeTest.ExtendEditor ?? throw new InvalidDataException("专用拓展面板没有加载。");
            var workspace = form.WorkspaceForSmokeTest;
            var service = new ExtendDataService(workspace);
            Require(!workspace.IsDirty && !editor.HasPendingChanges, "打开项目意外产生了草稿。");
            var categories = Enum.GetValues<ExtendDataCategory>();
            Require(categories.All(category => service.GetStatus(category).Available), "交付验收样本必须具有四个拓展文件。");
            results.Add(new { Name = "读取真实容量及名称", Success = true, Categories = categories.Select(service.GetStatus).ToArray(), NameEvidence = service.GetEffectNameSource() });

            var expected = new Dictionary<string, byte[]>();
            var targets = new[]
            {
                (Category: ExtendDataCategory.Effects, Control: "ExtendEffectValue0", Field: "槽0特效值", Offset: 1),
                (Category: ExtendDataCategory.Growth, Control: "ExtendGrowth1", Field: "Param1", Offset: 1),
                (Category: ExtendDataCategory.Strategies, Control: "ExtendStrategyCount0", Field: "策略0次数", Offset: 1),
                (Category: ExtendDataCategory.Forge, Control: "ExtendForgePrice", Field: "价格", Offset: 54)
            };
            foreach (var target in targets)
            {
                editor.DetailTabs.SelectedIndex = (int)target.Category; editor.BindEntity(0); Application.DoEvents();
                Require(form.EntityListForSmokeTest.Items.Count == service.GetStatus(target.Category).RowCount, "左侧实体没有独立绑定分类。");
                var document = service.GetDocument(target.Category);
                expected[document.FilePath] = document.OriginalFileBytes.ToArray();
                expected[document.FilePath][target.Offset] ^= 1;
                var input = Descendants(editor).OfType<TextBox>().Single(control => control.Name == target.Control);
                input.Text = (service.ReadLong(target.Category, 0, target.Field) ^ 1L).ToString(CultureInfo.InvariantCulture);
                Require(editor.HasPendingChanges, "未失焦的输入未进入待提交状态。");
                Require(editor.Flush(), "专用面板不能提交合法输入。");
                Stage("edited-" + target.Category);
            }
            var save = form.SavePendingForSmokeTest();
            Require(save.Saved && !workspace.IsDirty, "顶部统一保存未成功：" + string.Join("；", save.Messages));
            foreach (var pair in expected) Require(File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value), "单字段修改触碰了额外字节：" + pair.Key);
            Stage("saved");
            var reopened = new ProjectWorkspace(); reopened.Open(copy);
            Require(!reopened.IsDirty, "保存后重开产生了草稿。");
            Require(workspace.UndoSaved().Success, "已保存历史撤回失败。");
            foreach (var path in expected.Keys) Require(HashService.Sha256(path) == hashes[sourceFiles.Single(original => Path.GetFileName(original).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))], "历史撤回未还原文件。");
            Require(workspace.RedoSaved().Success, "已保存历史重做失败。");
            foreach (var pair in expected) Require(File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value), "历史重做未恢复草稿结果。");
            Stage("history-restored");
            results.Add(new { Name = "面板编辑、统一保存、重开、历史撤回和重做", Success = true, ChangedFiles = expected.Count, UnrelatedBytesUnchanged = true });

            var pictures = new List<object>();
            var defaultFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            foreach (var view in new[] { (1280, 720, 1f), (1920, 1080, 1f), (1280, 720, 1.25f), (1280, 720, 1.5f) })
            {
                form.Font = new Font(defaultFont.FontFamily, defaultFont.Size * view.Item3);
                form.ClientSize = new Size(view.Item1, view.Item2);
                Require(form.ClientSize == new Size(view.Item1, view.Item2), "系统限制了验收窗口大小，不能将裁切窗口记为1920布局通过。");
                for (var index = 0; index < 4; index++)
                {
                    editor.DetailTabs.SelectedIndex = index; editor.BindEntity(0);
                    form.PerformLayout(); Application.DoEvents();
                    foreach (var box in Descendants(form).OfType<TextBoxBase>()) { box.SelectionStart = box.TextLength; box.SelectionLength = 0; }
                    foreach (var combo in Descendants(form).OfType<ComboBox>()) { combo.DroppedDown = false; combo.SelectionStart = combo.Text.Length; combo.SelectionLength = 0; }
                    Application.DoEvents();
                    var scroll = Descendants(editor.DetailTabs.SelectedTab!).OfType<Panel>().Single(control => control.Name == "ExtendDataScroll");
                    Require(scroll.AutoScroll && scroll.ClientSize.Width > 100 && scroll.ClientSize.Height > 100, "编辑区域缺少滚动空间。");
                    var picture = Path.Combine(Path.GetDirectoryName(reportPath)!, $"发布验收-{categories[index]}-{view.Item1}x{view.Item2}-{view.Item3.ToString("0.00", CultureInfo.InvariantCulture)}.png");
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(picture, ImageFormat.Png);
                    pictures.Add(new { Path = picture, Width = form.ClientSize.Width, Height = form.ClientSize.Height, FontScale = view.Item3 });
                }
            }
            results.Add(new { Name = "交付EXE四分类布局", Success = true, Screenshots = pictures });
            Require(hashes.All(pair => File.Exists(pair.Key) && HashService.Sha256(pair.Key) == pair.Value), "验收修改了原始基底。");
            results.Add(new { Name = "原始基底SHA256不变", Success = true, Files = hashes });
            report["Success"] = true;
            Stage("passed");
        }
        catch (Exception error) { report["Success"] = false; report["Error"] = error.ToString(); Stage("failed " + error.Message); }
        finally
        {
            if (form != null)
            {
                form.BusinessEditorForSmokeTest.DiscardPendingChanges();
                while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
                form.Close(); form.Dispose();
            }
            try { if (Directory.Exists(copy)) Directory.Delete(copy, true); }
            catch (Exception error) { report["CleanupError"] = error.Message; }
            report["RuntimeModules"] = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(module => module.ModuleName.Contains("coreclr", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Contains("hostpolicy", StringComparison.OrdinalIgnoreCase))
                .Select(module => new { module.ModuleName, module.FileName }).ToArray();
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        return report.GetValueOrDefault("Success") is true ? 0 : 1;
    }

    private static void Require(bool condition, string error) { if (!condition) throw new InvalidDataException(error); }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}
