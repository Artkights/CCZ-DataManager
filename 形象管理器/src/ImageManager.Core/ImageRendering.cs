using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace ImageManager.Core;

public static class ImageRendering
{
    public static Bitmap DecodeStandardImage(byte[] bytes)
    {
        return BmpPayloadService.Decode(bytes);
    }

    public static bool IsStandardImage(byte[] bytes)
        => bytes.Length >= 2 && ((bytes[0] == (byte)'B' && bytes[1] == (byte)'M') ||
                                (bytes[0] == 0xFF && bytes[1] == 0xD8) ||
                                (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })));

    public static Bitmap DecodeIndexed(byte[] bytes, int width, int height, IReadOnlyList<Color> palette)
    {
        var pixelLength = checked(width * height);
        if (bytes.Length < pixelLength) throw new InvalidDataException($"RAW 像素不足：{bytes.Length}/{pixelLength}。");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var output = new byte[checked(data.Stride * height)];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = palette[bytes[y * width + x]];
                var target = y * data.Stride + x * 4;
                output[target] = color.B;
                output[target + 1] = color.G;
                output[target + 2] = color.R;
                output[target + 3] = 255;
            }
            Marshal.Copy(output, 0, data.Scan0, output.Length);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    public static Bitmap Crop(Bitmap strip, Rectangle source)
    {
        if (source.X < 0 || source.Y < 0 || source.Right > strip.Width || source.Bottom > strip.Height)
            throw new ArgumentOutOfRangeException(nameof(source));
        var frame = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.DrawImage(strip, new Rectangle(0, 0, frame.Width, frame.Height), source, GraphicsUnit.Pixel);
        return frame;
    }

    public static Bitmap ApplyKeyTransparency(Bitmap source, bool showBackground, bool dllBlue = false)
    {
        if (showBackground) return new Bitmap(source);
        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        // Avoid a GDI+ draw round trip for ARGB pixels: premultiplication can round RGB values.
        var pixels = source.PixelFormat == PixelFormat.Format32bppArgb
            ? ImageImportRasterNormalization.ReadArgbPixels(source) : BmpPayloadService.ReadPixels(source);
        for (var at = 0; at < pixels.Length; at += 4)
        {
            var key = dllBlue ? pixels[at + 2] == 0 && pixels[at + 1] == 0 && pixels[at] == 255 :
                pixels[at + 1] == 0 && pixels[at] == 255 && pixels[at + 2] is 247 or 255;
            if (key) pixels[at + 3] = 0;
        }
        var data = output.LockBits(new Rectangle(0, 0, output.Width, output.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        { for (var y = 0; y < output.Height; y++) Marshal.Copy(pixels, y * output.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), output.Width * 4); }
        finally { output.UnlockBits(data); }
        return output;
    }

    public static bool TryExtract8BppBmpIndexes(byte[] bytes, int expectedWidth, int expectedHeight, out byte[] indexes)
    {
        indexes = Array.Empty<byte>();
        var layout = BmpPayloadService.ReadLayout(bytes);
        return layout is { Bits: 8 } && layout.Width == expectedWidth && layout.Height == expectedHeight &&
               BmpPayloadService.TryGetIndexes(bytes, out indexes);
    }

    public static bool TryReplace8BppBmpIndexes(byte[] original, int width, int height, byte[] indexes, out byte[] rebuilt)
    {
        rebuilt = Array.Empty<byte>();
        if (indexes.Length != width * height || !TryExtract8BppBmpIndexes(original, width, height, out _)) return false;
        rebuilt = (byte[])original.Clone();
        BmpPayloadService.WriteIndexes(rebuilt, BmpPayloadService.ReadLayout(original)!, indexes);
        return true;
    }

    public static bool TryExtract8BppBmpPalette(byte[] bytes, out byte[] paletteBgra)
    {
        paletteBgra = Array.Empty<byte>();
        var layout = BmpPayloadService.ReadLayout(bytes);
        if (layout is not { Bits: 8 }) return false;
        paletteBgra = BuildPaletteBgra(BmpPayloadService.TryGetEmbeddedPalette(bytes)!);
        return true;
    }

    public static byte[] BuildPaletteBgra(IReadOnlyList<Color> palette)
    {
        var bytes = new byte[256 * 4];
        for (var index = 0; index < Math.Min(256, palette.Count); index++)
        {
            bytes[index * 4] = palette[index].B;
            bytes[index * 4 + 1] = palette[index].G;
            bytes[index * 4 + 2] = palette[index].R;
            bytes[index * 4 + 3] = palette[index].A;
        }
        return bytes;
    }
}
