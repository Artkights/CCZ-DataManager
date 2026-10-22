namespace ImageManager.Core;

/// <summary>Range archives also ship as LS-compressed 64×64 indexed pixel planes.</summary>
public sealed class RangeResourceCodec : IResourceCodec
{
    public const int RawWidth = 64;
    public const int RawHeight = 64;

    public ResourceKind Kind => ResourceKind.E5Indexed;

    public bool CanHandle(ImageResourceDescriptor resource)
        => resource.Kind == Kind &&
           (resource.Key.Equals("Hitarea.e5", StringComparison.OrdinalIgnoreCase) ||
            resource.Key.Equals("Effarea.e5", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
    {
        var archive = E5Archive.Open(resource.Paths.Single());
        return new GenericE5ResourceCodec().ReadAssets(project, resource).Select(asset =>
        {
            var frame = asset.Frames.Single();
            return archive.Entries[frame.ImageNumber - 1].DecodedLength is 4096 or 4098
                ? asset with { Frames = [frame with { Width = RawWidth, Height = RawHeight }] }
                : asset;
        }).ToArray();
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
    {
        var descriptor = asset.Frames.Single();
        var bytes = E5Archive.Open(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
        if (ImageRendering.IsStandardImage(bytes))
            return [new DecodedFrame(descriptor, ImageRendering.DecodeStandardImage(bytes))];
        if (bytes.Length is not (4096 or 4098))
            throw new InvalidDataException($"{asset.ResourceKey} #{descriptor.ImageNumber} 不是标准图片，也不是已知的 {RawWidth}×{RawHeight} RAW 范围图片。");
        bytes = bytes.AsSpan(0, RawWidth * RawHeight).ToArray();
        var palette = new PaletteService().Load(project);
        return [new DecodedFrame(descriptor, ImageRendering.DecodeIndexed(bytes, RawWidth, RawHeight, palette),
            bytes, ImageRendering.BuildPaletteBgra(palette))];
    }
}
