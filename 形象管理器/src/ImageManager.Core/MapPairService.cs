using System.Buffers.Binary;
using System.Drawing;
using System.Text.RegularExpressions;

namespace ImageManager.Core;

public sealed class MapPairResourceCodec : IResourceCodec
{
    private static readonly Regex MapName = new(@"^M(?<id>\d{3})\.(?:jpg|jpeg)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public ResourceKind Kind => ResourceKind.MapPair;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        => resource.Paths.Select(path => (Path: path, Match: MapName.Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success)
            .Select(item =>
            {
                var id = int.Parse(item.Match.Groups["id"].Value);
                var frame = new FrameDescriptor("MapJpeg", item.Path, id, 0, 1, "battle-map", "none", 0, 0);
                return new LogicalImageAsset(resource.Key, id, $"M{id:000}", ImageCategory.Scene, Kind, [frame],
                    ["Hexzmap.e5 地形块 #" + id], false, false, "JPG + Hexzmap 地形层");
            }).OrderBy(asset => asset.LogicalId).ToArray();

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => [new DecodedFrame(asset.Frames.Single(), new Bitmap(asset.Frames.Single().SourcePath))];
}

public sealed class MapPairService
{
    private const int TerrainPrefixLength = 2;
    private const int BmpPixelOffset = 14 + 40 + 256 * 4;

    public MutationPlan PlanImport(ProjectProfile project, LogicalImageAsset asset, string folder)
        => MutationPlanningSnapshot.Build(asset.Frames.Select(frame => frame.SourcePath)
                .Concat(project.TryGetResource("Hexzmap.e5") is { } hex ? [hex] : Array.Empty<string>()),
            () => PlanImportCore(project, asset, folder));

    private MutationPlan PlanImportCore(ProjectProfile project, LogicalImageAsset asset, string folder)
    {
        if (project.IsReadOnly) throw new InvalidOperationException("当前项目为只读，不能导入地图。");
        if (asset.Kind != ResourceKind.MapPair) throw new InvalidOperationException("目标不是战场地图槽。");
        var mapId = $"M{asset.LogicalId:000}";
        var imagePath = FindMapImage(folder, mapId);
        var bmp = Find(folder, mapId, [".bmp"]);
        var sources = MutationPlanningSnapshot.Capture([imagePath, bmp]);
        var terrain = DecodeTerrainBmp(bmp);
        using var importImage = ImageImportRasterNormalization.Load(imagePath, CancellationToken.None);
        var targetBytes = File.ReadAllBytes(asset.Frames.Single().SourcePath);
        ImageImportRasterNormalization.RequireNoUnknownTail(targetBytes);
        using var targetImage = ImageImportRasterNormalization.DecodeArgb(targetBytes);
        SpecialImageFileImport.ValidateMapSlot(project, asset, targetImage.Size);
        using var normalized = SpecialImageFileImport.FitMap(importImage.Bitmap, targetImage.Size);
        var jpeg = SpecialImageFileImport.Jpeg(normalized);
        using var verified = ImageImportRasterNormalization.DecodeArgb(jpeg);
        if (verified.Size != targetImage.Size) throw new InvalidDataException("地图JPG候选复读尺寸与原画布不一致。");
        var gridWidth = targetImage.Width / 48;
        var gridHeight = targetImage.Height / 48;
        if (terrain.Width != gridWidth || terrain.Height != gridHeight || terrain.Cells.Length != gridWidth * gridHeight)
            throw new InvalidDataException($"地形 BMP 网格 {terrain.Width}x{terrain.Height} 与目标地图网格 {gridWidth}x{gridHeight} 不一致。");

        var hexPath = project.TryGetResource("Hexzmap.e5") ?? throw new FileNotFoundException("缺少 Hexzmap.e5。");
        var archive = E5Archive.OpenFresh(hexPath);
        var imageNumber = asset.LogicalId + 1;
        if (imageNumber > archive.Entries.Count) throw new InvalidDataException($"Hexzmap.e5 缺少地图 {mapId} 的目录项。");
        var decoded = archive.ReadDecoded(imageNumber);
        if (decoded.Length != TerrainPrefixLength + terrain.Cells.Length)
            throw new InvalidDataException($"Hexzmap {mapId} 条目长度 {decoded.Length} 与 2+{terrain.Cells.Length} 不一致，拒绝猜测写入。");
        var replacement = new byte[decoded.Length];
        Buffer.BlockCopy(decoded, 0, replacement, 0, TerrainPrefixLength);
        Buffer.BlockCopy(terrain.Cells, 0, replacement, TerrainPrefixLength, terrain.Cells.Length);
        var hexCandidate = archive.Rebuild(replacementDecoded: new Dictionary<int, byte[]> { [imageNumber] = replacement });
        return sources.Complete(new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Replace, $"导入战场地图对 {mapId}",
            [new MutationFile(asset.Frames.Single().SourcePath, jpeg, "适配原地图尺寸并编码JPG"),
                new MutationFile(hexPath, hexCandidate, "替换 Hexzmap 地形格")],
            ["底图自动缩放至原画布并编码JPG；地形BMP仍须符合原地图网格，两层作为同一事务提交。"], false,
            new Dictionary<string, string> { ["MapId"] = mapId, ["Grid"] = $"{gridWidth}x{gridHeight}",
                ["SourceImage"] = imagePath, ["SourceFormat"] = importImage.Format }));
    }

    public (string JpegPath, string BmpPath) Export(ProjectProfile project, LogicalImageAsset asset, string outputFolder)
    {
        if (asset.Kind != ResourceKind.MapPair) throw new InvalidOperationException("目标不是战场地图槽。");
        Directory.CreateDirectory(outputFolder);
        var mapId = $"M{asset.LogicalId:000}";
        var jpegPath = Path.Combine(outputFolder, mapId + ".JPG");
        File.Copy(asset.Frames.Single().SourcePath, jpegPath, true);
        using var image = new Bitmap(jpegPath);
        if (image.Width % 48 != 0 || image.Height % 48 != 0) throw new InvalidDataException("地图尺寸不能被 48 整除。");
        var width = image.Width / 48;
        var height = image.Height / 48;
        var hexPath = project.TryGetResource("Hexzmap.e5") ?? throw new FileNotFoundException("缺少 Hexzmap.e5。");
        var archive = E5Archive.OpenFresh(hexPath);
        var decoded = archive.ReadDecoded(asset.LogicalId + 1);
        if (decoded.Length != TerrainPrefixLength + width * height)
            throw new InvalidDataException("Hexzmap 地形块长度与地图网格不一致。");
        var bmpPath = Path.Combine(outputFolder, mapId + ".BMP");
        EncodeTerrainBmp(bmpPath, width, height, decoded.AsSpan(0, 2).ToArray(), decoded.AsSpan(2).ToArray());
        _ = DecodeTerrainBmp(bmpPath);
        return (jpegPath, bmpPath);
    }

    private static void EncodeTerrainBmp(string path, int width, int height, byte[] prefix, byte[] cells)
    {
        var bytes = new byte[BmpPixelOffset + TerrainPrefixLength + cells.Length];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10, 4), BmpPixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), -height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 8);
        for (var index = 0; index < 256; index++)
        {
            var offset = 54 + index * 4;
            bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = (byte)index;
        }
        Buffer.BlockCopy(prefix, 0, bytes, BmpPixelOffset, 2);
        Buffer.BlockCopy(cells, 0, bytes, BmpPixelOffset + 2, cells.Length);
        File.WriteAllBytes(path, bytes);
    }

    private static TerrainBmp DecodeTerrainBmp(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < BmpPixelOffset + 2 || bytes[0] != 'B' || bytes[1] != 'M') throw new InvalidDataException("地形 BMP 格式无效。");
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
        if (offset != BmpPixelOffset || width <= 0 || rawHeight >= 0 || bits != 8) throw new InvalidDataException("地形 BMP 必须是规定的 8bpp 顶向格式。");
        var height = -rawHeight;
        var expected = BmpPixelOffset + 2 + width * height;
        if (bytes.Length != expected) throw new InvalidDataException($"地形 BMP 长度必须为 {expected}，实际 {bytes.Length}。");
        return new TerrainBmp(width, height, bytes.AsSpan(BmpPixelOffset + 2, width * height).ToArray());
    }

    private static string Find(string folder, string stem, IReadOnlyList<string> extensions)
    {
        var file = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase) &&
            extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        return file ?? throw new FileNotFoundException($"目录中缺少 {stem}{string.Join('/', extensions)}。", folder);
    }

    private static string FindMapImage(string folder, string mapId)
    {
        string[] extensions = [".bmp", ".png", ".jpg", ".jpeg", ".gif", ".tif", ".tiff", ".ico", ".webp"];
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(path =>
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            return extensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
                (stem.Equals(mapId + "_image", StringComparison.OrdinalIgnoreCase) ||
                 stem.Equals(mapId, StringComparison.OrdinalIgnoreCase) && !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        if (files.Length != 1) throw new InvalidDataException(files.Length == 0
            ? $"目录中缺少 {mapId} 的底图；支持图片格式，可使用 {mapId}_image.bmp 与地形 {mapId}.bmp 区分。"
            : $"目录中有多个 {mapId} 底图候选，请只保留一张图片。");
        return files[0];
    }

    private sealed record TerrainBmp(int Width, int Height, byte[] Cells);
}
