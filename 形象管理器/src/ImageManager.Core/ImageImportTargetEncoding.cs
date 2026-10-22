using System.Drawing;
using System.Drawing.Imaging;

namespace ImageManager.Core;

/// <summary>External file conversion does not inherit unchanged pixel-editor indexes or RGB rounding.</summary>
internal static class ImageImportTargetEncoding
{
    internal static byte[] Standard(byte[] original, Bitmap image, CancellationToken token = default)
    {
        if (original.Length >= 2 && original[0] == 'B' && original[1] == 'M')
        {
            var layout = BmpPayloadService.ReadLayout(original)
                ?? throw new InvalidDataException("目标 BMP 的压缩或像素布局未经验证，无法转换写回。");
            RequireSize(image, layout.Width, layout.Height);
            var result = (byte[])original.Clone();
            if (layout.Bits <= 8)
                BmpPayloadService.WriteIndexes(result, layout, Quantize(image, BmpPayloadService.TryGetEmbeddedPalette(original)!, token));
            else
            {
                var pixels = ImageImportRasterNormalization.ReadArgbPixels(image);
                var pixelWidth = layout.Bits / 8;
                byte keyRed = 247;
                for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                {
                    if (x == 0) token.ThrowIfCancellationRequested();
                    var at = layout.PixelOffset + y * layout.Stride + x * pixelWidth;
                    if (original[at] == 255 && original[at + 1] == 0 && original[at + 2] is 247 or 255)
                        keyRed = original[at + 2];
                }
                for (var y = 0; y < layout.Height; y++)
                for (var x = 0; x < layout.Width; x++)
                {
                    if (x == 0) token.ThrowIfCancellationRequested();
                    var source = (y * layout.Width + x) * 4;
                    var destination = layout.PixelOffset + (layout.TopDown ? y : layout.Height - y - 1) * layout.Stride + x * pixelWidth;
                    var transparent = pixels[source + 3] == 0;
                    result[destination] = transparent ? (byte)255 : pixels[source];
                    result[destination + 1] = transparent ? (byte)0 : pixels[source + 1];
                    result[destination + 2] = transparent ? keyRed : pixels[source + 2];
                    // BI_RGB reserves byte four; preserve the target's verified native layout.
                }
            }
            return result;
        }
        using var before = ImageImportRasterNormalization.DecodeArgb(original);
        RequireSize(image, before.Width, before.Height);
        using var stream = new MemoryStream();
        image.Save(stream, original[0] == 137 ? ImageFormat.Png : ImageFormat.Jpeg);
        return stream.ToArray();
    }

    internal static byte[] Raw8(byte[] original, int width, int height, Bitmap image, IReadOnlyList<Color> palette, CancellationToken token = default)
    {
        RequireSize(image, width, height);
        var count = checked(width * height);
        if (original.Length != count && original.Length != count + 2)
            throw new InvalidDataException("RAW 导入目标必须是已确认像素区，可带合法两字节尾部。");
        if (palette.Count != 256) throw new InvalidDataException("RAW 文件导入需要有效的256色项目调色板。");
        var result = (byte[])original.Clone();
        Quantize(image, palette, token).CopyTo(result, 0);
        return result;
    }

    private static byte[] Quantize(Bitmap image, IReadOnlyList<Color> palette, CancellationToken token)
    {
        if (palette.Count is < 1 or > 256) throw new InvalidDataException("目标调色板无效。");
        var pixels = ImageImportRasterNormalization.ReadArgbPixels(image);
        var indexes = new byte[checked(image.Width * image.Height)];
        var cache = new Dictionary<int, byte>();
        for (var index = 0; index < indexes.Length; index++)
        {
            if (index % 1024 == 0) token.ThrowIfCancellationRequested();
            var at = index * 4;
            // Transparency wins even when the old slot happens to contain the same RGB.
            if (pixels[at + 3] == 0) { indexes[index] = 0; continue; }
            var b = pixels[at]; var g = pixels[at + 1]; var r = pixels[at + 2];
            var key = r << 16 | g << 8 | b;
            if (!cache.TryGetValue(key, out var best))
            {
                var distance = int.MaxValue;
                for (var p = 0; p < palette.Count; p++)
                {
                    var color = palette[p];
                    var dr = r - color.R; var dg = g - color.G; var db = b - color.B;
                    var candidate = dr * dr + dg * dg + db * db;
                    if (candidate >= distance) continue;
                    distance = candidate; best = (byte)p;
                    if (candidate == 0) break;
                }
                cache[key] = best;
            }
            indexes[index] = best;
        }
        return indexes;
    }

    private static void RequireSize(Bitmap image, int width, int height)
    {
        if (image.Width != width || image.Height != height)
            throw new InvalidDataException($"转换画布应为{width}×{height}，实际{image.Width}×{image.Height}。");
    }
}
