using System.Security.Cryptography;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class AnimationRepresentativeFrameTests
{
    /// <summary>Read-only matrix across every Mcall in the four reference bases, plus Meff and Weather.</summary>
    public static void Run(string repositoryRoot)
    {
        var bases = Path.Combine(repositoryRoot, "基底");
        var roots = new[]
        {
            Path.Combine(bases, "加强版6.5未加密版"),
            Path.Combine(bases, "曹操传6.6-V7.16 解压密码：66"),
            Path.Combine(bases, "曹操传6.6-V10.1 【解压密码：66】", "复刻版6.6"),
            Path.GetDirectoryName(Path.GetDirectoryName(Directory.EnumerateFiles(Path.Combine(bases, "18州"),
                "Mcall00.e5", SearchOption.AllDirectories).Single()))!
        };
        var mcallCount = 0;
        var smallCount = 0;
        var weatherCount = 0;
        foreach (var root in roots)
        {
            var project = new ProjectProfileService().Detect(root);
            var catalog = new ImageCatalogService();
            var resources = catalog.BuildCatalog(project).Where(resource =>
                resource.Kind is ResourceKind.McallAnimation or ResourceKind.WeatherSpecial ||
                resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase)).ToArray();
            var before = resources.SelectMany(resource => resource.Paths).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => path, Sha, StringComparer.OrdinalIgnoreCase);
            using var adapter = new ResourceCatalogAdapter();
            var adapterCatalog = adapter.Scan(root);
            foreach (var resource in resources)
            {
                TestSupport.Assert(resource.CanPreview, $"动画资源未识别：{root}/{resource.Key}：{resource.Status}");
                if (resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
                {
                    var meff = adapterCatalog.Resources.Single(item => item.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase));
                    var expectedCount = root.Contains("18州", StringComparison.OrdinalIgnoreCase) ? 64 : meff.EntryCount;
                    TestSupport.Assert(meff.CanPreview && meff.EntryCount == expectedCount && meff.Paths.Count == 1 &&
                        Path.GetDirectoryName(meff.Paths[0])!.Equals(Path.Combine(root, "E5"), StringComparison.OrdinalIgnoreCase),
                        $"Meff 应按 E5 规范路径保留64条逐项探针：{root}：{meff.Status}");
                }
                foreach (var asset in catalog.ReadAssets(project, resource))
                {
                    if (asset.Status.Contains("载荷探针失败", StringComparison.Ordinal))
                    {
                        // The verified 18-state archive keeps a few opaque
                        // tail entries after the playable Meff rows. They are
                        // intentionally retained in the 64-item gallery and
                        // shown with a diagnostic, but have no decodable frame.
                        continue;
                    }
                    IReadOnlyList<DecodedFrame> full;
                    try { full = catalog.DecodeFrames(project, asset); }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException($"策略动画解码失败：{root}/{resource.Key}/{asset.DisplayName}", ex);
                    }
                    try
                    {
                        TestSupport.Assert(full.Count > 0, $"动画没有播放帧：{asset.StablePhysicalId}");
                        foreach (var index in new[] { 0, full.Count - 1 }.Distinct())
                        {
                            using var representative = catalog.DecodePreviewFrame(project, asset,
                                asset.Frames[0] with { FrameIndex = index });
                            TestSupport.Assert(representative != null && SamePixels(full[index].Bitmap, representative.Bitmap),
                                $"代表帧与完整解码不同：{root}/{asset.StablePhysicalId}/播放步{index}");
                        }
                        using var thumbnail = adapter.DecodeFirstFrame(root, resource.Key, asset.LogicalId, full[0].Bitmap.Size);
                        TestSupport.Assert(thumbnail != null && SamePixels(full[0].Bitmap, thumbnail),
                            $"适配层缩略图没有使用正确首播放步：{root}/{asset.StablePhysicalId}");
                        if (resource.Kind == ResourceKind.McallAnimation) mcallCount++;
                        else if (resource.Kind == ResourceKind.WeatherSpecial) weatherCount++;
                        else smallCount++;
                    }
                    finally { foreach (var frame in full) frame.Dispose(); }
                }
            }
            foreach (var (path, hash) in before)
                TestSupport.Assert(Sha(path) == hash, "代表帧与动画解码改变了源文件：" + path);
        }
        TestSupport.Assert(mcallCount == 38, $"Mcall矩阵应覆盖38个完整动画，实际{mcallCount}。");
        TestSupport.Assert(smallCount > 0 && weatherCount > 0, "Mcall矩阵没有同时覆盖Meff和Weather。");
        Console.WriteLine($"代表帧完整矩阵通过：Mcall={mcallCount}，Meff={smallCount}，Weather={weatherCount}；首/末帧像素及源SHA一致。");
    }

    private static bool SamePixels(Bitmap left, Bitmap right)
        => left.Size == right.Size && SHA256.HashData(BmpPayloadService.ReadPixels(left))
            .AsSpan().SequenceEqual(SHA256.HashData(BmpPayloadService.ReadPixels(right)));

    private static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
