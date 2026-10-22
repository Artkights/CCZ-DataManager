using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ImageManager.Core;

/// <summary>Face entries can be standard images or the legacy 64×80 RAW8 portrait plane.</summary>
public static class FacePayloadService
{
    public const int LegacyWidth = 64;
    public const int LegacyHeight = 80;

    public static Size GetSize(byte[] payload)
    {
        if (ImageRendering.IsStandardImage(payload))
        {
            var bmp = BmpPayloadService.ReadLayout(payload);
            if (bmp != null) return new Size(bmp.Width, bmp.Height);
            using var image = BmpPayloadService.Decode(payload);
            return image.Size;
        }
        // 东吴霸王传: Face.e5 load at VA 0x4171D0 -> 0x432D50 -> 0x432DC8;
        // 0x432ED3/0x432ED5 and 0x432F56/0x432F58 push height 0x50 and width 0x40.
        // Its Face.e5 entries contain 5,120-byte portraits.
        // Only this established portrait geometry (and the common two-byte suffix) is accepted.
        if (payload.Length is LegacyWidth * LegacyHeight or LegacyWidth * LegacyHeight + 2)
            return new Size(LegacyWidth, LegacyHeight);
        throw new InvalidDataException($"Face 条目长 {payload.Length} 字节，既不是标准图片，也不是已确认的 64×80 RAW8 头像。");
    }

    public static Bitmap Decode(ProjectProfile project, byte[] payload)
    {
        var size = GetSize(payload);
        return ImageRendering.IsStandardImage(payload) ? BmpPayloadService.Decode(payload)
            : ImageRendering.DecodeIndexed(payload, size.Width, size.Height, new PaletteService().Load(project));
    }

    public static byte[] Rebuild(ProjectProfile project, byte[] original, Bitmap edited)
    {
        var size = GetSize(original);
        using var fitted = FitPortrait(edited, size);
        return ImageRendering.IsStandardImage(original) ? BmpPayloadService.Rebuild(original, fitted)
            : BmpPayloadService.RebuildRaw8(original, size.Width, size.Height, fitted,
                new PaletteService().RequireWritePalette(project));
    }

    private static Bitmap FitPortrait(Bitmap image, Size target)
    {
        if (image.Size == target) return new Bitmap(image);
        var scale = Math.Max((double)target.Width / image.Width, (double)target.Height / image.Height);
        var sourceWidth = target.Width / scale;
        var sourceHeight = target.Height / scale;
        var source = new RectangleF((float)((image.Width - sourceWidth) / 2),
            (float)((image.Height - sourceHeight) / 2), (float)sourceWidth, (float)sourceHeight);
        var bitmap = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(image, new Rectangle(Point.Empty, target), source, GraphicsUnit.Pixel);
        return bitmap;
    }
}
