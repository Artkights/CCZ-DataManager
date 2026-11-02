using System.Drawing;
using System.Security.Cryptography;

namespace ImageManager.Core;

/// <summary>The physical frame and independently captured pixels that an editor intends to save.</summary>
public sealed record MutationImageExpectation(
    FrameDescriptor Target, ResourceKind Kind, string PixelSha256, bool IsAnimationPlane = false,
    Color? TransparentColor = null)
{
    public static MutationImageExpectation FromBitmap(FrameDescriptor target, ResourceKind kind, Bitmap bitmap,
        bool isAnimationPlane = false, Color? transparentColor = null)
    {
        if (bitmap.Width != target.Width || bitmap.Height != target.Height)
            throw new InvalidDataException("预期像素尺寸与物理目标不一致。");
        return new(target, kind, HashPixels(bitmap,
                Path.GetExtension(target.SourcePath).Equals(".dll", StringComparison.OrdinalIgnoreCase), transparentColor),
            isAnimationPlane, transparentColor);
    }

    internal static string HashPixels(Bitmap bitmap, bool blueKey = false, Color? transparentColor = null)
    {
        var pixels = ImageImportRasterNormalization.ReadArgbPixels(bitmap);
        for (var at = 0; at < pixels.Length; at += 4)
        {
            var key = blueKey ? Color.Blue : transparentColor;
            var keyed = key.HasValue && pixels[at] == key.Value.B && pixels[at + 1] == key.Value.G && pixels[at + 2] == key.Value.R;
            if (pixels[at + 3] == 0 || keyed) pixels.AsSpan(at, 4).Clear();
        }
        return bitmap.Width + "x" + bitmap.Height + ":" + Convert.ToHexString(SHA256.HashData(pixels));
    }
}
