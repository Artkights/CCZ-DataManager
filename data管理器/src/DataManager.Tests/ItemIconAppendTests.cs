using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class ItemIconAppendTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerItemAppend_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckAppend(root, oddTail: true, additions: 1, appendCopy: false);
            CheckAppend(root, oddTail: true, additions: 2, appendCopy: false);
            CheckAppend(root, oddTail: true, additions: 1, appendCopy: true);
            CheckAppend(root, oddTail: false, additions: 1, appendCopy: false);
            CheckAppend(root, oddTail: true, additions: 1, appendCopy: false, resourceName: "Mtem.e5");
            CheckAppend(root, oddTail: false, additions: 1, appendCopy: true, resourceName: "Mtem.e5");
        }
        finally
        {
            var actual = Path.GetFullPath(root);
            if (!actual.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(actual).StartsWith("DataManagerItemAppend_", StringComparison.Ordinal))
                throw new InvalidOperationException("测试目录清理范围无效。");
            Directory.Delete(actual, true);
        }
    }

    private static void CheckAppend(string root, bool oddTail, int additions, bool appendCopy, string resourceName = "Item.e5")
    {
        var directory = Path.Combine(root, $"{resourceName}_{oddTail}_{additions}_{appendCopy}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, resourceName);
        var small = Png(16, 16, Color.Red);
        var large = Png(32, 32, Color.Green);
        var payloads = Enumerable.Range(0, 512).Select(index => index % 2 == 0 ? small : large).ToList();
        // Keep this reserved tail opaque and marked compressed. Append must
        // preserve its exact stored bytes without trying to use it as an image.
        if (oddTail) payloads.Add([0x81, 0x37, 0xC2, 0xFF, 0x19]);
        var before = Archive(payloads, oddTail);
        File.WriteAllBytes(path, before);
        var mtemPath = Path.Combine(directory, resourceName == "Mtem.e5" ? "Item.e5" : "Mtem.e5");
        File.WriteAllBytes(mtemPath, Archive([Png(16, 16, Color.Blue), Png(32, 32, Color.Green)], false));
        var unrelatedHash = Hash(mtemPath);
        var sources = Enumerable.Range(0, additions).Select(index =>
        {
            var source = Path.Combine(directory, $"source{index}.png");
            File.WriteAllBytes(source, Png(32, 32, index == 0 ? Color.Cyan : Color.Yellow));
            return source;
        }).ToArray();
        var profile = new ProjectProfileService().Detect(directory);
        var catalog = new ImageCatalogService();
        var descriptor = catalog.BuildCatalog(profile).Single(resource => resource.Key.Equals(resourceName, StringComparison.OrdinalIgnoreCase));
        var original = E5Archive.Open(path);
        var history = new HistoryService(Path.Combine(root, "history"));
        var mutations = new ImageMutationService(history);
        MutationPlan plan;
        if (appendCopy)
        {
            using var bitmap = new Bitmap(sources[0]);
            plan = mutations.PlanItemIconEdit(profile, catalog.ReadAssets(profile, descriptor)[0], bitmap, appendCopy: true);
        }
        else plan = mutations.PlanAppend(profile, descriptor, sources);
        AssertCandidate(original, E5Archive.Parse(path, plan.Files.Single().CandidateBytes), additions, oddTail);
        TestSupport.Assert(File.ReadAllBytes(path).SequenceEqual(before), "规划Item追加时提前修改了项目文件");
        var result = mutations.Commit(profile, plan);
        TestSupport.Assert(result.Success && result.HistoryId != null, "Item追加提交失败或没有历史记录");
        var committed = File.ReadAllBytes(path);
        AssertCandidate(original, E5Archive.Parse(path, committed), additions, oddTail);
        var afterDescriptor = catalog.BuildCatalog(profile).Single(resource => resource.Key.Equals(resourceName, StringComparison.OrdinalIgnoreCase));
        var assets = catalog.ReadAssets(profile, afterDescriptor);
        TestSupport.Assert(assets.Count == 256 + additions && assets[256].LogicalId == 256 &&
                           assets[256].Frames.Select(frame => frame.ImageNumber).SequenceEqual([513, 514]),
            "新宝物图标逻辑256未对应新小图513/大图514");
        TestSupport.Assert(history.Undo(profile, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(before),
            "Item追加撤回没有逐字节恢复原文件");
        TestSupport.Assert(history.Redo(profile, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(committed),
            "Item追加重做没有逐字节恢复新增文件");
        TestSupport.Assert(history.Undo(profile, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(before),
            "Item追加二次撤回失败");
        TestSupport.Assert(Hash(mtemPath) == unrelatedHash, "Item追加影响了其他资源文件");
    }

    private static void AssertCandidate(E5Archive original, E5Archive candidate, int additions, bool oddTail)
    {
        TestSupport.Assert(candidate.Entries.Count == original.Entries.Count + additions * 2 &&
                           candidate.DirectoryTrailerLength == original.DirectoryTrailerLength,
            "Item追加条目数或目录尾结构发生错误");
        for (var number = 1; number <= 512; number++)
            AssertStoredEntry(original, number, candidate, number, "Item追加改变了已有图标载荷或编号");
        if (oddTail)
            AssertStoredEntry(original, 513, candidate, 513 + additions * 2, "Item追加丢失或重编码了未配对尾条目");
        for (var index = 0; index < additions; index++)
        {
            using var small = ImageRendering.DecodeStandardImage(candidate.ReadDecoded(513 + index * 2));
            using var large = ImageRendering.DecodeStandardImage(candidate.ReadDecoded(514 + index * 2));
            TestSupport.Assert(small.Size == new Size(16, 16) && large.Size == new Size(32, 32),
                "Item追加没有生成完整16x16/32x32大小图对");
            var expected = index == 0 ? Color.Cyan : Color.Yellow;
            TestSupport.Assert(large.GetPixel(16, 16).ToArgb() == expected.ToArgb(), "Item追加大图内容错位");
        }
    }

    private static void AssertStoredEntry(E5Archive original, int oldNumber, E5Archive candidate, int newNumber, string message)
        => TestSupport.Assert(original.ReadStored(oldNumber).SequenceEqual(candidate.ReadStored(newNumber)) &&
                              original.Entries[oldNumber - 1].DecodedLength == candidate.Entries[newNumber - 1].DecodedLength,
            message);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte[] Png(int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private static byte[] Archive(IReadOnlyList<byte[]> payloads, bool compressedTail)
    {
        const int trailerLength = 4;
        var offset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize + trailerLength;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        for (var index = 0; index < E5Archive.IndexOffset; index++) bytes[index] = (byte)(index % 251);
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = bytes.AsSpan(E5Archive.IndexOffset + index * E5Archive.EntrySize, E5Archive.EntrySize);
            BinaryPrimitives.WriteInt32BigEndian(entry, payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], compressedTail && index == payloads.Count - 1 ? 37 : payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }
}
