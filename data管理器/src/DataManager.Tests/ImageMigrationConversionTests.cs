using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using DataManager.Core.Migration;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class ImageMigrationConversionTests
{
    public static void Run(string sourceRoot, string targetRoot)
    {
        sourceRoot = MigrationService.ResolveProjectRoot(sourceRoot);
        targetRoot = MigrationService.ResolveProjectRoot(targetRoot);
        var temporary = Path.Combine(Path.GetTempPath(), "DataManagerIconConversion_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var adapter = new ImageMigrationResourceConverter();
            TestSupport.Assert(!adapter.CanConvert("Mtem.e5", "Mgcicon.dll"), "未验证的反向容器转换不应开放");
            foreach (var (dll, e5) in new[] { ("Itemicon.dll", "Item.e5"), ("Mgcicon.dll", "Mtem.e5") })
            {
                var source = Find(sourceRoot, dll);
                var target = Find(targetRoot, e5);
                var sourceBefore = Hash(source);
                var targetBefore = Hash(target);
                var clock = Stopwatch.StartNew();
                var candidate = adapter.Convert(source, target);
                var sourceEntries = DllBitmapArchive.Read(source).GroupBy(entry => entry.Id).OrderBy(group => group.Key).Select(group => group.First()).ToArray();
                var before = E5Archive.Open(target);
                var after = E5Archive.Parse(target, candidate);
                TestSupport.Assert(after.Entries.Count == before.Entries.Count, "源图标较少时应保留目标剩余图号");
                AssertMappedPixels(sourceEntries, after);
                for (var number = sourceEntries.Length + 1; number <= before.Entries.Count; number++)
                    TestSupport.Assert(before.ReadStored(number).SequenceEqual(after.ReadStored(number)), "图标转换改变了目标未覆盖的原压缩条目");
                var outputDirectory = Path.Combine(temporary, e5);
                Directory.CreateDirectory(outputDirectory);
                var candidatePath = Path.Combine(outputDirectory, e5);
                File.WriteAllBytes(candidatePath, candidate);
                // A root-level extension DATA file must not shadow a valid image archive.
                var imageRoot = Path.Combine(temporary, "image-root-" + e5);
                var imageDirectory = Path.Combine(imageRoot, "E5");
                Directory.CreateDirectory(imageDirectory);
                File.WriteAllBytes(Path.Combine(imageDirectory, e5), candidate);
                File.WriteAllBytes(Path.Combine(imageRoot, e5), new byte[6400]);
                if (e5 == "Item.e5") File.Copy(Find(targetRoot, "Mtem.e5"), Path.Combine(imageDirectory, "Mtem.e5"));
                else File.Copy(Find(targetRoot, "Item.e5"), Path.Combine(imageDirectory, "Item.e5"));
                var profile = new ProjectProfileService().Detect(imageRoot);
                TestSupport.Assert(profile.TryGetResource(e5) == Path.GetFullPath(Path.Combine(imageDirectory, e5)), "根目录扩展DATA遮蔽了E5图标档案");
                using (var catalog = new ResourceCatalogAdapter())
                {
                    var kind = e5 == "Item.e5" ? ResourceBindingKind.ItemIcon : ResourceBindingKind.StrategyIcon;
                    foreach (var id in new[] { 0, 1, sourceEntries.Length / 2 - 1 })
                    {
                        var mapping = catalog.ResolveBindingPreview(imageRoot, kind, id);
                        TestSupport.Assert(mapping.CanPreview && mapping.PhysicalImageNumbers.SequenceEqual([id * 2 + 1, id * 2 + 2]), "转换后逻辑图标未保留原配对编号");
                        using var image = catalog.DecodeBindingThumbnailAsync(imageRoot, kind, id, new Size(32, 32)).GetAwaiter().GetResult();
                        TestSupport.Assert(image is { Width: 32, Height: 32 }, "转换后关联预览没有读取大图");
                    }
                }
                using (var cancellation = new CancellationTokenSource())
                {
                    var cancelled = false;
                    try { adapter.Convert(source, target, new CallbackProgress(_ => cancellation.Cancel()), cancellation.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    TestSupport.Assert(cancelled, "图标转换循环没有响应安全取消");
                }
                TestSupport.Assert(Hash(source) == sourceBefore && Hash(target) == targetBefore, "候选图标转换或取消修改了输入基底");
                Console.WriteLine($"ICON_CONVERSION source={dll} target={e5} converted={sourceEntries.Length} preserved={before.Entries.Count - sourceEntries.Length} elapsed={clock.ElapsedMilliseconds}ms sourceSha={sourceBefore} targetSha={targetBefore}");
                CheckAppendBeforeOrphan(adapter, source, target, temporary, sourceEntries);
            }
        }
        finally
        {
            var absolute = Path.GetFullPath(temporary);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(absolute).StartsWith("DataManagerIconConversion_", StringComparison.Ordinal))
                throw new InvalidOperationException("转换测试清理路径无效。");
            Directory.Delete(absolute, true);
        }
    }

    private static void CheckAppendBeforeOrphan(ImageMigrationResourceConverter converter, string source, string target,
        string temporary, IReadOnlyList<DllBitmapEntry> sourceEntries)
    {
        var original = E5Archive.Open(target);
        var bytes = original.Rebuild(deleteImageNumbers: Enumerable.Range(4, original.Entries.Count - 3).ToArray());
        var smallTemplate = Path.Combine(temporary, "small-" + Path.GetFileNameWithoutExtension(target), Path.GetFileName(target));
        Directory.CreateDirectory(Path.GetDirectoryName(smallTemplate)!);
        File.WriteAllBytes(smallTemplate, bytes);
        var template = E5Archive.Parse(smallTemplate, bytes);
        var candidate = E5Archive.Parse(smallTemplate, converter.Convert(source, smallTemplate));
        TestSupport.Assert(candidate.Entries.Count == sourceEntries.Count + 1 &&
            candidate.ReadStored(candidate.Entries.Count).SequenceEqual(template.ReadStored(3)), "扩容没有把原孤立尾条目完整保留到末尾");
        AssertMappedPixels(sourceEntries, candidate);
        TestSupport.Assert(File.ReadAllBytes(smallTemplate).SequenceEqual(bytes), "图标扩容候选修改了模板");
    }

    private static void AssertMappedPixels(IReadOnlyList<DllBitmapEntry> sourceEntries, E5Archive target)
    {
        long compared = 0;
        for (var index = 0; index < sourceEntries.Count; index++)
        {
            using var source = DllBitmapArchive.Decode(sourceEntries[index]);
            using var result = ImageRendering.DecodeStandardImage(target.ReadDecoded(index + 1));
            TestSupport.Assert(source.Size == result.Size, "转换改变了图标原始尺寸");
            var dib = sourceEntries[index].Dib;
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
            var pixelStart = BinaryPrimitives.ReadInt32LittleEndian(dib) +
                (bits == 8 ? (BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4)) is var used && used > 0 ? (int)used : 256) * 4 : 0);
            var topDown = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4)) < 0;
            var stride = (source.Width + 3) / 4 * 4;
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var left = source.GetPixel(x, y);
                var right = result.GetPixel(x, y);
                var key = bits == 8 ? dib[pixelStart + (topDown ? y : source.Height - y - 1) * stride + x] == 0
                    : left.R == 247 && left.G == 0 && left.B == 255 || left.R == 0 && left.G == 0 && left.B == 0;
                TestSupport.Assert(key ? right.R == 247 && right.G == 0 && right.B == 255 : left.ToArgb() == right.ToArgb(),
                    $"转换像素不一致：ID={sourceEntries[index].Id} 图号={index + 1} ({x},{y})");
                TestSupport.Assert(key == (right.R == 247 && right.G == 0 && right.B == 255), "图标转换后的透明掩码与源DIB不一致");
                compared++;
            }
        }
        Console.WriteLine($"ICON_CONVERSION_PIXELS images={sourceEntries.Count} pixels={compared}");
    }

    private static string Find(string root, string name)
        => new[] { Path.Combine(root, "E5", name), Path.Combine(root, name) }.First(File.Exists);
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private sealed class CallbackProgress(Action<MigrationProgress> callback) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => callback(value);
    }
}
