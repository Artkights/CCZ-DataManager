using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using DataManager.Core.Migration;
using ImageManager.Core;

namespace DataManager.Images;

/// <summary>Converts legacy icon bitmaps into a verified candidate archive; never writes either input.</summary>
public sealed partial class ImageMigrationResourceConverter : IMigrationResourceConverter, IMigrationResourceVerifier
{
    public bool CanConvert(string sourceRelativePath, string targetRelativePath)
    {
        var source = Path.GetFileName(sourceRelativePath);
        var target = Path.GetFileName(targetRelativePath);
        return source.Equals("Itemicon.dll", StringComparison.OrdinalIgnoreCase) && target.Equals("Item.e5", StringComparison.OrdinalIgnoreCase)
            || source.Equals("Mgcicon.dll", StringComparison.OrdinalIgnoreCase) && target.Equals("Mtem.e5", StringComparison.OrdinalIgnoreCase);
    }

    public byte[] Convert(string sourcePath, string targetTemplatePath,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!CanConvert(sourcePath, targetTemplatePath))
            throw new NotSupportedException($"未验证的图标容器转换：{Path.GetFileName(sourcePath)} → {Path.GetFileName(targetTemplatePath)}。");
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFingerprint = Fingerprint(sourcePath);
        var targetBytes = File.ReadAllBytes(targetTemplatePath);
        var targetFingerprint = SHA256.HashData(targetBytes);
        var template = E5Archive.Parse(targetTemplatePath, targetBytes);
        var entries = ReadOrderedEntries(sourcePath);
        var completeTargetCount = template.Entries.Count / 2 * 2;
        var replacements = new Dictionary<int, byte[]>();
        var appended = new List<byte[]>();
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            progress?.Report(new("转换图标资源", $"{Path.GetFileName(sourcePath)} ID={entry.Id} → {Path.GetFileName(targetTemplatePath)} 图号 {index + 1}", index, entries.Count));
            using var source = DllBitmapArchive.Decode(entry);
            // Mod DLLs can contain a 32×32 raster even in a nominal small slot.
            // The engine's slot identity comes from the resource ID, so retain
            // that raster's actual dimensions rather than resizing or shifting it.
            if (source.Width is <= 0 or > 4096 || source.Height is <= 0 or > 4096)
                throw new InvalidDataException($"RT_BITMAP ID={entry.Id} 尺寸越界，无法安全转换。");
            var encoded = EncodeForE5(entry, source);
            if (index < completeTargetCount) replacements.Add(index + 1, encoded);
            else appended.Add(encoded);
        }

        // A lone final entry is not a complete icon. New pairs go before it so
        // both existing logical IDs and the trailing bytes retain their meaning.
        var hasOrphan = template.Entries.Count % 2 != 0;
        var inserts = hasOrphan && appended.Count > 0
            ? new Dictionary<int, IReadOnlyList<byte[]>> { [template.Entries.Count] = appended }
            : null;
        var candidate = template.Rebuild(replacements, appendDecoded: inserts == null ? appended : null, insertBeforeDecoded: inserts);
        var decodedCandidate = E5Archive.Parse(targetTemplatePath, candidate);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("校验转换像素", $"{Path.GetFileName(targetTemplatePath)} 图号 {index + 1}", index, entries.Count));
            using var source = DllBitmapArchive.Decode(entries[index]);
            using var expected = NormalizeForE5(entries[index], source);
            using var actual = ImageRendering.DecodeStandardImage(decodedCandidate.ReadDecoded(index + 1));
            AssertPixelsEqual(expected, actual, $"转换后的图号 {index + 1}");
        }
        var shift = Math.Max(0, entries.Count - completeTargetCount);
        foreach (var entry in template.Entries.Where(entry => entry.ImageNumber > entries.Count))
        {
            var number = entry.ImageNumber == template.Entries.Count && hasOrphan ? entry.ImageNumber + shift : entry.ImageNumber;
            if (!template.ReadStored(entry.ImageNumber).AsSpan().SequenceEqual(decodedCandidate.ReadStored(number)) ||
                entry.DecodedLength != decodedCandidate.Entries[number - 1].DecodedLength)
                throw new InvalidDataException($"目标保留图号 {entry.ImageNumber} 的原始压缩数据被意外修改。");
        }
        if (hasOrphan && entries.Count >= template.Entries.Count)
        {
            var original = template.Entries.Count;
            if (!template.ReadStored(original).AsSpan().SequenceEqual(decodedCandidate.ReadStored(original + shift)))
                throw new InvalidDataException("目标孤立末尾图像没有完整保留。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!sourceFingerprint.AsSpan().SequenceEqual(Fingerprint(sourcePath)) || !targetFingerprint.AsSpan().SequenceEqual(Fingerprint(targetTemplatePath)))
            throw new IOException("图标转换期间源文件或目标模板被外部修改，已拒绝生成候选。");
        progress?.Report(new("图标转换完成", $"已核对 {entries.Count} 张图片的全部像素，并保留目标未覆盖条目", entries.Count, entries.Count));
        return candidate;
    }

    private static IReadOnlyList<DllBitmapEntry> ReadOrderedEntries(string sourcePath)
    {
        var all = DllBitmapArchive.Read(sourcePath);
        var ordered = all.GroupBy(entry => entry.Id).OrderBy(group => group.Key).Select(group =>
        {
            // Multiple resource languages cannot be represented independently
            // in E5. Accept them only when the visible raster is identical.
            var variants = group.OrderByDescending(entry => entry.Language == 1041)
                .ThenByDescending(entry => entry.Language == 2052).ThenBy(entry => entry.Language).ToArray();
            using var preferred = DllBitmapArchive.Decode(variants[0]);
            var preferredMask = ReadSourceTransparencyMask(variants[0], preferred);
            foreach (var variant in variants.Skip(1))
            {
                using var other = DllBitmapArchive.Decode(variant);
                AssertPixelsEqual(preferred, other, $"RT_BITMAP ID={group.Key} 的多语言变体");
                if (!preferredMask.AsSpan().SequenceEqual(ReadSourceTransparencyMask(variant, other)))
                    throw new InvalidDataException($"RT_BITMAP ID={group.Key} 的多语言透明掩码不同，不能合并为单一E5条目。");
            }
            return variants[0];
        }).ToArray();
        if (ordered.Length == 0 || ordered.Length % 2 != 0 || ordered[0].Id != 100 ||
            ordered.Where((entry, index) => entry.Id != 100 + index).Any())
            throw new InvalidDataException("DLL图标必须是从RT_BITMAP 100开始连续的小图/大图完整配对；缺失或异形编号不能自动迁移。");
        return ordered;
    }

    private static Bitmap NormalizeForE5(DllBitmapEntry entry, Bitmap source)
    {
        var mask = ReadSourceTransparencyMask(entry, source);
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var transparent = mask[y * source.Width + x];
                if (!transparent && pixel.R == 247 && pixel.G == 0 && pixel.B == 255)
                    throw new InvalidDataException($"RT_BITMAP ID={entry.Id} 像素({x},{y})为不透明RGB247,0,255，与目标24位BMP透明键冲突，无法无损转换。");
                result.SetPixel(x, y, transparent ? Color.FromArgb(247, 0, 255) : Color.FromArgb(pixel.R, pixel.G, pixel.B));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static bool[] ReadSourceTransparencyMask(DllBitmapEntry entry, Bitmap source)
    {
        var dib = entry.Dib;
        if (dib.Length < 40) throw new InvalidDataException("未验证的DIB信息头，禁止猜测透明掩码。");
        var header = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16, 4));
        if (header < 40 || header > dib.Length || compression != 0 || bits is not (8 or 24))
            throw new InvalidDataException($"RT_BITMAP ID={entry.Id} 的{bits}位/压缩{compression}透明掩码未验证，禁止自动转换。");
        var mask = new bool[checked(source.Width * source.Height)];
        if (bits == 8)
        {
            var used = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4));
            var paletteCount = used == 0 ? 256 : checked((int)used);
            if (paletteCount is < 1 or > 256) throw new InvalidDataException("DIB调色板数量越界。");
            var start = checked(header + paletteCount * 4);
            var stride = checked((source.Width + 3) / 4 * 4);
            if ((long)start + (long)stride * source.Height > dib.Length) throw new InvalidDataException("DIB像素索引越界。");
            var topDown = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4)) < 0;
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                mask[y * source.Width + x] = dib[start + (topDown ? y : source.Height - y - 1) * stride + x] == 0;
        }
        else
        {
            // 18州 6.5 consumer 40A26D/40A279: F700FF and black are
            // transparent. V10.1 40A259 accepts only F700FF in 24-bit BMP.
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                mask[y * source.Width + x] = pixel.R == 247 && pixel.G == 0 && pixel.B == 255 ||
                    pixel.R == 0 && pixel.G == 0 && pixel.B == 0;
            }
        }
        return mask;
    }

    private static byte[] EncodeForE5(DllBitmapEntry entry, Bitmap source)
    {
        using var bitmap = NormalizeForE5(entry, source);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        var bytes = stream.ToArray();
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) != 24 || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(30, 4)) != 0)
            throw new InvalidDataException("转换器未生成目标引擎支持的24位无压缩BMP。");
        return bytes;
    }

    internal static void AssertPixelsEqual(Bitmap expected, Bitmap actual, string context)
    {
        if (expected.Size != actual.Size) throw new InvalidDataException($"{context} 尺寸不一致。");
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var left = expected.GetPixel(x, y);
            var right = actual.GetPixel(x, y);
            if (left.A == 0 && right.A == 0) continue;
            if (left.ToArgb() != right.ToArgb()) throw new InvalidDataException($"{context} 像素({x},{y})不一致，禁止有损迁移。");
        }
    }

    private static byte[] Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
