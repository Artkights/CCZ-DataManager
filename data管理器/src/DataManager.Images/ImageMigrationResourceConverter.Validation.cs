using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DataManager.Core.Migration;
using ImageManager.Core;

namespace DataManager.Images;

public sealed partial class ImageMigrationResourceConverter
{
    private static readonly Regex MapFile = new(@"^M(?<id>\d{3})\.(jpg|jpeg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex McallFile = new(@"^Mcall\d+\.e5$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> BitmapLibraries = new(StringComparer.OrdinalIgnoreCase)
        { "Tou.dll", "Itemicon.dll", "Mgcicon.dll", "Cmdicon.dll" };
    private static readonly HashSet<string> StandardImageArchives = new(StringComparer.OrdinalIgnoreCase)
        { "Item.e5", "Mtem.e5", "Mmap.e5", "Tr.e5", "Gate.e5", "Hitarea.e5", "Effarea.e5", "U_select.e5", "Fb.e5", "Meff.e5" };

    /// <summary>Reads only candidate bytes and explicitly tracked target dependencies; it never modifies either project.</summary>
    public MigrationResourceVerificationResult ValidateFile(string stagingRoot, string targetRoot, string relativePath,
        byte[] candidate, CancellationToken cancellationToken = default)
    {
        var context = new ResourceValidationContext(stagingRoot, targetRoot, relativePath, candidate, cancellationToken);
        var name = Path.GetFileName(relativePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (BitmapLibraries.Contains(name))
                return context.Result(true, ValidateBitmapLibrary(candidate, cancellationToken));
            if (MapFile.Match(name) is { Success: true } map &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(relativePath)), "Map", StringComparison.OrdinalIgnoreCase))
            {
                var dimensions = ValidateStandardRaster(candidate, null);
                var terrain = context.FindResource("Hexzmap.e5") ?? throw new InvalidDataException("地图缺少目标 Hexzmap.e5 配对依赖。");
                var archive = E5Archive.Parse(terrain.RelativePath, terrain.Bytes);
                ValidateMapPair(archive, int.Parse(map.Groups["id"].Value), dimensions, relativePath);
                return context.Result(true, $"地图实际图像与 Hexzmap 地形槽配对通过：{dimensions.Width}×{dimensions.Height}，48像素网格。");
            }
            if (McallFile.IsMatch(name))
            {
                var diagnostics = StrategyAnimationPackageCodec.Probe(candidate);
                using var animation = new StrategyAnimationPackageCodec().Decode(context.AnimationProfile(), candidate);
                foreach (var plane in animation.PlaneFrames) { cancellationToken.ThrowIfCancellationRequested(); _ = BmpPayloadService.ReadPixels(plane); }
                return context.Result(true, "Mcall记录、平面、播放引用及实际图像解析通过；" + diagnostics);
            }
            if (!Path.GetExtension(name).Equals(".e5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Mark.e5", StringComparison.OrdinalIgnoreCase))
                return context.Result(false, "此类别没有已确认的完整语义验证器；仅由迁移事务核对候选SHA，不宣称格式验证通过。");

            var known = name.Equals("Pmapobj.e5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Unit_mov.e5", StringComparison.OrdinalIgnoreCase) || name.Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Unit_spc.e5", StringComparison.OrdinalIgnoreCase) || name.Equals("Face.e5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Pmap.e5", StringComparison.OrdinalIgnoreCase) || name.Equals("Weather.e5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DT.e5", StringComparison.OrdinalIgnoreCase) || name.Equals("Hexzmap.e5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Logo.e5", StringComparison.OrdinalIgnoreCase) || StandardImageArchives.Contains(name);
            // Arbitrary .e5 files include raw data and encrypted/custom containers. Do not guess their format.
            if (!known) return context.Result(false, "此E5类别的目录及载荷语义尚未确认，未将其当作通用图片容器验证。");
            var e5 = E5Archive.Parse(relativePath, candidate);
            var allSemantic = true;
            foreach (var entry in e5.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = e5.ReadDecoded(entry.ImageNumber);
                if (payload.Length != entry.DecodedLength) throw new InvalidDataException($"图号{entry.ImageNumber}解压后长度不符。");
                try
                {
                    if (name.Equals("Pmapobj.e5", StringComparison.OrdinalIgnoreCase)) ValidateStrip(payload, 48, 1280);
                    else if (name.Equals("Unit_mov.e5", StringComparison.OrdinalIgnoreCase)) ValidateStrip(payload, 48, 528);
                    else if (name.Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase)) ValidateStrip(payload, 64, 768);
                    else if (name.Equals("Unit_spc.e5", StringComparison.OrdinalIgnoreCase)) ValidateStrip(payload, 48, 240);
                    else if (name.Equals("Face.e5", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = FacePayloadService.GetSize(payload);
                        if (ImageRendering.IsStandardImage(payload)) ValidateStandardRaster(payload, null);
                    }
                    else if (name.Equals("Pmap.e5", StringComparison.OrdinalIgnoreCase)) ValidatePmap(payload);
                    else if (name.Equals("Weather.e5", StringComparison.OrdinalIgnoreCase))
                    {
                        if (payload.Length != WeatherResourceCodec.FrameWidth * WeatherResourceCodec.FrameHeight * WeatherResourceCodec.FrameCount)
                            throw new InvalidDataException("天气条带应为216×50×4，共43200字节。");
                    }
                    else if (name.Equals("DT.e5", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = StrategyAnimationPackageCodec.Probe(payload);
                        using var animation = new StrategyAnimationPackageCodec().Decode(context.AnimationProfile(), payload);
                        foreach (var plane in animation.PlaneFrames) { cancellationToken.ThrowIfCancellationRequested(); _ = BmpPayloadService.ReadPixels(plane); }
                    }
                    else if (name.Equals("Hexzmap.e5", StringComparison.OrdinalIgnoreCase))
                    {
                        if (payload.Length < 3) throw new InvalidDataException("地形条目缺少两字节前缀或网格数据。");
                    }
                    else if (name.Equals("Logo.e5", StringComparison.OrdinalIgnoreCase) && !ImageRendering.IsStandardImage(payload))
                    {
                        if (payload.Length != 48 * 48) allSemantic = false;
                    }
                    else if (ImageRendering.IsStandardImage(payload)) ValidateStandardRaster(payload, null);
                    else allSemantic = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { throw new InvalidDataException($"{name} 图号{entry.ImageNumber}：{ex.Message}", ex); }
            }
            if (name.Equals("Hexzmap.e5", StringComparison.OrdinalIgnoreCase))
            {
                var maps = context.ReadMaps();
                foreach (var item in maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateMapPair(e5, int.Parse(MapFile.Match(Path.GetFileName(item.RelativePath)).Groups["id"].Value),
                        ValidateStandardRaster(item.Bytes, null), item.RelativePath);
                }
                allSemantic = maps.Count > 0;
                return context.Result(allSemantic, allSemantic
                    ? $"E5目录、全部条目解压及{maps.Count}张地图的地形网格配对通过。"
                    : "E5目录和全部条目解压通过；没有目标Map地图可核对网格，未宣称完整语义验证。");
            }
            return context.Result(allSemantic, allSemantic
                ? $"E5目录及全部{e5.Entries.Count}条载荷按已知类别解析通过。"
                : $"E5目录及全部{e5.Entries.Count}条载荷解压通过；含未确认的RAW/专用载荷，未宣称完整图像语义验证。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidDataException)
        { throw new InvalidDataException($"资源候选验证失败：{relativePath}：{ex.Message}", ex); }
    }

    private static void ValidateStrip(byte[] payload, int width, int height)
    {
        if (ImageRendering.IsStandardImage(payload)) ValidateStandardRaster(payload, new Size(width, height));
        else if (payload.Length != width * height && payload.Length != width * height + 2)
            throw new InvalidDataException($"动作条应为{width}×{height} RAW8（可保留两字节尾部）或同尺寸标准图片，实际{payload.Length}字节。");
    }

    private static Size ValidateStandardRaster(byte[] payload, Size? expected)
    {
        if (!ImageRendering.IsStandardImage(payload)) throw new InvalidDataException("未识别为BMP、PNG或JPEG，禁止猜测图片解码。");
        _ = BmpPayloadService.GetStandardImageLength(payload);
        using var image = ImageRendering.DecodeStandardImage(payload);
        if (expected.HasValue && image.Size != expected.Value)
            throw new InvalidDataException($"实际尺寸{image.Width}×{image.Height}与要求{expected.Value.Width}×{expected.Value.Height}不符。");
        _ = BmpPayloadService.ReadPixels(image);
        return image.Size;
    }

    private static void ValidatePmap(byte[] payload)
    {
        if (payload.Length < 17) throw new InvalidDataException("地形图片不足16字节头和像素数据。");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10, 2));
        if (width == 0 || height == 0 || 16L + (long)width * height != payload.Length)
            throw new InvalidDataException("地形图片头部尺寸与RAW8平面长度不一致。");
    }

    private static void ValidateMapPair(E5Archive terrain, int id, Size dimensions, string map)
    {
        if (dimensions.Width % 48 != 0 || dimensions.Height % 48 != 0) throw new InvalidDataException($"{map}宽高不能被48整除。");
        var expected = checked(2 + dimensions.Width / 48 * (dimensions.Height / 48));
        if (id + 1 > terrain.Entries.Count) throw new InvalidDataException($"Hexzmap缺少{map}对应的图号{id + 1}。");
        if (terrain.ReadDecoded(id + 1).Length != expected) throw new InvalidDataException($"{map}对应地形长度应为{expected}字节。");
    }

    private static string ValidateBitmapLibrary(byte[] candidate, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "DataManagerBitmapValidation_" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(temporary, candidate);
            var entries = DllBitmapArchive.Read(temporary, requireAllBitmapRecords: true);
            if (entries.Count == 0) throw new InvalidDataException("已知图片DLL没有可解析的数值RT_BITMAP资源。");
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var image = DllBitmapArchive.Decode(entry);
                if (image.Width != entry.Width || image.Height != entry.Height) throw new InvalidDataException($"RT_BITMAP ID={entry.Id}头部尺寸不符。");
                _ = BmpPayloadService.ReadPixels(image);
            }
            return $"已知图片DLL的{entries.Count}个数值RT_BITMAP记录（含语言变体）实际解码通过；其他PE资源保持原样。";
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class ResourceValidationContext(string stagingRoot, string targetRoot, string relativePath, byte[] candidate,
        CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, string> _dependencies = new(StringComparer.OrdinalIgnoreCase);
        private ProjectProfile? _animationProfile;

        public MigrationResourceVerificationResult Result(bool verified, string detail)
            => new(verified, detail, new Dictionary<string, string>(_dependencies, StringComparer.OrdinalIgnoreCase));

        public ResourceDependency? FindResource(string name)
        {
            var parent = Path.GetDirectoryName(relativePath);
            var candidates = new[] { !string.IsNullOrEmpty(parent) && !Path.GetFileName(parent).Equals("Map", StringComparison.OrdinalIgnoreCase) ? Path.Combine(parent, name) : null,
                Path.Combine("E5", name), name }.Where(path => path != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var root in new[] { stagingRoot, targetRoot })
            foreach (var relative in candidates)
                if (File.Exists(Path.Combine(root, relative))) return Read(root, relative);
            return null;
        }

        public IReadOnlyList<ResourceDependency> ReadMaps()
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { targetRoot, stagingRoot })
            {
                var directory = Path.Combine(root, "Map");
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Where(path => MapFile.IsMatch(Path.GetFileName(path))))
                    paths[Path.GetRelativePath(root, path)] = root;
            }
            return paths.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => Read(pair.Value, pair.Key)).ToArray();
        }

        public ProjectProfile AnimationProfile()
        {
            if (_animationProfile != null) return _animationProfile;
            // RAW8 indexes are valid independently of the project's color choices.
            // Use the codec's bundled 256-color palette for structural decode, so a
            // second path read cannot silently replace an already hashed dependency.
            var emptyRoot = Path.Combine(Path.GetTempPath(), "DataManagerValidationPalette_" + Guid.NewGuid().ToString("N"));
            return _animationProfile = new ProjectProfile(emptyRoot, "迁移候选结构验证", ProjectVersion.Unknown, true,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);
        }

        private ResourceDependency Read(string root, string relative)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFullPath(root).Equals(Path.GetFullPath(stagingRoot), StringComparison.OrdinalIgnoreCase) &&
                relative.Equals(relativePath, StringComparison.OrdinalIgnoreCase)) return new(root, relative, candidate);
            using var stream = new FileStream(Path.Combine(root, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new MemoryStream(); stream.CopyTo(output);
            var bytes = output.ToArray();
            if (!Path.GetFullPath(root).Equals(Path.GetFullPath(stagingRoot), StringComparison.OrdinalIgnoreCase))
                _dependencies[relative] = System.Convert.ToHexString(SHA256.HashData(bytes));
            return new(root, relative, bytes);
        }
    }

    private sealed record ResourceDependency(string Root, string RelativePath, byte[] Bytes);
}
