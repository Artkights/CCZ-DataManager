using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ImageManager.Core;

/// <summary>Pixel edits keep the source BMP header, bit depth, palette, padding and trailing bytes.</summary>
public static class BmpPayloadService
{
    internal sealed record Layout(int Width, int Height, int Bits, int Stride, int PixelOffset,
        bool TopDown, int PaletteOffset, int PaletteEntrySize, int PaletteCount, int DeclaredLength);

    internal static Layout? ReadLayout(byte[] bytes)
    {
        if (bytes.Length < 26 || bytes[0] != 'B' || bytes[1] != 'M') return null;
        var dib = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(14, 4));
        if (dib != 12 && dib is not (40 or 52 or 56 or 108 or 124)) return null;
        if ((long)14 + dib > bytes.Length) return null;
        var core = dib == 12;
        var width = core ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18, 2)) : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        var signedHeight = core ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2)) : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(core ? 24 : 28, 2));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(core ? 22 : 26, 2));
        var compression = core ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(30, 4));
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue || planes != 1 || compression != 0 || bits is not (4 or 8 or 24 or 32)) return null;
        var height = Math.Abs(signedHeight);
        var stride64 = (((long)width * bits + 31) / 32) * 4;
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10, 4));
        var paletteOffset = 14 + dib;
        var entrySize = core ? 3 : 4;
        var count = bits <= 8 ? 1 << bits : 0;
        if (!core && count > 0)
        {
            var used = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(46, 4));
            if (used > count) return null;
            if (used != 0) count = (int)used;
        }
        if (stride64 > int.MaxValue || offset < paletteOffset + count * entrySize ||
            (long)offset + stride64 * height > bytes.Length) return null;
        var declared = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(2, 4));
        if (declared <= 0) declared = checked(offset + (int)stride64 * height);
        if (declared > bytes.Length || declared < (long)offset + stride64 * height) return null;
        return new Layout(width, height, bits, (int)stride64, offset, signedHeight < 0,
            paletteOffset, entrySize, count, declared);
    }

    public static IReadOnlyList<Color>? TryGetEmbeddedPalette(byte[] original)
    {
        var layout = ReadLayout(original);
        if (layout == null || layout.PaletteCount == 0) return null;
        return Enumerable.Range(0, layout.PaletteCount).Select(index =>
        {
            var offset = layout.PaletteOffset + index * layout.PaletteEntrySize;
            return Color.FromArgb(255, original[offset + 2], original[offset + 1], original[offset]);
        }).ToArray();
    }

    public static bool TryGetIndexes(byte[] original, out byte[] indexes)
    {
        indexes = [];
        var layout = ReadLayout(original);
        if (layout == null || layout.Bits > 8) return false;
        indexes = new byte[checked(layout.Width * layout.Height)];
        for (var y = 0; y < layout.Height; y++)
        for (var x = 0; x < layout.Width; x++)
        {
            var row = layout.PixelOffset + (layout.TopDown ? y : layout.Height - 1 - y) * layout.Stride;
            var value = layout.Bits == 8 ? original[row + x] : (original[row + x / 2] >> (x % 2 == 0 ? 4 : 0)) & 15;
            if (value >= layout.PaletteCount) throw new InvalidDataException("BMP 像素索引超出自身调色板范围。");
            indexes[y * layout.Width + x] = (byte)value;
        }
        return true;
    }

    public static Bitmap Decode(byte[] original)
    {
        var layout = ReadLayout(original);
        if (layout != null && layout.Bits <= 8 && TryGetIndexes(original, out var indexes))
            return ImageRendering.DecodeIndexed(indexes, layout.Width, layout.Height, TryGetEmbeddedPalette(original)!);
        using var stream = new MemoryStream(original, false);
        using var image = Image.FromStream(stream, false, true);
        // Bitmap(Image) draws through GDI+ and rounds RGB values at partial
        // alpha. Decoding must preserve the same pixels as a file-import
        // candidate; the conservative rebuild/read-pixel rules remain separate.
        return ImageImportRasterNormalization.CopyArgb((Bitmap)image);
    }

    public static byte[] Rebuild(byte[] original, Bitmap edited, IReadOnlyList<Color>? rawPalette = null)
    {
        if (original.Length >= 2 && original[0] == 'B' && original[1] == 'M')
        {
            var layout = ReadLayout(original) ?? throw new InvalidDataException("该 BMP 压缩或像素布局尚未验证，保留原文件并禁止按 RAW 重写。");
            EnsureSize(edited, layout.Width, layout.Height);
            var result = (byte[])original.Clone();
            if (layout.Bits <= 8)
            {
                _ = TryGetIndexes(original, out var oldIndexes);
                var indexes = QuantizePreservingIndexes(edited, TryGetEmbeddedPalette(original)!, oldIndexes);
                WriteIndexes(result, layout, indexes);
            }
            else
            {
                var pixels = ReadPixels(edited);
                var pixelWidth = layout.Bits / 8;
                var keyRed = (byte)247;
                for (var y = 0; y < layout.Height; y++)
                {
                    var row = layout.PixelOffset + y * layout.Stride;
                    for (var x = 0; x < layout.Width; x++)
                    {
                        var at = row + x * pixelWidth;
                        if (original[at] == 255 && original[at + 1] == 0 && original[at + 2] is 247 or 255)
                        { keyRed = original[at + 2]; break; }
                    }
                }
                for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                {
                    var source = (y * layout.Width + x) * 4;
                    var target = layout.PixelOffset + (layout.TopDown ? y : layout.Height - 1 - y) * layout.Stride + x * pixelWidth;
                    // BI_RGB's fourth byte is reserved, rather than an alpha channel.
                    if (pixels[source + 3] < 128)
                    {
                        // CCZ's BI_RGB payloads encode transparency using the magenta key, not alpha.
                        if (original[target] == 255 && original[target + 1] == 0 && original[target + 2] is 247 or 255) continue;
                        result[target] = 255; result[target + 1] = 0; result[target + 2] = keyRed;
                    }
                    else
                    { result[target] = pixels[source]; result[target + 1] = pixels[source + 1]; result[target + 2] = pixels[source + 2]; }
                }
            }
            using var check = Decode(result);
            EnsureSize(check, edited.Width, edited.Height);
            return result;
        }
        if (!ImageRendering.IsStandardImage(original))
            throw new InvalidDataException("非标准图片需要明确宽高及有效调色板，使用 RebuildRaw8。");
        using var before = Decode(original);
        EnsureSize(edited, before.Width, before.Height);
        if (ReadPixels(before).AsSpan().SequenceEqual(ReadPixels(edited))) return (byte[])original.Clone();
        var imageLength = GetStandardImageLength(original);
        using var output = new MemoryStream();
        edited.Save(output, original[0] == 0x89 ? ImageFormat.Png : ImageFormat.Jpeg);
        output.Write(original.AsSpan(imageLength));
        var rebuilt = output.ToArray();
        using var reread = Decode(rebuilt);
        EnsureSize(reread, before.Width, before.Height);
        return rebuilt;
    }

    public static byte[] RebuildRaw8(byte[] original, int width, int height, Bitmap edited, IReadOnlyList<Color> palette)
    {
        EnsureSize(edited, width, height);
        if (original.Length < checked(width * height)) throw new InvalidDataException("原始 RAW8 像素长度不足。");
        var result = (byte[])original.Clone();
        QuantizePreservingIndexes(edited, palette, original.AsSpan(0, width * height).ToArray()).CopyTo(result, 0);
        return result;
    }

    public static byte[] QuantizePreservingIndexes(Bitmap edited, IReadOnlyList<Color> palette, byte[]? originalIndexes = null)
    {
        if (palette.Count is < 1 or > 256) throw new InvalidDataException("没有可用于写入的有效调色板。");
        var pixels = ReadPixels(edited);
        var count = checked(edited.Width * edited.Height);
        if (originalIndexes != null && originalIndexes.Length != count) throw new InvalidDataException("原始索引尺寸不一致。");
        var indexes = new byte[count];
        var colors = new Dictionary<int, byte>();
        for (var i = 0; i < count; i++)
        {
            var at = i * 4;
            var b = pixels[at]; var g = pixels[at + 1]; var r = pixels[at + 2];
            var transparent = pixels[at + 3] < 128;
            if (originalIndexes != null && originalIndexes[i] < palette.Count)
            {
                var old = palette[originalIndexes[i]];
                if (old.R == r && old.G == g && old.B == b || transparent &&
                    (originalIndexes[i] == 0 || old.G == 0 && old.B == 255 && old.R >= 247))
                { indexes[i] = originalIndexes[i]; continue; }
            }
            if (transparent) { indexes[i] = 0; continue; }
            var key = r << 16 | g << 8 | b;
            if (!colors.TryGetValue(key, out var best))
            {
                var distance = int.MaxValue; best = 0;
                for (var p = 0; p < palette.Count; p++)
                {
                    var color = palette[p]; var dr = r - color.R; var dg = g - color.G; var db = b - color.B;
                    var d = dr * dr + dg * dg + db * db;
                    if (d >= distance) continue;
                    distance = d; best = (byte)p;
                    if (d == 0) break;
                }
                colors[key] = best;
            }
            indexes[i] = best;
        }
        return indexes;
    }

    public static byte[] ReadPixels(Bitmap source)
    {
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        { graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; graphics.DrawImageUnscaled(source, 0, 0); }
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * bitmap.Width * 4, bitmap.Width * 4);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    public static int GetStandardImageLength(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return ReadLayout(bytes)?.DeclaredLength ?? throw new InvalidDataException("BMP 边界不完整。");
        if (bytes.Length >= 8 && bytes[0] == 0x89)
        {
            var at = 8;
            while (at <= bytes.Length - 12)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
                if (length > int.MaxValue || (long)at + 12 + length > bytes.Length) break;
                var end = checked(at + 12 + (int)length);
                if (bytes.AsSpan(at + 4, 4).SequenceEqual("IEND"u8)) return end;
                at = end;
            }
            throw new InvalidDataException("PNG 没有完整 IEND 边界。");
        }
        // JPEG is a standard image, but its segment tail is retained only when the stream has a final EOI.
        for (var at = bytes.Length - 2; at >= 2; at--)
            if (bytes[at] == 0xff && bytes[at + 1] == 0xd9) return at + 2;
        throw new InvalidDataException("标准图片边界未经验证。");
    }

    private static void EnsureSize(Bitmap image, int width, int height)
    { if (image.Width != width || image.Height != height) throw new InvalidDataException($"图片必须为 {width}×{height}，实际 {image.Width}×{image.Height}。"); }

    internal static void WriteIndexes(byte[] target, Layout layout, byte[] indexes)
    {
        for (var y = 0; y < layout.Height; y++)
        for (var x = 0; x < layout.Width; x++)
        {
            var row = layout.PixelOffset + (layout.TopDown ? y : layout.Height - 1 - y) * layout.Stride;
            var value = indexes[y * layout.Width + x];
            if (value >= layout.PaletteCount) throw new InvalidDataException("索引超出目标 BMP 调色板。");
            if (layout.Bits == 8) target[row + x] = value;
            else
            {
                var shift = x % 2 == 0 ? 4 : 0;
                target[row + x / 2] = (byte)((target[row + x / 2] & ~(15 << shift)) | value << shift);
            }
        }
    }
}
