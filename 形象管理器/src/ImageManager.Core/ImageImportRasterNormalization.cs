using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace ImageManager.Core;

/// <summary>Raster preparation for external file imports. Pixel editing intentionally does not use these rules.</summary>
internal static class ImageImportRasterNormalization
{
    internal static readonly Color E5Key = Color.FromArgb(247, 0, 255);

    internal sealed record Source(byte[] Bytes, Bitmap Bitmap, string Format) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    internal static Source Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(path);
        var bitmap = ImageImportSourceDecoder.Decode(bytes, token);
        try
        {
            _ = ReadArgbPixels(bitmap);
            token.ThrowIfCancellationRequested();
            return new Source(bytes, bitmap, Format(bytes));
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static string Format(byte[] bytes) => ImageImportSourceDecoder.Format(bytes);

    internal static void ValidateStandardSource(byte[] bytes) => _ = StandardImageLength(bytes);

    internal static Bitmap DecodeArgb(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var image = Image.FromStream(stream, false, true);
        return CopyArgb((Bitmap)image);
    }

    internal static Bitmap Portrait(Bitmap source, Size target)
    {
        if (target.Width <= 0 || target.Height <= 0) throw new InvalidDataException("头像目标尺寸无效。");
        if (source.Size == target) return CopyArgb(source);
        var result = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.Clear(Color.Transparent);
        var crop = PortraitSource(source.Size, target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(Point.Empty, target),
            crop, GraphicsUnit.Pixel);
        return result;
    }

    internal static Rectangle PortraitCrop(Size source, Size target)
    {
        var crop = PortraitSource(source, target);
        return Rectangle.FromLTRB((int)Math.Floor(crop.Left), (int)Math.Floor(crop.Top),
            (int)Math.Ceiling(crop.Right), (int)Math.Ceiling(crop.Bottom));
    }

    private static RectangleF PortraitSource(Size source, Size target)
    {
        if (target.Width == target.Height)
        {
            var side = Math.Min(source.Width, source.Height);
            return new RectangleF((source.Width - side) / 2, (source.Height - side) / 2, side, side);
        }
        var scale = Math.Max(target.Width / (double)source.Width, target.Height / (double)source.Height);
        var width = target.Width / scale; var height = target.Height / scale;
        return new RectangleF((float)((source.Width - width) / 2), (float)((source.Height - height) / 2), (float)width, (float)height);
    }

    internal static Rectangle IconCrop(Bitmap source, Size target, bool dllKeys, CancellationToken token)
    {
        if (source.Size == target) return new Rectangle(Point.Empty, source.Size);
        var minX = source.Width; var minY = source.Height; var maxX = -1; var maxY = -1;
        for (var y = 0; y < source.Height; y++)
        {
            if (y % 32 == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                if (pixel.A == 0 || IsMagenta(pixel) || dllKeys && pixel.R == 0 && pixel.G == 0 && pixel.B == 255) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        }
        return maxX < minX ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    internal static Bitmap Canvas(Bitmap source, Size target)
    {
        if (source.Size == target) return CopyArgb(source);
        var result = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(Point.Empty, target));
        return result;
    }

    internal static Bitmap CopyArgb(Bitmap source)
    {
        var target = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(Point.Empty, source.Size);
        BitmapData? input = null;
        try
        {
            input = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var output = target.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[checked(source.Width * 4)];
                for (var y = 0; y < source.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(input.Scan0, y * input.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(output.Scan0, y * output.Stride), row.Length);
                }
            }
            finally { target.UnlockBits(output); }
            return target;
        }
        catch { target.Dispose(); throw; }
        finally { if (input != null) source.UnlockBits(input); }
    }

    internal static byte[] ReadArgbPixels(Bitmap source)
    {
        var data = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[checked(source.Width * source.Height * 4)];
            for (var y = 0; y < source.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * source.Width * 4, source.Width * 4);
            return pixels;
        }
        finally { source.UnlockBits(data); }
    }

    internal static Bitmap Icon(Bitmap source, Size target, bool dllKeys, CancellationToken token)
    {
        using var keyed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var minX = source.Width; var minY = source.Height; var maxX = -1; var maxY = -1;
        for (var y = 0; y < source.Height; y++)
        {
            if (y % 32 == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var transparent = pixel.A == 0 || IsMagenta(pixel) || dllKeys && pixel.R == 0 && pixel.G == 0 && pixel.B == 255;
                keyed.SetPixel(x, y, transparent ? Color.Transparent : pixel);
                if (transparent) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        }
        if (source.Size == target) return CopyArgb(keyed);
        var output = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.SmoothingMode = SmoothingMode.None;
        if (maxX >= minX && maxY >= minY)
        {
            var crop = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            var scale = Math.Min(target.Width / (double)crop.Width, target.Height / (double)crop.Height);
            var width = Math.Clamp((int)Math.Round(crop.Width * scale), 1, target.Width);
            var height = Math.Clamp((int)Math.Round(crop.Height * scale), 1, target.Height);
            graphics.DrawImage(keyed, new Rectangle((target.Width - width) / 2, (target.Height - height) / 2, width, height), crop, GraphicsUnit.Pixel);
        }
        return output;
    }

    internal static Bitmap SmallIcon(Bitmap large, Size target, bool hardTransparency)
    {
        if (!hardTransparency || large.Width != target.Width * 2 || large.Height != target.Height * 2)
            return Canvas(large, target);
        var result = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppArgb);
        ReadOnlySpan<Point> priority = [new(1, 1), new(1, 0), new(0, 1), new(0, 0)];
        for (var y = 0; y < target.Height; y++)
        for (var x = 0; x < target.Width; x++)
        {
            var pixel = Color.Transparent;
            foreach (var offset in priority)
            {
                var candidate = large.GetPixel(x * 2 + offset.X, y * 2 + offset.Y);
                if (candidate.A == 0) continue;
                pixel = candidate; break;
            }
            result.SetPixel(x, y, pixel);
        }
        return result;
    }

    internal static byte[] Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    internal static byte[] E5Icon(Bitmap bitmap)
    {
        using var flattened = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            flattened.SetPixel(x, y, pixel.A == 0 || IsMagenta(pixel) ? E5Key : Color.FromArgb(pixel.R, pixel.G, pixel.B));
        }
        using var stream = new MemoryStream(); flattened.Save(stream, ImageFormat.Bmp); return stream.ToArray();
    }

    internal static bool IsMagenta(Color pixel)
        => pixel.R >= 210 && pixel.B >= 210 && pixel.G <= 90 && Math.Abs(pixel.R - pixel.B) <= 70;

    internal static void RequireNoUnknownTail(byte[] bytes)
    {
        var length = StandardImageLength(bytes);
        if (length != bytes.Length)
            throw new InvalidDataException($"标准图片后带有 {bytes.Length - length} 字节未知尾部，文件导入不会丢弃或解释该专用结构；请使用对应专用入口。");
    }

    private static int StandardImageLength(byte[] bytes)
    {
        // A complete standard BMP can use a compression or bit depth that the
        // conservative pixel editor does not rewrite. File imports preserve it
        // verbatim; use its validated file boundary rather than its pixel layout.
        var length = bytes.Length >= 14 && bytes[0] == 'B' && bytes[1] == 'M'
            ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(2)))
            : BmpPayloadService.GetStandardImageLength(bytes);
        if (length < 14 || length > bytes.Length)
            throw new InvalidDataException("标准图片边界不完整，不能确认是否带有专用尾部。");
        return length;
    }
}
