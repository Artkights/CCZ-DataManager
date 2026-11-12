using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Text.Json;

namespace RangeManager.App;

/// <summary>Format-level palette regressions using generated, disposable E5 fixtures.</summary>
public static class PaletteVerification
{
    private sealed record Result(string Name, bool Passed, string Detail);

    public static int Run(string output)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "ccz-range-palette-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var results = new List<Result>();
        int serial = 0;
        void Check(string name, Action<string> action)
        {
            try
            {
                string root = Path.Combine(temporary, (++serial).ToString("D2"));
                CreateFixture(root);
                action(root);
                results.Add(new(name, true, "通过"));
            }
            catch (Exception ex) { results.Add(new(name, false, ex.ToString())); }
        }

        try
        {
            Check("精确RGB优先、重复颜色选最低索引", root =>
            {
                using var image = Solid(Color.FromArgb(80, 90, 100));
                using var images = new E5RangeImages(root);
                images.SetIcon(true, 0, image);
                Assert(Payload(images.BuildPendingFiles()[images.AttackPath], 0).All(value => value == 3),
                    "精确色必须选择第3项，不能选择同色第4项或近似色。");
            });
            Check("缺色使用最小RGB平方距离", root =>
            {
                using var image = Solid(Color.FromArgb(18, 1, 0));
                using var images = new E5RangeImages(root);
                images.SetIcon(true, 0, image);
                Assert(Payload(images.BuildPendingFiles()[images.AttackPath], 0).All(value => value == 1),
                    "(18,1,0)到(20,0,0)平方距离5，应使用第1项。");
            });
            Check("等距颜色使用最低索引", root =>
            {
                using var image = Solid(Color.FromArgb(10, 0, 0));
                using var images = new E5RangeImages(root);
                images.SetIcon(true, 0, image);
                Assert(Payload(images.BuildPendingFiles()[images.AttackPath], 0).All(value => value == 0),
                    "到第0与第1项均为100，应选择第0项。");
            });
            Check("索引匹配仅比较RGB、不添加透明索引规则", root =>
            {
                using var image = Solid(Color.FromArgb(0, 18, 1, 0));
                using var images = new E5RangeImages(root);
                images.SetIcon(true, 0, image);
                Assert(Payload(images.BuildPendingFiles()[images.AttackPath], 0).All(value => value == 1),
                    "透明输入仍应按RGB选择第1项。");
            });
            Check("重复编码、缓存命中及新实例输出稳定", root =>
            {
                using var image = Pattern();
                using var first = new E5RangeImages(root);
                first.SetIcon(true, 0, image);
                byte[] candidate = first.BuildPendingFiles()[first.AttackPath];
                first.SetIcon(true, 0, image);
                Assert(candidate.AsSpan().SequenceEqual(first.BuildPendingFiles()[first.AttackPath]), "重复编码候选不一致。");
                using var second = new E5RangeImages(root);
                second.SetIcon(true, 0, image);
                Assert(candidate.AsSpan().SequenceEqual(second.BuildPendingFiles()[second.AttackPath]), "新实例编码候选不一致。");
                byte[] indices = Payload(candidate, 0);
                byte[] expected = [3, 1, 0, 1];
                for (int i = 0; i < indices.Length; i++) Assert(indices[i] == expected[i % 4], "重复颜色映射出现不稳定结果。");
            });
            Check("RAW4096与4098后缀保存复读、Spalet及未改档案不变", root =>
            {
                string palette = Path.Combine(root, "E5", "Spalet.e5");
                string other = Path.Combine(root, "E5", "Effarea.e5");
                string paletteHash = RangeSaveTransaction.Hash(palette), otherHash = RangeSaveTransaction.Hash(other);
                using var image = Pattern();
                using (var images = new E5RangeImages(root))
                {
                    images.SetIcon(true, 0, image); images.SetIcon(true, 1, image);
                    byte[] candidate = images.BuildPendingFiles()[images.AttackPath];
                    byte[] plain = Payload(candidate, 0), suffix = Payload(candidate, 1);
                    Assert(plain.Length == 4096 && suffix.Length == 4098, "RAW载荷长度改变。");
                    Assert(suffix.AsSpan(4096).SequenceEqual(new byte[] { 0xA5, 0x5A }), "4098载荷的后缀改变。");
                    images.Save();
                }
                using var reopened = new E5RangeImages(root);
                Assert(reopened.DecodedLength(true, 0) == 4096 && reopened.DecodedLength(true, 1) == 4098,
                    "保存重开后的RAW长度改变。");
                using var actual = reopened.Copy(true, 0) ?? throw new InvalidDataException("RAW重开失败。");
                Assert(actual.GetPixel(0, 0).ToArgb() == Color.FromArgb(80, 90, 100).ToArgb() &&
                    actual.GetPixel(1, 0).ToArgb() == Color.FromArgb(20, 0, 0).ToArgb(), "RAW颜色未按选定索引复读。");
                Assert(RangeSaveTransaction.Hash(palette) == paletteHash, "索引写回修改了Spalet。");
                Assert(RangeSaveTransaction.Hash(other) == otherHash, "索引写回修改了未编辑档案。");
            });
            Check("PNG继续保留原始ARGB、不做调色板映射", root =>
            {
                string palette = Path.Combine(root, "E5", "Spalet.e5");
                string paletteHash = RangeSaveTransaction.Hash(palette);
                using var image = Solid(Color.FromArgb(91, 123, 45, 67));
                using (var images = new E5RangeImages(root))
                {
                    Assert(!images.PierceIsIndexed, "PNG测试档案格式错误。");
                    images.SetIcon(false, 0, image); images.Save();
                }
                using var reopened = new E5RangeImages(root);
                using var actual = reopened.Copy(false, 0) ?? throw new InvalidDataException("PNG重开失败。");
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                    Assert(actual.GetPixel(x, y).ToArgb() == image.GetPixel(x, y).ToArgb(), "PNG像素被量化或透明值改变。");
                Assert(RangeSaveTransaction.Hash(palette) == paletteHash, "PNG写回修改了Spalet。");
            });
            foreach (bool missing in new[] { true, false })
                Check(missing ? "缺失Spalet仍拒绝RAW写入" : "损坏Spalet仍拒绝RAW写入", root =>
                {
                    string palette = Path.Combine(root, "E5", "Spalet.e5");
                    if (missing) File.Delete(palette); else File.WriteAllBytes(palette, [1, 2, 3]);
                    var before = Directory.GetFiles(Path.Combine(root, "E5")).ToDictionary(path => path, RangeSaveTransaction.Hash);
                    using var image = Solid(Color.FromArgb(18, 1, 0));
                    using var images = new E5RangeImages(root);
                    bool refused = false;
                    try { images.SetIcon(true, 0, image); }
                    catch (InvalidDataException ex) when (ex.Message.Contains("Spalet.e5", StringComparison.Ordinal)) { refused = true; }
                    Assert(refused && !images.IsDirty, "无有效调色板时未明确拒绝或污染了草稿。");
                    Assert(before.All(pair => RangeSaveTransaction.Hash(pair.Key) == pair.Value), "无效调色板失败路径修改了文件。");
                    Assert(File.Exists(palette) != missing, "失败路径创建或删除了Spalet。");
                });
        }
        finally
        {
            string full = Path.GetFullPath(temporary);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("ccz-range-palette-", StringComparison.Ordinal)) Directory.Delete(full, recursive: true);
        }
        bool passed = results.All(result => result.Passed);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = passed, Checks = results.Count, Results = results },
            new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results) Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}{(result.Passed ? "" : " " + result.Detail)}");
        Console.WriteLine((passed ? "PALETTE_VERIFICATION_OK " : "PALETTE_VERIFICATION_FAILED ") + output);
        return passed ? 0 : 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static Bitmap Solid(Color color)
    {
        var image = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) image.SetPixel(x, y, color);
        return image;
    }

    private static Bitmap Pattern()
    {
        var image = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        Color[] colors = [Color.FromArgb(80, 90, 100), Color.FromArgb(18, 1, 0),
            Color.FromArgb(10, 0, 0), Color.FromArgb(0, 18, 1, 0)];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) image.SetPixel(x, y, colors[x % colors.Length]);
        return image;
    }

    private static void CreateFixture(string root)
    {
        string e5 = Path.Combine(root, "E5"); Directory.CreateDirectory(e5);
        var colors = Enumerable.Repeat(Color.FromArgb(250, 250, 250), 256).ToArray();
        colors[0] = Color.Black; colors[1] = Color.FromArgb(20, 0, 0); colors[2] = Color.FromArgb(0, 20, 0);
        colors[3] = colors[4] = Color.FromArgb(80, 90, 100);
        var palette = new byte[768];
        for (int i = 0; i < colors.Length; i++)
        {
            palette[i * 3] = colors[i].B; palette[i * 3 + 1] = colors[i].R; palette[i * 3 + 2] = colors[i].G;
        }
        File.WriteAllBytes(Path.Combine(e5, "Spalet.e5"), Archive(palette));
        byte[] plain = Enumerable.Repeat((byte)7, 4096).ToArray();
        File.WriteAllBytes(Path.Combine(e5, "Hitarea.e5"), Archive(plain, [.. plain, 0xA5, 0x5A]));
        using var image = Solid(Color.FromArgb(255, 25, 35, 45));
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        File.WriteAllBytes(Path.Combine(e5, "Effarea.e5"), Archive(stream.ToArray()));
    }

    private static byte[] Archive(params byte[][] payloads)
    {
        int at = 0x110 + payloads.Length * 12 + 4;
        var bytes = new byte[at + payloads.Sum(payload => payload.Length)];
        for (int i = 0; i < payloads.Length; i++)
        {
            int entry = 0x110 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry), (uint)payloads[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)payloads[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), (uint)at);
            payloads[i].CopyTo(bytes, at); at += payloads[i].Length;
        }
        return bytes;
    }

    private static byte[] Payload(byte[] archive, int index)
    {
        int at = 0x110 + index * 12;
        int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(at)));
        int decoded = checked((int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(at + 4)));
        int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(at + 8)));
        Assert(size == decoded, "编码后的专项测试载荷应为未压缩数据。");
        return archive.AsSpan(offset, size).ToArray();
    }
}
