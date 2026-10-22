using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using RasterImage = SixLabors.ImageSharp.Image;

namespace ImageManager.Core;

/// <summary>File-import decoder only; game payload decoding and pixel editing keep their native codecs.</summary>
public static class ImageImportSourceDecoder
{
    public const string SupportedExtensions = ".bmp;.png;.jpg;.jpeg;.gif;.tif;.tiff;.ico;.webp";
    public const string FileDialogFilter = "图片素材|*.bmp;*.png;*.jpg;*.jpeg;*.gif;*.tif;*.tiff;*.ico;*.webp|所有文件|*.*";
    private static readonly DecoderOptions FirstFrame = new() { MaxFrames = 1 };

    public static Bitmap Decode(byte[] bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (IsIcon(bytes)) return DecodeIcon(bytes, token);
            var info = Identify(bytes);
            // Preserve the existing standard-image pixel semantics, including BMP alpha.
            if (ImageRendering.IsStandardImage(bytes))
            {
                ImageImportRasterNormalization.ValidateStandardSource(bytes);
                using var stream = new MemoryStream(bytes, false);
                using var original = System.Drawing.Image.FromStream(stream, false, true);
                var result = ImageImportRasterNormalization.CopyArgb((Bitmap)original);
                if (token.IsCancellationRequested) { result.Dispose(); token.ThrowIfCancellationRequested(); }
                return result;
            }
            using var decoded = RasterImage.Load<Bgra32>(FirstFrame, bytes);
            token.ThrowIfCancellationRequested();
            if (decoded.Width <= 0 || decoded.Height <= 0 || (long)decoded.Width * decoded.Height > 64_000_000)
                throw new InvalidDataException("解码后的首帧尺寸无效或超过 6400 万像素。");
            var bitmap = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
                decoded.Frames.RootFrame.CopyPixelDataTo(pixels);
                var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < bitmap.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        Marshal.Copy(pixels, y * bitmap.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), bitmap.Width * 4);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { throw new InvalidDataException("素材不能完整解码。支持 BMP、PNG、JPG/JPEG、GIF、TIF/TIFF、ICO、WebP；请检查图片内容。" + ex.Message, ex); }
    }

    public static string Describe(byte[] bytes)
    {
        if (IsIcon(bytes))
        {
            _ = IconPayload(bytes);
            return $"ICO / {BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12))} 位（静态导入取第一幅）";
        }
        var info = Identify(bytes);
        var format = info.Metadata.DecodedImageFormat!.Name.ToUpperInvariant();
        return $"{format} / {info.PixelType.BitsPerPixel} 位" +
            (format is "GIF" or "TIFF" or "ICO" or "WEBP" ? "（静态导入取第一帧/页）" : "");
    }

    internal static string Format(byte[] bytes)
    {
        if (IsIcon(bytes)) { _ = IconPayload(bytes); return "ICO"; }
        var format = Identify(bytes).Metadata.DecodedImageFormat!.Name.ToUpperInvariant();
        return format == "JPEG" ? "JPG" : format;
    }

    private static bool IsIcon(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0;

    private static byte[] IconPayload(byte[] bytes)
    {
        if (bytes.Length < 22) throw new InvalidDataException("ICO 目录不完整。");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var tableEnd = checked(6 + count * 16);
        if (count == 0 || tableEnd > bytes.Length) throw new InvalidDataException("ICO 图片目录无效。");
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(6 + i * 16, 16);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (size == 0 || start < tableEnd || (long)start + size > bytes.Length)
                throw new InvalidDataException($"ICO 第 {i + 1} 幅图片数据不完整。");
        }
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14)));
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18)));
        return bytes.AsSpan(offset, length).ToArray();
    }

    private static Bitmap DecodeIcon(byte[] bytes, CancellationToken token)
    {
        var payload = IconPayload(bytes);
        if (payload.Length >= 8 && payload[0] == 137 && payload[1] == 80) return Decode(payload, token);
        var single = new byte[checked(22 + payload.Length)];
        bytes.AsSpan(0, 22).CopyTo(single);
        BinaryPrimitives.WriteUInt16LittleEndian(single.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(18), 22);
        payload.CopyTo(single, 22);
        using var stream = new MemoryStream(single, false);
        using var icon = new Icon(stream);
        using var bitmap = icon.ToBitmap();
        token.ThrowIfCancellationRequested();
        return ImageImportRasterNormalization.CopyArgb(bitmap);
    }

    private static SixLabors.ImageSharp.ImageInfo Identify(byte[] bytes)
    {
        var info = RasterImage.Identify(FirstFrame, bytes);
        var format = info.Metadata.DecodedImageFormat?.Name.ToUpperInvariant();
        if (format is not ("BMP" or "PNG" or "JPEG" or "JPG" or "GIF" or "TIFF" or "ICO" or "WEBP"))
            throw new InvalidDataException("不支持此素材的图片格式。");
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 64_000_000)
            throw new InvalidDataException("素材尺寸无效或超过 6400 万像素。");
        return info;
    }
}
