using System.Drawing;
using Ccz.Shared;

namespace ImageManager.Core;

internal static class CodecHelpers
{
    public static Bitmap DecodeEntry(ProjectProfile project, string path, int imageNumber, int width = 0, int height = 0)
    {
        var bytes = E5Archive.Open(path).ReadDecoded(imageNumber);
        if (ImageRendering.IsStandardImage(bytes)) return ImageRendering.DecodeStandardImage(bytes);
        if (Path.GetFileName(path).Equals("Logo.e5", StringComparison.OrdinalIgnoreCase) && bytes.Length is 2304 or 2306)
            return ImageRendering.DecodeIndexed(bytes.AsSpan(0, 48 * 48).ToArray(), 48, 48, new PaletteService().Load(project));
        if (width <= 0 || height <= 0) throw new InvalidDataException($"{Path.GetFileName(path)} #{imageNumber} 不是标准图片，且没有已知 RAW 尺寸。");
        return ImageRendering.DecodeIndexed(bytes, width, height, new PaletteService().Load(project));
    }

    public static IReadOnlyList<DecodedFrame> DecodeStrip(ProjectProfile project, LogicalImageAsset asset)
    {
        var result = new List<DecodedFrame>();
        foreach (var group in asset.Frames.GroupBy(frame => (frame.SourcePath, frame.ImageNumber)))
        {
            var first = group.First();
            var maxFrame = group.Max(frame => frame.FrameIndex) + 1;
            var decoded = E5Archive.Open(first.SourcePath).ReadDecoded(first.ImageNumber);
            var indexed = !ImageRendering.IsStandardImage(decoded);
            var fullHeight = checked(first.Height * maxFrame);
            byte[]? stripIndexes = indexed ? decoded.AsSpan(0, checked(first.Width * fullHeight)).ToArray() :
                ImageRendering.TryExtract8BppBmpIndexes(decoded, first.Width, fullHeight, out var bmpIndexes) ? bmpIndexes : null;
            var palette = new PaletteService().Load(project);
            var paletteBgra = indexed ? ImageRendering.BuildPaletteBgra(palette) :
                ImageRendering.TryExtract8BppBmpPalette(decoded, out var bmpPalette) ? bmpPalette : null;
            using var strip = indexed
                ? ImageRendering.DecodeIndexed(decoded, first.Width, fullHeight, palette)
                : ImageRendering.DecodeStandardImage(decoded);
            foreach (var descriptor in group.OrderBy(frame => frame.FrameIndex))
            {
                var frame = ImageRendering.Crop(strip, new Rectangle(0, descriptor.FrameIndex * descriptor.Height, descriptor.Width, descriptor.Height));
                byte[]? indexes = null;
                if (stripIndexes != null)
                {
                    var frameLength = checked(descriptor.Width * descriptor.Height);
                    indexes = stripIndexes.AsSpan(descriptor.FrameIndex * frameLength, frameLength).ToArray();
                }
                result.Add(new DecodedFrame(descriptor, frame, indexes, paletteBgra));
            }
        }
        return result;
    }
}

public sealed class RResourceCodec : IResourceCodec
{
    public ResourceKind Kind => ResourceKind.RComposite;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
    {
        var path = resource.Paths.Single();
        var count = E5Archive.Open(path).Entries.Count / 2;
        return Enumerable.Range(0, count).Select(id =>
        {
            var frames = new List<FrameDescriptor>(40);
            AddSide(frames, path, id * 2 + 1, "front");
            AddSide(frames, path, id * 2 + 2, "back");
            return new LogicalImageAsset(resource.Key, id, $"R{id:000}", ImageCategory.CharacterR, Kind,
                frames, Array.Empty<string>(), false, false, "正反面各 20 帧")
            {
                EditableSections =
                [
                    new("正面", "Pmapobj.e5", path, id * 2 + 1, 48, 1280, 48, 64, 20),
                    new("背面", "Pmapobj.e5", path, id * 2 + 2, 48, 1280, 48, 64, 20)
                ]
            };
        }).ToArray();
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => CodecHelpers.DecodeStrip(project, asset);

    private static void AddSide(List<FrameDescriptor> frames, string path, int imageNumber, string side)
    {
        for (var frame = 0; frame < 20; frame++)
            frames.Add(new FrameDescriptor("Pmapobj.e5", path, imageNumber, frame, 1, "r-action", side, 48, 64));
    }
}

public sealed class SResourceCodec : IResourceCodec
{
    private static readonly (string Key, string Action, int Width, int Height, int Count)[] Definitions =
    [
        ("Unit_mov.e5", "mov", 48, 48, 11),
        ("Unit_atk.e5", "atk", 64, 64, 12),
        ("Unit_spc.e5", "spc", 48, 48, 5)
    ];

    public ResourceKind Kind => ResourceKind.SComposite;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        => ReadAssets(project, resource, SImageMode.Person);

    public IReadOnlyList<LogicalImageAsset> ReadJobAssets(ProjectProfile project, ImageResourceDescriptor resource)
        => ReadAssets(project, resource, SImageMode.Job);

    private IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource, SImageMode selectedMode)
    {
        var paths = resource.Paths.ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase);
        var physicalCount = Definitions.Min(definition => E5Archive.Open(paths[definition.Key]).Entries.Count);
        var mapper = new SLogicalAssetMappingService();
        var mappings = (selectedMode == SImageMode.Job ? mapper.BuildJobs(physicalCount) : mapper.Build(physicalCount))
            .Select(mapping => (Mapping: mapping, Mode: selectedMode)).ToArray();
        var assets = new List<LogicalImageAsset>(mappings.Length);
        foreach (var (mapping, mode) in mappings)
        {
            var frames = new List<FrameDescriptor>();
            var sections = new List<EditableSourceSection>();
            foreach (var binding in mapping.Turns)
            foreach (var definition in Definitions)
            {
                for (var frame = 0; frame < definition.Count; frame++)
                {
                    var direction = ResolveDirection(definition.Action, frame);
                    frames.Add(new FrameDescriptor(definition.Key, paths[definition.Key], binding.ImageNumber, frame,
                        binding.Turn, definition.Action, direction, definition.Width, definition.Height));
                }
                sections.Add(new EditableSourceSection(
                    definition.Action, definition.Key, paths[definition.Key], binding.ImageNumber,
                    definition.Width, definition.Height * definition.Count,
                    definition.Width, definition.Height, definition.Count));
            }

            var imageNumber = mapping.Turns[0].ImageNumber;
            var jobId = mode == SImageMode.Job ? SImageNumberingRules.GetJobId(imageNumber) : 0;
            var factionIndex = mode == SImageMode.Job ? SImageNumberingRules.GetFactionIndex(imageNumber) : 0;
            var jobFamily = jobId < 60 ? jobId / 3 : jobId - 40;
            var jobTurn = jobId < 60 ? jobId % 3 + 1 : 1;
            var faction = factionIndex switch { 0 => "我军", 1 => "友军", _ => "敌军" };
            var displayName = $"S_{imageNumber:000}";
            assets.Add(new LogicalImageAsset(resource.Key, mapping.SId, displayName, ImageCategory.CharacterS, Kind,
                frames, Array.Empty<string>(), mode == SImageMode.Job, false,
                mode == SImageMode.Job ? $"物理 Unit #{imageNumber:000}；兵种 ID {jobId}；{faction}；兵种系 ID {jobFamily}；{ChineseTurn(jobTurn)}转" :
                $"将领 S 形象；物理 Unit #{mapping.Turns[0].ImageNumber:000}")
            {
                EditableSections = sections,
                SMapping = mapping,
                SMode = mode,
                PhysicalImageNumber = imageNumber,
                JobId = mode == SImageMode.Job ? jobId : null,
                FactionIndex = mode == SImageMode.Job ? factionIndex : null,
                PhysicalIdentity = $"{resource.Key}:Unit:{imageNumber}"
            });
        }
        return assets;
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => CodecHelpers.DecodeStrip(project, asset);

    private static string ResolveDirection(string action, int frame) => action switch
    {
        "mov" when frame <= 1 => "front",
        "mov" when frame <= 3 => "back",
        "mov" when frame <= 5 => "side",
        "mov" when frame == 6 => "front",
        "mov" when frame == 7 => "back",
        "mov" when frame == 8 => "side",
        "atk" when frame <= 3 => "front",
        "atk" when frame <= 7 => "back",
        "atk" => "side",
        _ => "none"
    };

    private static string ChineseTurn(int turn) => turn switch { 1 => "一", 2 => "二", 3 => "三", _ => turn.ToString() };
}

public sealed class FaceResourceCodec : IResourceCodec
{
    public ResourceKind Kind => ResourceKind.FaceComposite;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
    {
        var path = resource.Paths[0];
        var archive = E5Archive.Open(path);
        var count = archive.Entries.Count;
        var touPath = resource.Paths.FirstOrDefault(item => Path.GetFileName(item).Equals("Tou.dll", StringComparison.OrdinalIgnoreCase));
        var tou = touPath == null ? new Dictionary<int, DllBitmapRecord>() : DllBitmapReader.Read(touPath)
            .GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.FirstOrDefault(item => item.Language == 2052) ?? group.First());
        var assets = new List<LogicalImageAsset>();
        if (count >= 8)
        {
            var special = Enumerable.Range(1, 8).SelectMany(number => Descriptors(archive, path, number, number - 1, touPath, tou)).ToArray();
            assets.Add(new LogicalImageAsset(resource.Key, 0, "头像 000（八表情）", ImageCategory.Portrait, Kind,
                special, Array.Empty<string>(), true, false, project.TryGetResource("Tou.dll") == null ? "小头像降级模式" : "Face/Tou 完整模式"));
        }
        for (var id = 1; id + 8 <= count; id++)
            assets.Add(new LogicalImageAsset(resource.Key, id, $"头像 {id:000}", ImageCategory.Portrait, Kind,
                Descriptors(archive, path, id + 8, 0, touPath, tou), Array.Empty<string>(), false, false,
                project.TryGetResource("Tou.dll") == null ? "小头像降级模式" : "Face/Tou 完整模式"));
        return assets;
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => asset.Frames.Select(descriptor => DecodeFrame(project, descriptor)).ToArray();

    public DecodedFrame? DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!asset.Frames.Contains(descriptor)) return null;
        var frame = DecodeFrame(project, descriptor);
        if (cancellationToken.IsCancellationRequested) { frame.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return frame;
    }

    private static DecodedFrame DecodeFrame(ProjectProfile project, FrameDescriptor descriptor)
    {
        if (!descriptor.SourceKey.Equals("Tou.dll", StringComparison.OrdinalIgnoreCase))
            return new DecodedFrame(descriptor, FacePayloadService.Decode(project,
                E5Archive.Open(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber)));
        var record = DllBitmapReader.Read(descriptor.SourcePath)
            .First(item => item.Id == descriptor.ImageNumber && item.Language == descriptor.SourceOffset);
        return new DecodedFrame(descriptor, DllBitmapReader.DecodeDib(record.Dib));
    }

    private static IReadOnlyList<FrameDescriptor> Descriptors(E5Archive archive, string path, int imageNumber, int frameIndex,
        string? touPath, IReadOnlyDictionary<int, DllBitmapRecord> tou)
    {
        var size = FacePayloadService.GetSize(archive.ReadDecoded(imageNumber));
        var descriptors = new List<FrameDescriptor>
        {
            new("Face.e5", path, imageNumber, frameIndex, 1, "face-small", "none", size.Width, size.Height)
        };
        var touId = imageNumber + 300;
        if (touPath != null && tou.TryGetValue(touId, out var record))
            descriptors.Add(new FrameDescriptor("Tou.dll", touPath, touId, frameIndex, 1, "tou-large", "none",
                record.Width, record.Height, record.Language));
        return descriptors;
    }
}

public sealed class PairedE5IconCodec : IResourceCodec
{
    public ResourceKind Kind => ResourceKind.E5Indexed;
    public bool CanHandle(ImageResourceDescriptor resource)
        => resource.Kind == Kind && (resource.Key.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) ||
                                    resource.Key.Equals("Mtem.e5", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
    {
        var path = resource.Paths.Single();
        var count = E5Archive.Open(path).Entries.Count / 2;
        var strategy = resource.Key.Equals("Mtem.e5", StringComparison.OrdinalIgnoreCase);
        return Enumerable.Range(0, count).Select(id => new LogicalImageAsset(resource.Key, id, $"{(strategy ? "策略" : "宝物")}图标 {id:000}",
            strategy ? ImageCategory.StrategyIcon : ImageCategory.ItemIcon, Kind,
            [Descriptor(resource.Key, path, id * 2 + 1, "small"), Descriptor(resource.Key, path, id * 2 + 2, "large")],
            Array.Empty<string>(), false, false, "6.6 小图/大图双条目")).ToArray();
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => asset.Frames.Select(descriptor => new DecodedFrame(descriptor,
            CodecHelpers.DecodeEntry(project, descriptor.SourcePath, descriptor.ImageNumber))).ToArray();

    private static FrameDescriptor Descriptor(string key, string path, int imageNumber, string size)
        => new(key, path, imageNumber, 0, 1, size, "none", 0, 0);
}

public sealed class GenericE5ResourceCodec : IResourceCodec
{
    public ResourceKind Kind => ResourceKind.E5Indexed;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
    {
        var path = resource.Paths.Single();
        var archive = E5Archive.Open(path);
        return archive.Entries.Select(entry =>
        {
            var logical = entry.ImageNumber;
            var frame = new FrameDescriptor(resource.Key, path, entry.ImageNumber, 0, 1, "image", "none", 0, 0);
            return new LogicalImageAsset(resource.Key, logical, $"{resource.DisplayName} {logical:000}", resource.Category,
                Kind, [frame], Array.Empty<string>(), false, resource.IsExperimental, resource.Status);
        }).Where(asset => asset.LogicalId >= 0).ToArray();
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => asset.Frames.Select(descriptor => new DecodedFrame(descriptor,
            CodecHelpers.DecodeEntry(project, descriptor.SourcePath, descriptor.ImageNumber))).ToArray();
}
