using System.Buffers.Binary;
using System.Drawing.Imaging;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class UnifiedHistoryTests
{
    public static void Run(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var dataPath = Path.Combine(root, "Data.e5");
            var originalData = HashService.Sha256(dataPath);
            var value = Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]);
            for (var index = 1; index <= 2; index++)
            {
                workspace.GetDocument("persons").Data.Rows[0]["武力"] = (value + index) % 256;
                TestSupport.Assert(workspace.Save().Saved, "统一历史测试的数据保存失败");
            }
            var savedData = HashService.Sha256(dataPath);
            // Resource writes require their own independently detected layout.
            foreach (var name in new[] { "Itemicon.dll", "Mgcicon.dll" })
            {
                var source = new[] { Path.Combine(basePath, name), Path.Combine(basePath, "E5", name) }.First(File.Exists);
                File.Copy(source, Path.Combine(root, name));
            }
            var imagePath = Path.Combine(root, "Logo.e5");
            File.WriteAllBytes(imagePath, Archive(Color.Blue));
            var originalImage = HashService.Sha256(imagePath);
            var material = Path.Combine(root, "历史素材.png");
            using (var bitmap = new Bitmap(16, 16)) bitmap.Save(material, ImageFormat.Png);
            using var resources = new ResourceCatalogAdapter();
            var append = resources.Append(root, "Logo.e5", [material]);
            TestSupport.Assert(append.Success, "统一历史测试的资源保存失败");
            var savedImage = HashService.Sha256(imagePath);
            var unified = new UnifiedHistoryService(workspace, resources);
            var entries = unified.List();
            TestSupport.Assert(entries.Count == 3 && entries.Count(entry => entry.Source == "数据") == 2 &&
                entries.Count(entry => entry.Source == "形象") == 1 && entries.All(entry => entry.Status == "已保存"),
                "统一历史没有同时读取两套原始 journal 或状态未中文化");
            var imageEntry = entries.Single(entry => entry.Source == "形象");
            TestSupport.Assert(unified.Restore(imageEntry, false).Success && HashService.Sha256(imagePath) == originalImage &&
                HashService.Sha256(dataPath) == savedData, "资源历史撤回写入了错误的来源");
            for (var index = 0; index < 2; index++)
            {
                var entry = unified.FindDefault(false)!;
                TestSupport.Assert(entry.Source == "数据" && unified.Restore(entry, false).Success, "数据历史撤回分发失败");
            }
            TestSupport.Assert(HashService.Sha256(dataPath) == originalData, "连续撤回没有恢复数据原始文件");
            for (var index = 0; index < 3; index++)
                TestSupport.Assert(unified.Restore(unified.FindDefault(true)!, true).Success, "连续重做未按保存依赖顺序恢复");
            TestSupport.Assert(HashService.Sha256(dataPath) == savedData && HashService.Sha256(imagePath) == savedImage,
                "统一历史重做没有恢复两类来源");
            TestSupport.Assert(File.Exists(Path.Combine(root, ".data管理器历史", "manifest.jsonl")) &&
                Directory.EnumerateFiles(Path.Combine(root, ".data管理器形象历史"), "history.jsonl", SearchOption.AllDirectories).Count() == 1,
                "统一历史不应迁移或复制两套底层记录");
            File.AppendAllText(imagePath, "外部改动");
            TestSupport.Assert(!unified.Restore(unified.List().Single(entry => entry.Source == "形象" && entry.CanUndo), false).Success &&
                HashService.Sha256(dataPath) == savedData, "统一历史绕过了资源 SHA 冲突检查");
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Archive(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var png = stream.ToArray();
        var offset = E5Archive.IndexOffset + E5Archive.EntrySize;
        var bytes = new byte[offset + png.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset), png.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 4), png.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 8), offset);
        png.CopyTo(bytes, offset);
        return bytes;
    }
}
