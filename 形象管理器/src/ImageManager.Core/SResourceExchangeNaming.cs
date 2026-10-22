namespace ImageManager.Core;

/// <summary>Import naming is chosen explicitly; old S numbers must never be guessed.</summary>
public enum RsImportNaming { Physical, Legacy }

public static class SResourceExchangeNaming
{
    public static int PhysicalId(LogicalImageAsset asset)
    {
        var numbers = asset.Frames.Select(frame => frame.ImageNumber).Distinct().ToArray();
        if (asset.Kind != ResourceKind.SComposite || numbers.Length != 1 || numbers[0] < 1)
            throw new InvalidDataException("S 形象必须对应一个有效的物理 Unit 图号。");
        return numbers[0];
    }

    public static string Category(LogicalImageAsset asset) => asset.SMode == SImageMode.Job ? "兵种" : "将领";

    public static void ValidateFrameSet(LogicalImageAsset asset, IReadOnlyList<FrameDescriptor>? frames = null)
    {
        var physical = PhysicalId(asset);
        frames ??= asset.Frames;
        if (frames.Count != 28 || frames.Any(frame => frame.ImageNumber != physical || frame.Turn != 1))
            throw new InvalidDataException("S 形象只接受同一物理图号的一组完整 28 帧。");
        foreach (var (action, count, size) in new[] { ("mov", 11, 48), ("atk", 12, 64), ("spc", 5, 48) })
        {
            var selected = frames.Where(frame => frame.Action == action).OrderBy(frame => frame.FrameIndex).ToArray();
            if (selected.Length != count || !selected.Select(frame => frame.FrameIndex).SequenceEqual(Enumerable.Range(0, count)) ||
                selected.Any(frame => frame.Width != size || frame.Height != size))
                throw new InvalidDataException($"物理 Unit #{physical} 的 {action} 必须含从第0帧开始的 {count} 个 {size}×{size} 帧。");
        }
        var expected = asset.Frames.ToHashSet();
        if (frames.Any(frame => !expected.Contains(frame)))
            throw new InvalidDataException("提供的 S 帧描述与选中物理图号的动作条不一致。");
    }

    public static string BaseName(LogicalImageAsset asset) => $"S_{PhysicalId(asset):000}";
    public static string RelativePath(LogicalImageAsset asset) => Path.Combine(Category(asset), BaseName(asset));
    public static string ImportBaseName(LogicalImageAsset asset, RsImportNaming naming)
    {
        if (asset.Kind == ResourceKind.RComposite) return $"R{asset.LogicalId:000}";
        if (naming == RsImportNaming.Physical) return BaseName(asset);
        var physical = PhysicalId(asset);
        return asset.SMode == SImageMode.Job ? $"兵种S{physical - 1:000}" : $"S{physical - 240:000}";
    }

    public static string DescribeImport(LogicalImageAsset asset, RsImportNaming naming)
        => $"{Category(asset)}/{ImportBaseName(asset, naming)} → Unit #{PhysicalId(asset)}（{BaseName(asset)}）";
}
