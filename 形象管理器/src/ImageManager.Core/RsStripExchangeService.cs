using System.Drawing;
using System.Drawing.Imaging;

namespace ImageManager.Core;

public sealed record RsStripExportResult(IReadOnlyList<string> Files);

/// <summary>Native action-strip exchange, separate from editable AI reference atlases.</summary>
public sealed class RsStripExchangeService
{
    private readonly IImageCatalogService _catalog;

    public RsStripExchangeService(IImageCatalogService? catalog = null, RsImportService? imports = null)
    {
        _catalog = catalog ?? new ImageCatalogService();
    }

    public RsStripExportResult Export(ProjectProfile project, LogicalImageAsset asset, string directory,
        int preferredTurn = 1, bool allTurns = false)
    {
        ValidateAsset(asset);
        var selected = SelectDescriptors(asset, preferredTurn, allTurns);
        var decoded = _catalog.DecodeFrames(project, asset with { Frames = selected });
        var keyColor = ReferenceAtlasService.ResolveKeyColor(new PaletteService().Load(project));
        try
        {
            var destination = Path.Combine(Path.GetFullPath(directory), asset.Kind == ResourceKind.SComposite
                ? SResourceExchangeNaming.RelativePath(asset) : BaseName(asset));
            Directory.CreateDirectory(destination);
            var files = new List<string>();
            foreach (var group in decoded.GroupBy(frame => (frame.Descriptor.Turn,
                         Role: asset.Kind == ResourceKind.RComposite ? frame.Descriptor.Direction : frame.Descriptor.Action)))
            {
                var ordered = group.OrderBy(frame => frame.Descriptor.FrameIndex).ToArray();
                var spec = Specification(asset.Kind, group.Key.Role);
                ValidateFrames(ordered, spec.Width, spec.Height, spec.Count);
                var folder = destination;
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, group.Key.Role + ".bmp");
                using var strip = new Bitmap(spec.Width, spec.Height * spec.Count, PixelFormat.Format24bppRgb);
                // The integration package uses 24bpp BMP with a magenta key. Do
                // not let GDI choose an indexed/grayscale palette on save.
                using (var graphics = Graphics.FromImage(strip))
                {
                    graphics.Clear(keyColor);
                    for (var index = 0; index < ordered.Length; index++)
                    {
                        using var frame = ImageRendering.ApplyKeyTransparency(ordered[index].Bitmap, false);
                        graphics.DrawImageUnscaled(frame, 0, index * spec.Height);
                    }
                }
                SaveBmp(strip, target);
                files.Add(target);
            }
            return new RsStripExportResult(files);
        }
        finally { foreach (var frame in decoded) frame.Dispose(); }
    }

    public MutationPlan PlanReplace(ProjectProfile project, LogicalImageAsset asset, string directory,
        int preferredTurn = 1, bool allTurns = false, RsImportNaming naming = RsImportNaming.Physical)
        => PlanReplaceMany(project, [asset], directory, preferredTurn, allTurns, naming);

    public MutationPlan PlanReplaceMany(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets,
        string directory, int preferredTurn = 1, bool allTurns = false, RsImportNaming naming = RsImportNaming.Physical)
        => MutationPlanningSnapshot.Build(assets.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath),
            () => PlanReplaceManyCore(project, assets, directory, preferredTurn, allTurns, naming));

    private MutationPlan PlanReplaceManyCore(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets,
        string directory, int preferredTurn, bool allTurns, RsImportNaming naming)
    {
        if (project.IsReadOnly) throw new InvalidOperationException("当前项目只读，不能替换动作条。");
        if (assets.Count == 0) throw new InvalidOperationException("没有选中的 R/S 形象。");
        var first = assets[0];
        if (assets.Any(asset => asset.Kind != first.Kind || !asset.ResourceKey.Equals(first.ResourceKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("批量动作条替换必须来自同一个 R/S 资源。");
        var assignments = new List<ImageImportAssignment>();
        foreach (var asset in assets)
        {
            ValidateAsset(asset);
            _ = SelectDescriptors(asset, preferredTurn, allTurns);
            var sourceRoot = ResolveMaterialDirectory(asset, directory, naming, assets.Count == 1);
            if (asset.Kind == ResourceKind.SComposite && naming == RsImportNaming.Legacy &&
                new[] { "turn2", "turn3" }.Any(name => Directory.Exists(Path.Combine(sourceRoot, name))))
                throw new InvalidDataException("当前 S 导入只写入选中的一个物理图号。旧多转目录请先明确选择一组动作条，不能展开到其他图号。");
            var roles = asset.Kind == ResourceKind.RComposite ? new[] { "front", "back" } : new[] { "mov", "atk", "spc" };
            var hasLegacyTurn = asset.Kind == ResourceKind.SComposite && naming == RsImportNaming.Legacy &&
                Directory.Exists(Path.Combine(sourceRoot, "turn1"));
            if (hasLegacyTurn && roles.Any(role => FindMaterial(sourceRoot, role) != null))
                throw new InvalidDataException("旧素材同时包含根目录动作条与 turn1，请使用文件导入窗口明确映射。");
            var folder = hasLegacyTurn ? Path.Combine(sourceRoot, "turn1") : sourceRoot;
            var sources = roles.Select(role => (Role: role, Path: FindMaterial(folder, role))).Where(item => item.Path != null).ToArray();
            if (sources.Length == 0) throw new InvalidDataException($"{asset.DisplayName} 没有匹配的 BMP/PNG/JPG/JPEG 原生动作条。");
            assignments.AddRange(sources.Select(source => new ImageImportAssignment(asset, source.Role, source.Path!)));
        }
        var resource = new ImageResourceDescriptor(first.ResourceKey, first.ResourceKey, first.Category, first.Kind,
            assets.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            assets.Count, true, true, false, "", "");
        using var prepared = new ImageImportPreparationService().PrepareAsync(new(project, resource,
            ImageImportOperation.Replace, assignments)).GetAwaiter().GetResult();
        if (!prepared.CanCommit) throw new InvalidDataException(string.Join("；", prepared.Rows.Where(row => row.Error != null)
            .Select(row => row.TargetLabel + "/" + row.Role + "：" + row.Error)));
        return prepared.Plan! with
        {
            Summary = $"替换 {assets.Count} 个 R/S 形象原生动作条",
            Warnings = prepared.Plan.Warnings.Concat(assets.Where(asset => asset.Kind == ResourceKind.SComposite)
                .Select(asset => "物理目标：" + SResourceExchangeNaming.DescribeImport(asset, naming))).ToArray()
        };
    }

    private static IReadOnlyList<FrameDescriptor> SelectDescriptors(LogicalImageAsset asset, int preferredTurn, bool allTurns)
    {
        if (asset.Kind == ResourceKind.SComposite)
            SResourceExchangeNaming.ValidateFrameSet(asset);
        return asset.Frames;
    }

    private static void ValidateAsset(LogicalImageAsset asset)
    {
        if (asset.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
            throw new InvalidOperationException("原生动作条只适用于 R/S 形象。");
    }

    private static (int Width, int Height, int Count) Specification(ResourceKind kind, string role)
        => (kind, role) switch
        {
            (ResourceKind.RComposite, "front" or "back") => (48, 64, 20),
            (ResourceKind.SComposite, "mov") => (48, 48, 11),
            (ResourceKind.SComposite, "atk") => (64, 64, 12),
            (ResourceKind.SComposite, "spc") => (48, 48, 5),
            _ => throw new InvalidDataException($"未知动作条：{role}")
        };

    private static void ValidateFrames(IReadOnlyList<DecodedFrame> frames, int width, int height, int count)
    {
        if (frames.Count != count || !frames.Select(frame => frame.Descriptor.FrameIndex).SequenceEqual(Enumerable.Range(0, count)) ||
            frames.Any(frame => frame.Bitmap.Width != width || frame.Bitmap.Height != height))
            throw new InvalidDataException($"动作条要求完整的 {count} 个 {width}×{height} 动作帧。");
    }

    private static string BaseName(LogicalImageAsset asset) => asset.Kind == ResourceKind.RComposite
        ? $"R{asset.LogicalId:000}" : SResourceExchangeNaming.BaseName(asset);

    private static string ResolveMaterialDirectory(LogicalImageAsset asset, string directory, RsImportNaming naming, bool allowDirect)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到素材目录：{root}");
        var name = SResourceExchangeNaming.ImportBaseName(asset, naming);
        var candidates = new List<string>();
        if (asset.Kind == ResourceKind.SComposite && naming == RsImportNaming.Physical)
        {
            var categorized = Path.Combine(root, SResourceExchangeNaming.Category(asset), name);
            if (Directory.Exists(categorized)) candidates.Add(categorized);
        }
        var nested = Path.Combine(root, name);
        if (Directory.Exists(nested)) candidates.Add(nested);
        if (candidates.Count > 1)
            throw new InvalidDataException($"{name} 同时匹配分类目录和根目录中的素材，请只保留一个来源。");
        if (candidates.Count == 1) return candidates[0];
        // R keeps its existing ability to apply one front/back pair to several
        // selected logical assets. S batch imports require one named directory
        // per physical target and must not repeat a fallback source silently.
        if (allowDirect || asset.Kind == ResourceKind.RComposite) return root;
        throw new DirectoryNotFoundException($"批量导入缺少 {name} 目录；请明确选择物理编号或旧编号格式。");
    }

    private static string? FindMaterial(string directory, string role)
    {
        var names = role switch
        {
            "front" => new[] { "front", "x", "1" },
            "back" => new[] { "back", "y", "2" },
            _ => new[] { role }
        };
        var candidates = Directory.EnumerateFiles(directory).Where(path =>
            names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase) &&
            new[] { ".bmp", ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length > 1)
            throw new InvalidDataException($"{role} 同时匹配多个素材，请在文件导入窗口明确映射，或只保留一张 BMP/PNG/JPG/JPEG。");
        return candidates.SingleOrDefault();
    }

    private static void SaveBmp(Bitmap strip, string target)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { strip.Save(temporary, ImageFormat.Bmp); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
