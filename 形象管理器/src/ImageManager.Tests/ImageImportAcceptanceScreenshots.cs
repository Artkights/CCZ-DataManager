using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ImageManager.App;
using ImageManager.Core;

namespace ImageManager.Tests;

/// <summary>Real-project import previews only: never confirms or commits a mutation.</summary>
internal static class ImageImportAcceptanceScreenshots
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        var repository = FindRepository();
        var requestedOutput = Environment.GetEnvironmentVariable("IMAGE_IMPORT_UI_SNAPSHOTS");
        var output = Path.GetFullPath(string.IsNullOrWhiteSpace(requestedOutput)
            ? Path.Combine(repository, "data管理器", "验证", "图片导入与调色板修正-20261023", "真实导入窗口") : requestedOutput);
        Directory.CreateDirectory(output);
        var temporary = Path.Combine(Path.GetTempPath(), "ImageImportAcceptance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Exception? failure = null;
            using var host = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(100, 100) };
            host.Shown += (_, _) =>
            {
                try { Capture(repository, output, temporary, host); }
                catch (Exception ex) { failure = ex; }
                finally { host.Close(); }
            };
            Application.Run(host);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            var full = Path.GetFullPath(temporary);
            Require(string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                    StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("ImageImportAcceptance_", StringComparison.Ordinal),
                "验收辅助文件清理路径超出独立临时目录。");
            Directory.Delete(full, true);
        }
    }

    private static void Capture(string repository, string output, string temporary, Form host)
    {
        var catalog = new ImageCatalogService();
        var (project, resource, target) = SelectPortrait(repository, catalog);
        var sourceHashes = project.ResourcePaths.Values.Where(File.Exists).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, Sha256, StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<object>();
        try
        {
            var descriptor = target.Frames.Single(frame => frame.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase));
            using var portrait = catalog.DecodePreviewFrame(project, target, descriptor);
            Require(portrait != null && HasPortraitDetail(portrait.Bitmap), "真实头像源图为空或只有色块。");
            var source = Path.Combine(temporary, $"真实头像-{target.LogicalId:000}.png");
            portrait!.Bitmap.Save(source, ImageFormat.Png);
            var preparation = new ImageImportPreparationService();

            using (var replace = new ImageImportDialog(project, resource, ImageImportOperation.Replace, [target], preparation))
            {
                Show(replace, host);
                AssignSource(replace, source);
                Await((Task)Call(replace, "PrepareAsync")!);
                RequirePreview(replace, replacing: true);
                Require(Field<ComboBox>(replace, "_outputFormat").SelectedIndex == 0 &&
                    replace.PreparedResult!.Rows.Any(row => row.Role == "Face" && row.OutputFormat == "BMP24"),
                    "真实标准头像默认转换结果不是BMP24。");
                CaptureSizes(replace, "真实头像-替换-默认BMP", output, snapshots);
                foreach (var (index, label) in new[] { (1, "PNG"), (2, "保留原格式") })
                {
                    Field<ComboBox>(replace, "_outputFormat").SelectedIndex = index;
                    Await(Field<Task>(replace, "_formatPreparationTask"));
                    RequirePreview(replace, replacing: true);
                    if (index == 1) Require(replace.PreparedResult!.Rows.Any(row => row.Role == "Face" && row.OutputFormat == "PNG32"),
                        "真实头像切换PNG未生成PNG32候选。");
                    CaptureSizes(replace, "真实头像-替换-" + label, output, snapshots);
                }
                replace.Close();
            }

            using (var append = new ImageImportDialog(project, resource, ImageImportOperation.Append, preparation: preparation))
            {
                Show(append, host);
                AssignSource(append, source);
                Await((Task)Call(append, "PrepareAsync")!);
                RequirePreview(append, replacing: false);
                CaptureSizes(append, "真实头像-新增-默认BMP", output, snapshots);
                append.Close();
            }
            var manifest = new
            {
                Project = project.RootPath, project.VersionText, Target = target.DisplayName,
                target.LogicalId, descriptor.ImageNumber, SourceSize = portrait!.Bitmap.Size,
                SourcePngSha256 = Sha256(source), Confirmed = false, Snapshots = snapshots,
                Rendering = "真实导入窗口内容在离屏Panel中按指定尺寸重新布局并DrawToBitmap；不含原生标题边框，非桌面原生窗口截屏。"
            };
            File.WriteAllText(Path.Combine(output, "真实导入窗口-验收记录.json"), JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            var verified = sourceHashes.Select(pair => new
            {
                Path = pair.Key, Before = pair.Value, After = Sha256(pair.Key)
            }).ToArray();
            File.WriteAllText(Path.Combine(output, "真实导入窗口-原资源SHA256.json"), JsonSerializer.Serialize(verified,
                new JsonSerializerOptions { WriteIndented = true }));
            Require(verified.All(item => item.Before == item.After), "生成导入预览修改了原项目资源。");
        }
        Console.WriteLine("IMAGE_IMPORT_REAL_SCREENSHOTS_OK " + output);
    }

    private static (ProjectProfile Project, ImageResourceDescriptor Resource, LogicalImageAsset Target) SelectPortrait(
        string repository, ImageCatalogService catalog)
    {
        foreach (var name in new[] { "复刻版6.6山贼 国庆", "复刻版6.6山贼", "曹操传6.6-V7.16 解压密码：66" })
        {
            var root = Path.Combine(repository, "基底", name);
            if (!Directory.Exists(root)) continue;
            var project = new ProjectProfileService().Detect(root);
            var resource = catalog.BuildCatalog(project).FirstOrDefault(item => item.Kind == ResourceKind.FaceComposite && item.CanMutate);
            if (resource == null) continue;
            var archive = E5Archive.Open(project.TryGetResource("Face.e5")!);
            if (archive.Entries.Count < 9 || !ImageRendering.IsStandardImage(archive.ReadDecoded(archive.Entries.Count))) continue;
            var tou = project.TryGetResource("Tou.dll");
            if (tou != null && !DllBitmapArchive.Read(tou).Any(record => record.Id == archive.Entries.Count + 300)) continue;
            foreach (var asset in catalog.ReadAssets(project, resource).Where(asset => asset.LogicalId > 0 && !asset.IsFixedSlot).Take(64))
            {
                var frame = asset.Frames.Single(item => item.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase));
                if (!ImageRendering.IsStandardImage(archive.ReadDecoded(frame.ImageNumber))) continue;
                using var decoded = catalog.DecodePreviewFrame(project, asset, frame);
                if (decoded != null && HasPortraitDetail(decoded.Bitmap)) return (project, resource, asset);
            }
        }
        throw new InvalidOperationException("山贼、国庆及V7.16中未找到可切换格式且具备新增模板的真实非0头像。");
    }

    private static void Show(ImageImportDialog dialog, Form host)
    {
        // A child form uses the production control tree and real UI thread, while
        // avoiding the desktop's MaxWindowTrackSize limit for 1920x1080 layout QA.
        dialog.TopLevel = false;
        dialog.FormBorderStyle = FormBorderStyle.None;
        dialog.ShowInTaskbar = false;
        dialog.MaximumSize = new Size(4096, 4096);
        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = Point.Empty;
        host.Controls.Add(dialog);
        dialog.Show(); dialog.Size = new Size(1280, 720); Application.DoEvents();
    }

    private static void AssignSource(ImageImportDialog dialog, string path)
    {
        var slot = ((IEnumerable)Field<object>(dialog, "_slots")).Cast<object>().Single();
        Call(dialog, "Assign", slot, path); Call(dialog, "PopulateMapping");
    }

    private static void RequirePreview(ImageImportDialog dialog, bool replacing)
    {
        Require(dialog.PreparedResult?.CanCommit == true && dialog.ResultPlan == null,
            "真实导入准备失败或意外确认了写入：" + Field<TextBox>(dialog, "_details").Text);
        foreach (var name in replacing ? new[] { "_before", "_sourcePreview", "_after" } : new[] { "_sourcePreview", "_after" })
            Require(Field<PictureBox>(dialog, name).Image is Bitmap image && HasPortraitDetail(image),
                "真实人像预览为空或只有色块：" + name);
    }

    private static void CaptureSizes(ImageImportDialog dialog, string name, string output, List<object> snapshots)
    {
        foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
        {
            Field<CheckBox>(dialog, "_showDetails").Checked = size.Width == 1920;
            // Form.ApplyBoundsConstraints also caps non-top-level borderless
            // forms on this desktop. Render the very same root control tree in
            // a Panel, whose layout bounds have no desktop tracking constraint.
            var content = dialog.Controls.Cast<Control>().Single();
            using var canvas = new Panel { Size = size, Location = Point.Empty, Font = dialog.Font };
            dialog.Parent!.Controls.Add(canvas);
            try
            {
                canvas.Controls.Add(content);
                canvas.CreateControl(); canvas.PerformLayout(); Application.DoEvents(); canvas.Refresh();
                Require(canvas.Size == size && content.Size == size,
                    $"离屏导入内容未达到要求的布局尺寸：{canvas.Size} / {content.Size} / {size}。");
                var file = name + $"-{size.Width}x{size.Height}.png";
                using var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
                canvas.DrawToBitmap(image, new Rectangle(Point.Empty, size));
                image.Save(Path.Combine(output, file), ImageFormat.Png);
                snapshots.Add(new { File = file, Width = size.Width, Height = size.Height,
                    Format = Field<ComboBox>(dialog, "_outputFormat").Text,
                    Rows = dialog.PreparedResult!.Rows.Select(row => new { row.TargetLabel, row.Role, row.OutputFormat, row.SourceSize, row.OutputSize }).ToArray() });
                Console.WriteLine("[IMAGE_IMPORT_REAL] " + file);
            }
            finally { dialog.Controls.Add(content); dialog.PerformLayout(); Application.DoEvents(); }
        }
    }

    private static bool HasPortraitDetail(Bitmap image)
    {
        var colors = new HashSet<int>();
        for (var y = 0; y < image.Height; y += Math.Max(1, image.Height / 48))
        for (var x = 0; x < image.Width; x += Math.Max(1, image.Width / 48)) colors.Add(image.GetPixel(x, y).ToArgb());
        return colors.Count >= 32;
    }

    private static void Await(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(45)) { Application.DoEvents(); Thread.Sleep(5); }
        Require(task.IsCompleted, "真实导入预览未在45秒内完成。");
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }

    private static string FindRepository()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (DirectoryInfo? directory = new(start); directory != null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "基底")) && Directory.Exists(Path.Combine(directory.FullName, "形象管理器")))
                    return directory.FullName;
        throw new DirectoryNotFoundException("找不到包含基底与形象管理器的项目根目录。");
    }

    private static string Sha256(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static object? Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, Private)!.Invoke(instance, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
