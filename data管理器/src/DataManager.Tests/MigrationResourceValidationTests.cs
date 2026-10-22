using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class MigrationResourceValidationTests
{
    public static void Run(string basePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerResourceValidation_" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(root, "stage"); var target = Path.Combine(root, "target");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(target);
        try
        {
            var adapter = new ImageMigrationResourceConverter();
            var png = Raster(48, 48, ImageFormat.Png);
            TestSupport.Assert(adapter.ValidateFile(stage, target, "E5/Logo.e5", Archive(png)).Verified, "已知图像E5未实际验证");
            Reject(() => adapter.ValidateFile(stage, target, "E5/Logo.e5", Archive(png[..^8])), "截断PNG被静默接受");
            var malformed = Archive(png); malformed[0x110 + 8] = 0x7f;
            Reject(() => adapter.ValidateFile(stage, target, "E5/Logo.e5", malformed), "已知E5损坏目录被接受");
            TestSupport.Assert(!adapter.ValidateFile(stage, target, "Foreign.e5", [1, 2, 3]).Verified, "未知E5被错误宣称验证");
            TestSupport.Assert(!adapter.ValidateFile(stage, target, "Logo.e5", Archive([1, 2, 3])).Verified, "未知RAW语义被错误宣称验证");
            TestSupport.Assert(adapter.ValidateFile(stage, target, "Pmapobj.e5", Archive(new byte[48 * 1280 + 2])).Verified, "已知R条带含两字节尾部未接受");
            Reject(() => adapter.ValidateFile(stage, target, "Pmapobj.e5", Archive(new byte[48 * 1280 - 1])), "截断R条带被接受");
            TestSupport.Assert(adapter.ValidateFile(stage, target, "Unit_mov.e5", Archive(new byte[48 * 528])).Verified, "已知S动作条未接受");
            Reject(() => adapter.ValidateFile(stage, target, "Unit_atk.e5", Archive(Raster(48, 768, ImageFormat.Png))), "S动作条宽度不符被接受");
            TestSupport.Assert(adapter.ValidateFile(stage, target, "Face.e5", Archive(new byte[64 * 80])).Verified, "已知RAW头像未接受");
            Reject(() => adapter.ValidateFile(stage, target, "Face.e5", Archive(new byte[64 * 80 - 1])), "未知RAW头像长度被接受");
            TestSupport.Assert(adapter.ValidateFile(stage, target, "Weather.e5", Archive(new byte[43200])).Verified, "天气固定四帧未验证");
            Reject(() => adapter.ValidateFile(stage, target, "Weather.e5", Archive(new byte[43201])), "天气长度不符被接受");
            var pmap = new byte[16 + 4 * 5]; BinaryPrimitives.WriteUInt16LittleEndian(pmap.AsSpan(8), 4); BinaryPrimitives.WriteUInt16LittleEndian(pmap.AsSpan(10), 5);
            TestSupport.Assert(adapter.ValidateFile(stage, target, "Pmap.e5", Archive(pmap)).Verified, "Pmap头部与平面未校验");
            pmap[10] = 6; Reject(() => adapter.ValidateFile(stage, target, "Pmap.e5", Archive(pmap)), "Pmap尺寸不符被接受");
            var animation = Animation();
            TestSupport.Assert(adapter.ValidateFile(stage, target, "Mcall00.e5", animation).Verified, "Mcall已知记录未验证");
            animation[20] = 1; Reject(() => adapter.ValidateFile(stage, target, "Mcall00.e5", animation), "Mcall播放越界被接受");
            TestSupport.Assert(adapter.ValidateFile(stage, target, "DT.e5", Archive(Animation())).Verified, "DT专用动画未验证");

            Directory.CreateDirectory(Path.Combine(target, "E5")); Directory.CreateDirectory(Path.Combine(target, "Map"));
            var terrain = Archive(new byte[2 + 2 * 1]);
            File.WriteAllBytes(Path.Combine(target, "E5", "Hexzmap.e5"), terrain);
            var jpeg = Raster(96, 48, ImageFormat.Jpeg); File.WriteAllBytes(Path.Combine(target, "Map", "M000.JPG"), jpeg);
            var mapResult = adapter.ValidateFile(stage, target, "Map/M000.JPG", jpeg);
            TestSupport.Assert(mapResult.Verified && mapResult.DependencyHashes?.TryGetValue(Path.Combine("E5", "Hexzmap.e5"), out var hash) == true &&
                hash == Convert.ToHexString(SHA256.HashData(terrain)), "地图fallback配对依赖未按同快照哈希记录");
            var hexResult = adapter.ValidateFile(stage, target, "E5/Hexzmap.e5", terrain);
            TestSupport.Assert(hexResult.Verified && hexResult.DependencyHashes?.Count == 1, "Hexzmap未核对未选地图依赖");
            Reject(() => adapter.ValidateFile(stage, target, "Map/M000.JPG", Raster(48, 48, ImageFormat.Jpeg)), "地图网格与地形长度不符被接受");
            Directory.CreateDirectory(Path.Combine(stage, "E5"));
            File.WriteAllBytes(Path.Combine(stage, "E5", "Hexzmap.e5"), Archive(new byte[3]));
            var staged = adapter.ValidateFile(stage, target, "Map/M000.JPG", Raster(48, 48, ImageFormat.Jpeg));
            TestSupport.Assert(staged.Verified && staged.DependencyHashes?.Count == 0, "配对未优先采用staging候选");
            Reject(() => adapter.ValidateFile(stage, target, "Itemicon.dll", [1, 2, 3]), "无效已知图片DLL被接受");
            var dll = new[] { Path.Combine(basePath, "Itemicon.dll"), Path.Combine(basePath, "E5", "Itemicon.dll") }.FirstOrDefault(File.Exists);
            if (dll != null) TestSupport.Assert(adapter.ValidateFile(stage, target, "Itemicon.dll", File.ReadAllBytes(dll)).Verified, "真实6.5图片DLL未严格解码");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = false;
            try { adapter.ValidateFile(stage, target, "Logo.e5", Archive(png), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            TestSupport.Assert(cancelled, "资源验证未响应取消");
            Console.WriteLine("MIGRATION_RESOURCE_VALIDATION_TESTS_OK");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("DataManagerResourceValidation_", StringComparison.Ordinal)) throw new InvalidOperationException("测试清理路径无效。");
            Directory.Delete(root, true);
        }
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static byte[] Raster(int width, int height, ImageFormat format)
    {
        using var image = new Bitmap(width, height); using var graphics = Graphics.FromImage(image); graphics.Clear(Color.Red);
        using var stream = new MemoryStream(); image.Save(stream, format); return stream.ToArray();
    }

    private static byte[] Animation()
    {
        var result = new byte[40 + 2 * 2]; result[0] = 1; result[1] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), 2); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), 2);
        return result;
    }

    private static byte[] Archive(params byte[][] entries)
    {
        var first = 0x110 + entries.Length * 12; var bytes = new byte[first + entries.Sum(entry => entry.Length)]; var at = first;
        for (var i = 0; i < entries.Length; i++)
        {
            var directory = bytes.AsSpan(0x110 + i * 12, 12);
            BinaryPrimitives.WriteInt32BigEndian(directory, entries[i].Length); BinaryPrimitives.WriteInt32BigEndian(directory[4..], entries[i].Length);
            BinaryPrimitives.WriteInt32BigEndian(directory[8..], at); entries[i].CopyTo(bytes, at); at += entries[i].Length;
        }
        return bytes;
    }
}
