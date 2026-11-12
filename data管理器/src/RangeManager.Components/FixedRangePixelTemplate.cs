using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace RangeManager.App;

/// <summary>Immutable source artwork; no game directory is involved in material selection.</summary>
public static class FixedRangePixelTemplate
{
    public const string SourceId = "Signal-Effarea10";
    public const string SourceArchiveSha256 = "BE79C7364350E3E3B2B84576D2F0686A4518BF23650D3598C19C704FE6328D83";
    public const string SourceEntrySha256 = "043739505A4B2577FBED71D6C1504F9B3481E7E3F5A36F69663CF6E71DD909A5";
    private const string Prefix = "RangeManager.App.Assets.Range.";

    public static RangePixelTiles Create()
    {
        var allocated = new List<Bitmap>();
        try
        {
            using var manifestStream = Open("SignalEffarea10Manifest.json");
            using var manifest = JsonDocument.Parse(manifestStream);
            var files = manifest.RootElement.GetProperty("files");
            Bitmap Read(string key, int size)
            {
                var file = files.GetProperty(key);
                string name = file.GetProperty("file").GetString()!;
                using var stream = Open(name);
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                byte[] payload = bytes.ToArray();
                string hash = Convert.ToHexString(SHA256.HashData(payload));
                if (!hash.Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"固定范围素材校验失败：{name}");
                using var input = new MemoryStream(payload, writable: false);
                using var source = new Bitmap(input);
                if (source.Width != size || source.Height != size)
                    throw new InvalidDataException($"固定范围素材尺寸错误：{name}，应为 {size}×{size}");
                // Bitmap(Image) redraws through GDI+ and can round the RGB
                // channels of translucent border pixels. Copy straight ARGB
                // values explicitly, with no blending or premultiplication.
                var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                allocated.Add(result);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++) result.SetPixel(x, y, source.GetPixel(x, y));
                return result;
            }
            return new RangePixelTiles(Read("background", 8), Read("own", 8),
                Read("attack", 8), Read("pierce", 8), Read("frame", 64));
        }
        catch
        {
            foreach (var image in allocated) image.Dispose();
            throw;
        }
    }

    private static Stream Open(string file) => typeof(FixedRangePixelTemplate).Assembly
        .GetManifestResourceStream(Prefix + file)
        ?? throw new InvalidDataException($"固定范围素材缺失：{file}");
}
