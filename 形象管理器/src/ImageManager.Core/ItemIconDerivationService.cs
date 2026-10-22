using System.Drawing;
using System.Drawing.Imaging;

namespace ImageManager.Core;

public interface IItemIconDerivationService
{
    Bitmap DeriveSmall(Bitmap large, Size targetSize, Color keyColor);
    byte[] EncodeStandardImageLike(byte[] original, Bitmap bitmap, Color keyColor);
}

public sealed class ItemIconDerivationService : IItemIconDerivationService
{
    public Bitmap DeriveSmall(Bitmap large, Size targetSize, Color keyColor)
    {
        if (targetSize.Width <= 0 || targetSize.Height <= 0 ||
            large.Width != targetSize.Width * 2 || large.Height != targetSize.Height * 2)
            throw new InvalidDataException($"宝物大图必须恰好是小图的两倍：大图 {large.Width}x{large.Height}，小图 {targetSize.Width}x{targetSize.Height}。");
        var result = new Bitmap(targetSize.Width, targetSize.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < targetSize.Height; y++)
        for (var x = 0; x < targetSize.Width; x++)
        {
            long alphaSum = 0;
            long red = 0;
            long green = 0;
            long blue = 0;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var color = large.GetPixel(x * 2 + dx, y * 2 + dy);
                var alpha = IsKey(color, keyColor) ? 0 : color.A;
                alphaSum += alpha;
                red += color.R * (long)alpha;
                green += color.G * (long)alpha;
                blue += color.B * (long)alpha;
            }
            if (alphaSum == 0)
            {
                result.SetPixel(x, y, Color.Transparent);
                continue;
            }
            var outputAlpha = (int)Math.Clamp((alphaSum + 2) / 4, 0, 255);
            result.SetPixel(x, y, Color.FromArgb(outputAlpha,
                (int)(red / alphaSum), (int)(green / alphaSum), (int)(blue / alphaSum)));
        }
        return result;
    }

    public byte[] EncodeStandardImageLike(byte[] original, Bitmap bitmap, Color keyColor)
    {
        if (!ImageRendering.IsStandardImage(original))
            throw new InvalidDataException("宝物图标不是可安全重建的 BMP/PNG 标准图片。");
        using var decoded = ImageRendering.DecodeStandardImage(original);
        if (decoded.Size != bitmap.Size)
            throw new InvalidDataException($"宝物图标尺寸必须为 {decoded.Width}x{decoded.Height}，实际为 {bitmap.Width}x{bitmap.Height}。");
        using var normalized = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            normalized.SetPixel(x, y, color.A < 128 ? keyColor : Color.FromArgb(color.R, color.G, color.B));
        }
        return BmpPayloadService.Rebuild(original, normalized);
    }

    public static Color DetectKeyColor(Bitmap bitmap, bool dll)
    {
        if (dll) return Color.Blue;
        var corners = new[]
        {
            bitmap.GetPixel(0, 0), bitmap.GetPixel(bitmap.Width - 1, 0),
            bitmap.GetPixel(0, bitmap.Height - 1), bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1)
        };
        var key = corners.GroupBy(color => color.ToArgb()).OrderByDescending(group => group.Count()).First();
        return Color.FromArgb(key.Key);
    }

    private static bool IsKey(Color color, Color key)
        => color.A == 0 || color.R == key.R && color.G == key.G && color.B == key.B;
}
