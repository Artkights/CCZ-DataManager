using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class RangePreviewCompatibilityTests
{
    public static void Run(string repositoryRoot)
    {
        TestRawAndStandardContainers();
        var fixtureRoot = Path.Combine(repositoryRoot, "基底");
        var checkedProjects = 0;
        var checkedImages = 0;
        var rawImages = 0;
        var standardImages = 0;
        foreach (var hitareaPath in Directory.EnumerateFiles(fixtureRoot, "Hitarea.e5", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(hitareaPath)!;
            var projectRoot = Path.GetFileName(directory).Equals("E5", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(directory)!.FullName : directory;
            using var adapter = new ResourceCatalogAdapter();
            var profile = new ProjectProfileService().Detect(projectRoot);
            var palette = new PaletteService().Load(profile);
            using var baselinePreviews = new ImagePreviewService();
            foreach (var attack in new[] { true, false })
            {
                var key = attack ? "Hitarea.e5" : "Effarea.e5";
                var path = profile.TryGetResource(key);
                if (path == null) continue;
                var before = SHA256.HashData(File.ReadAllBytes(path));
                var archive = E5Archive.Open(path);
                foreach (var entry in archive.Entries)
                {
                    var info = adapter.ResolveRangePreview(projectRoot, attack, entry.ImageNumber - 1);
                    TestSupport.Assert(info.CanPreview && info.PhysicalImageNumber == entry.ImageNumber &&
                                       info.EntryCount == archive.Entries.Count, $"{path} #{entry.ImageNumber} 范围字段映射错误");
                    var bytes = archive.ReadDecoded(entry.ImageNumber);
                    using var image = adapter.DecodeRangeFirstFrameAsync(projectRoot, attack, entry.ImageNumber - 1,
                        new Size(512, 512)).GetAwaiter().GetResult();
                    TestSupport.Assert(image != null, $"{path} #{entry.ImageNumber} 范围预览为空");
                    if (ImageRendering.IsStandardImage(bytes))
                    {
                        using var original = ImageRendering.DecodeStandardImage(bytes);
                        TestSupport.Assert(image!.Size == original.Size, $"{path} #{entry.ImageNumber} 标准图片尺寸改变");
                        // Compare standard-image thumbnails against the old
                        // generic codec through the same rendering service.
                        // GDI+ bitmap copies can alter semitransparent RGB, so
                        // an unrendered PNG is not an exact thumbnail baseline.
                        var asset = adapter.ReadAssets(projectRoot, key).Single(item => item.LogicalId == entry.ImageNumber);
                        using var expected = baselinePreviews.GetThumbnailAsync(profile, new StandardRangeBaselineCatalog(),
                            asset, asset.Frames.Single(), new Size(512, 512), true).GetAwaiter().GetResult();
                        TestSupport.Assert(expected != null && image.Size == expected.Size, $"{path} 标准预览基准不可用");
                        AssertPixels(image, (x, y) => expected!.GetPixel(x, y), path);
                        standardImages++;
                    }
                    else
                    {
                        TestSupport.Assert(bytes.Length == 64 * 64 && image!.Size == new Size(64, 64),
                            $"{path} #{entry.ImageNumber} RAW 范围图片尺寸错误");
                        AssertPixels(image!, (x, y) => palette[bytes[y * 64 + x]], path);
                        rawImages++;
                    }
                    checkedImages++;
                }
                TestSupport.Assert(SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(before), $"范围预览修改了资源：{path}");
            }
            checkedProjects++;
            Console.WriteLine($"RANGE_PREVIEW_PROJECT_OK {projectRoot}");
        }
        TestSupport.Assert(checkedProjects > 0 && rawImages > 0 && standardImages > 0,
            "真实基底矩阵必须同时覆盖 RAW 与标准图片范围资源");
        Console.WriteLine($"RANGE_PREVIEW_COMPATIBILITY_TESTS_OK projects={checkedProjects} images={checkedImages} raw={rawImages} standard={standardImages}");
    }

    private static void TestRawAndStandardContainers()
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerRangePreview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paletteBytes = new byte[1024];
            for (var index = 0; index < 256; index++)
            {
                paletteBytes[index * 4] = (byte)(index ^ 85);
                paletteBytes[index * 4 + 1] = (byte)(255 - index);
                paletteBytes[index * 4 + 2] = (byte)index;
            }
            File.WriteAllBytes(Path.Combine(root, "tsb"), paletteBytes);
            var raw = Enumerable.Range(0, 64 * 64).Select(index => (byte)((index / 64 * 13 + index % 64 * 7) % 256)).ToArray();
            byte[] standard;
            using (var bitmap = new Bitmap(32, 24))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Orange);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                standard = stream.ToArray();
            }
            File.WriteAllBytes(Path.Combine(root, "Hitarea.e5"), BuildArchive([raw, standard, new byte[4095], new byte[4097]]));
            File.WriteAllBytes(Path.Combine(root, "Effarea.e5"), BuildArchive([raw]));
            File.WriteAllBytes(Path.Combine(root, "Foreign.e5"), BuildArchive([raw]));
            using var adapter = new ResourceCatalogAdapter();
            foreach (var attack in new[] { true, false })
            {
                using var image = adapter.DecodeRangeFirstFrameAsync(root, attack, 0, new Size(96, 96)).GetAwaiter().GetResult();
                TestSupport.Assert(image?.Size == new Size(64, 64), "64×64 RAW 范围图片没有正确解码");
                AssertPixels(image!, (x, y) =>
                {
                    var index = raw[y * 64 + x];
                    return Color.FromArgb(index, 255 - index, index ^ 85);
                }, attack ? "合成 Hitarea" : "合成 Effarea");
            }
            using (var image = adapter.DecodeRangeFirstFrameAsync(root, true, 1, new Size(96, 96)).GetAwaiter().GetResult())
                TestSupport.Assert(image?.Size == new Size(32, 24) && image.GetPixel(0, 0).ToArgb() == Color.Orange.ToArgb(),
                    "标准 PNG 范围图片被错误地按 RAW 尺寸读取");
            foreach (var value in new[] { 2, 3 })
                ExpectInvalidData(() =>
                {
                    using var image = adapter.DecodeRangeFirstFrameAsync(root, true, value, new Size(96, 96)).GetAwaiter().GetResult();
                }, "未知 RAW 范围尺寸没有拒绝读取");
            ExpectInvalidData(() =>
            {
                var frames = adapter.DecodeFrames(root, "Discovered:Foreign.e5", 1);
                foreach (var frame in frames) frame.Dispose();
            }, "未知 E5 文件被错误地猜测为 RAW 范围图片");
            var project = new ProjectProfileService().Detect(root);
            var catalog = new ImageCatalogService();
            var resource = catalog.BuildCatalog(project).Single(item => item.Key == "Hitarea.e5");
            var asset = catalog.ReadAssets(project, resource).First();
            TestSupport.Assert(asset.LogicalId == 1 && asset.Frames.Single() is { ImageNumber: 1, Width: 64, Height: 64 },
                "范围画廊没有保留一基图号和 RAW 帧尺寸");
            var decoded = catalog.DecodeFrames(project, asset);
            try
            {
                TestSupport.Assert(decoded.Count == 1 && decoded[0].PaletteIndexes!.SequenceEqual(raw) &&
                                   decoded[0].PaletteBgra?.Length == 1024, "RAW 范围解码没有保留原像素索引及调色板");
            }
            finally { foreach (var frame in decoded) frame.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StandardRangeBaselineCatalog : IImageCatalogService
    {
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => [];
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
            => new GenericE5ResourceCodec().ReadAssets(project, resource);
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
            => new GenericE5ResourceCodec().DecodeFrames(project, asset);
    }

    private static void AssertPixels(Bitmap actual, Func<int, int, Color> expected, string context)
    {
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < actual.Width; x++)
        {
            var actualColor = actual.GetPixel(x, y);
            var expectedColor = expected(x, y);
            if (actualColor.ToArgb() != expectedColor.ToArgb())
                throw new InvalidOperationException($"{context} 像素 ({x}, {y}) 的方向或调色板错误：{actualColor} / {expectedColor}");
        }
    }

    private static void ExpectInvalidData(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static byte[] BuildArchive(IReadOnlyList<byte[]> payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize + 4;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = bytes.AsSpan(E5Archive.IndexOffset + index * E5Archive.EntrySize, E5Archive.EntrySize);
            BinaryPrimitives.WriteInt32BigEndian(entry, payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], offset);
            payloads[index].CopyTo(bytes, offset);
            offset += payloads[index].Length;
        }
        return bytes;
    }
}
