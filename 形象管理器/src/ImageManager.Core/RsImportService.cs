using System.Drawing;

namespace ImageManager.Core;

public sealed class RsImportService
{
    public MutationPlan PlanImport(ProjectProfile project, LogicalImageAsset asset, IReadOnlyList<DecodedFrame> importedFrames)
        => PlanImportMany(project, [(asset, importedFrames)]);

    public MutationPlan PlanImportMany(ProjectProfile project,
        IReadOnlyList<(LogicalImageAsset Asset, IReadOnlyList<DecodedFrame> Frames)> imports)
        => MutationPlanningSnapshot.Build(imports.SelectMany(item => item.Asset.Frames).Select(frame => frame.SourcePath),
            () => PlanImportManyCore(project, imports));

    private MutationPlan PlanImportManyCore(ProjectProfile project,
        IReadOnlyList<(LogicalImageAsset Asset, IReadOnlyList<DecodedFrame> Frames)> imports)
    {
        EnsureWritable(project);
        if (imports.Count == 0) throw new InvalidOperationException("没有待导入的参考图。");
        if (imports.Select(item => (item.Asset.ResourceKey, item.Asset.SMode, item.Asset.LogicalId)).Distinct().Count() != imports.Count)
            throw new InvalidOperationException("批量参考图中包含重复逻辑编号。");
        var replacementsByPath = new Dictionary<string, Dictionary<int, byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in imports)
        {
            var asset = item.Asset;
            if (asset.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
                throw new InvalidOperationException("只支持向 R/S 逻辑形象导入参考图。");
            var expected = asset.Frames.ToDictionary(FrameKey);
            var normalizedFrames = NormalizeLegacySFrames(asset, item.Frames);
            var imported = normalizedFrames.ToDictionary(frame => FrameKey(frame.Descriptor));
            var fullSet = expected.Count == imported.Count && expected.Keys.All(imported.ContainsKey);
            var partialLegacyS = asset.Kind == ResourceKind.SComposite && imported.Count == 28 &&
                                 imported.Keys.All(expected.ContainsKey) && imported.Values.Select(frame => frame.Descriptor.ImageNumber).Distinct().Count() == 1;
            if (!fullSet && !partialLegacyS)
                throw new InvalidDataException($"{asset.DisplayName} 帧集合与目标不一致：目标 {expected.Count} 帧，导入 {imported.Count} 帧。");
            foreach (var group in asset.Frames.GroupBy(frame => (frame.SourcePath, frame.ImageNumber))
                         .Where(group => group.All(frame => imported.ContainsKey(FrameKey(frame)))))
            {
                var ordered = group.OrderBy(frame => frame.FrameIndex).ToArray();
                var original = E5Archive.OpenFresh(group.Key.SourcePath).ReadDecoded(group.Key.ImageNumber);
                var bytes = EncodeStrip(project, ordered, imported, original);
                if (!replacementsByPath.TryGetValue(group.Key.SourcePath, out var replacements))
                    replacementsByPath[group.Key.SourcePath] = replacements = [];
                replacements[group.Key.ImageNumber] = bytes;
            }
        }
        var files = replacementsByPath.Select(pair => new MutationFile(pair.Key,
            E5Archive.OpenFresh(pair.Key).Rebuild(replacementDecoded: pair.Value), "参考图还原 R/S 长条")).ToArray();
        return new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.ImportReference,
            $"批量导入 {imports.Count} 个 R/S 参考总图", files,
            ["导入按当前逻辑形象的固定语义帧序重建，不改变其他逻辑形象。"], false,
            new Dictionary<string, string>
            {
                ["LogicalAssets"] = string.Join(",", imports.Select(item => item.Asset.DisplayName)),
                ["Protocol"] = "layout-derived-v4"
            });
    }

    public MutationPlan PlanAppend(ProjectProfile project, ImageResourceDescriptor resource, string sourceDirectory)
        => MutationPlanningSnapshot.Build(resource.Paths, () => PlanAppendCore(project, resource, sourceDirectory));

    private MutationPlan PlanAppendCore(ProjectProfile project, ImageResourceDescriptor resource, string sourceDirectory)
    {
        EnsureWritable(project);
        if (resource.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
            throw new InvalidOperationException("该资源不是 R/S 形象。");
        if (resource.Kind == ResourceKind.SComposite) EnsureAlignedSArchives(resource.Paths);
        var directories = ResolveSourceDirectories(sourceDirectory, resource.Kind).ToArray();
        var roles = resource.Kind == ResourceKind.RComposite ? new[] { "front", "back" } : new[] { "mov", "atk", "spc" };
        var assignments = directories.SelectMany((directory, group) => roles.Select(role =>
            new ImageImportAssignment(null, role, FindImage(directory, role), group))).ToArray();
        using var prepared = new ImageImportPreparationService().PrepareAsync(new(project, resource,
            ImageImportOperation.Append, assignments)).GetAwaiter().GetResult();
        if (!prepared.CanCommit) throw new InvalidDataException(string.Join("；", prepared.Rows.Where(row => row.Error != null)
            .Select(row => row.TargetLabel + "/" + row.Role + "：" + row.Error)));
        return prepared.Plan!;
    }

    public MutationPlan PlanPixelEdit(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor editedDescriptor, Bitmap edited)
        => MutationPlanningSnapshot.Build(asset.Frames.Select(frame => frame.SourcePath),
            () => PlanPixelEditCore(project, asset, editedDescriptor, edited));

    private MutationPlan PlanPixelEditCore(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor editedDescriptor, Bitmap edited)
    {
        var catalog = new ImageCatalogService();
        var frames = catalog.DecodeFrames(project, asset).ToList();
        try
        {
            var index = frames.FindIndex(frame => FrameKey(frame.Descriptor) == FrameKey(editedDescriptor));
            if (index < 0) throw new InvalidOperationException("找不到待写回帧。");
            frames[index].Bitmap.Dispose();
            frames[index] = new DecodedFrame(editedDescriptor, new Bitmap(edited));
            return PlanImport(project, asset, frames);
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    public MutationPlan PlanAppendCopy(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor editedDescriptor, Bitmap edited)
        => MutationPlanningSnapshot.Build(asset.Frames.Select(frame => frame.SourcePath),
            () => PlanAppendCopyCore(project, asset, editedDescriptor, edited));

    private MutationPlan PlanAppendCopyCore(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor editedDescriptor, Bitmap edited)
    {
        EnsureWritable(project);
        ValidateAppendNamespace(asset);
        var catalog = new ImageCatalogService();
        var frames = catalog.DecodeFrames(project, asset).ToList();
        try
        {
            var index = frames.FindIndex(frame => FrameKey(frame.Descriptor) == FrameKey(editedDescriptor));
            if (index < 0) throw new InvalidOperationException("找不到待复制帧。");
            frames[index].Bitmap.Dispose();
            frames[index] = new DecodedFrame(editedDescriptor, new Bitmap(edited));
            return PlanAppendCopy(project, asset, frames, editedDescriptor.Turn);
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    public MutationPlan PlanAppendCopy(ProjectProfile project, LogicalImageAsset asset, IReadOnlyList<DecodedFrame> frames,
        int preferredTurn = 1)
        => MutationPlanningSnapshot.Build(asset.Frames.Select(frame => frame.SourcePath),
            () => PlanAppendCopyCore(project, asset, frames, preferredTurn));

    private MutationPlan PlanAppendCopyCore(ProjectProfile project, LogicalImageAsset asset, IReadOnlyList<DecodedFrame> frames,
        int preferredTurn)
    {
        ValidateAppendNamespace(asset);
        if (asset.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
            throw new InvalidOperationException("只支持复制 R/S 完整逻辑形象。");
        var imported = frames.ToDictionary(frame => FrameKey(frame.Descriptor));
        var expected = asset.Frames.ToDictionary(FrameKey);
        if (imported.Count != expected.Count || expected.Keys.Any(key => !imported.ContainsKey(key)))
            throw new InvalidDataException("复制并追加要求提供完整且唯一的源帧集合。");
        var payloadsByPath = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        if (asset.Kind == ResourceKind.SComposite)
            SResourceExchangeNaming.ValidateFrameSet(asset);
        var sourceFrames = asset.Frames.ToArray();
        foreach (var group in sourceFrames.GroupBy(frame => (frame.SourcePath, frame.ImageNumber)))
        {
            var ordered = group.OrderBy(frame => frame.FrameIndex).ToArray();
            var original = E5Archive.OpenFresh(group.Key.SourcePath).ReadDecoded(group.Key.ImageNumber);
            var payload = EncodeStrip(project, ordered, imported, original);
            if (!payloadsByPath.TryGetValue(group.Key.SourcePath, out var payloads))
                payloadsByPath[group.Key.SourcePath] = payloads = [];
            payloads.Add(payload);
        }
        var files = payloadsByPath.Select(pair => new MutationFile(pair.Key,
            E5Archive.OpenFresh(pair.Key).Rebuild(appendDecoded: pair.Value), "复制完整逻辑形象并追加")).ToArray();
        return new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Append,
            "复制并追加 " + asset.DisplayName, files,
            [asset.Kind == ResourceKind.SComposite
                ? $"复制物理 Unit #{SResourceExchangeNaming.PhysicalId(asset)} 的完整 28 帧到三档案末尾；不会自动写入 Data 指定。"
                : "新物理编号不会自动写入 Data 指定。"], false);
    }

    private static void ValidateAppendNamespace(LogicalImageAsset asset)
    {
        if (asset.Kind == ResourceKind.SComposite && asset.SMode == SImageMode.Job)
            throw new InvalidOperationException("兵种 S 形象使用物理图号 1～240 固定槽，请导入或替换选中槽，不能追加到将领形象库。");
        if (asset.Kind == ResourceKind.SComposite)
            EnsureAlignedSArchives(asset.Frames.Select(frame => frame.SourcePath));
    }

    private static void EnsureAlignedSArchives(IEnumerable<string> paths)
    {
        var counts = paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Name: Path.GetFileName(path), Count: E5Archive.OpenFresh(path).Entries.Count)).ToArray();
        if (counts.Select(item => item.Count).Distinct().Count() > 1)
            throw new InvalidOperationException("Unit_mov/atk/spc 图片数量不一致，不能同步追加将领 S，否则会造成物理编号错位。" +
                string.Join("；", counts.Select(item => $"{item.Name}：{item.Count} 张")));
    }

    private static byte[] EncodeStrip(ProjectProfile project, IReadOnlyList<FrameDescriptor> descriptors,
        IReadOnlyDictionary<string, DecodedFrame> imported, byte[] original)
    {
        var width = descriptors[0].Width;
        var height = descriptors[0].Height;
        if (!descriptors.Select(frame => frame.FrameIndex).SequenceEqual(Enumerable.Range(0, descriptors.Count)))
            throw new InvalidDataException("动作条必须包含从 0 开始完整连续的动作帧。");
        using var strip = new Bitmap(width, checked(height * descriptors.Count), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(strip))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.Clear(Color.Transparent);
            for (var index = 0; index < descriptors.Count; index++)
            {
                var importedFrame = imported[FrameKey(descriptors[index])];
                var frame = importedFrame.Bitmap;
                if (frame.Width != descriptors[index].Width || frame.Height != descriptors[index].Height)
                    throw new InvalidDataException($"帧尺寸不符：要求 {descriptors[index].Width}x{descriptors[index].Height}，实际 {frame.Width}x{frame.Height}。");
                // Source numeric indexes never override the target palette.
                // Both F700FF and FF00FF are accepted as transparent material keys.
                using var keyed = ImageRendering.ApplyKeyTransparency(frame, false);
                graphics.DrawImageUnscaled(keyed, 0, index * height);
            }
        }
        return RebuildStrip(project, original, strip);
    }

    private static byte[] RebuildStrip(ProjectProfile project, byte[] original, Bitmap edited)
    {
        if (ImageRendering.IsStandardImage(original)) return BmpPayloadService.Rebuild(original, edited);
        var palette = new PaletteService().RequireWritePalette(project);
        return BmpPayloadService.RebuildRaw8(original, edited.Width, edited.Height, edited, palette);
    }

    private static void EnsureWritable(ProjectProfile project)
    {
        if (project.IsReadOnly) throw new InvalidOperationException("当前项目只读，不能写入 R/S 动作条。");
    }

    private static string FindImage(string directory, string stem)
    {
        var candidates = MaterialCandidates(directory, stem);
        if (candidates.Length > 1) throw new InvalidDataException($"{stem} 匹配多个素材，请使用文件导入窗口明确映射，或仅保留一个来源。");
        return candidates.SingleOrDefault() ?? throw new FileNotFoundException($"目录中缺少 {stem} 的 BMP/PNG/JPG/JPEG 素材。", directory);
    }

    private static string[] MaterialCandidates(string directory, string stem)
    {
        var names = stem == "front" ? new[] { "front", "x", "1" } : stem == "back" ? new[] { "back", "y", "2" } : new[] { stem };
        return Directory.EnumerateFiles(directory).Where(path => names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase) &&
            new[] { ".bmp", ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static IEnumerable<string> ResolveSourceDirectories(string root, ResourceKind kind)
    {
        var required = kind == ResourceKind.RComposite ? new[] { "front", "back" } : new[] { "mov", "atk", "spc" };
        bool IsValid(string directory) => required.All(stem => MaterialCandidates(directory, stem).Length > 0);
        if (kind == ResourceKind.SComposite) return ResolveSAppendDirectories(root);
        if (IsValid(root)) return [root];
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Where(IsValid).OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
        if (directories.Length == 0) throw new InvalidDataException("所选目录及其一级子目录中没有符合规范的 R/S 素材组。");
        return directories;
    }

    private static IReadOnlyList<string> ResolveSAppendDirectories(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("找不到 S 动作条素材目录：" + root);
        var stems = new[] { "mov", "atk", "spc" };
        bool HasMaterial(string directory) => stems.Any(stem => MaterialCandidates(directory, stem).Length > 0);
        void ValidateGroup(string directory)
        {
            foreach (var stem in stems)
            {
                var count = MaterialCandidates(directory, stem).Length;
                if (count != 1) throw new InvalidDataException($"S 素材目录 {directory} 中的 {stem} 必须恰好提供一张 BMP/PNG/JPG/JPEG，不能缺失或同时提供多张。");
            }
        }
        var children = Directory.EnumerateDirectories(root).ToArray();
        var generals = children.FirstOrDefault(path => Path.GetFileName(path).Equals("将领", StringComparison.Ordinal));
        var jobs = children.FirstOrDefault(path => Path.GetFileName(path).Equals("兵种", StringComparison.Ordinal));
        var directGroups = children.Where(HasMaterial).ToArray();
        if (HasMaterial(root))
        {
            if (directGroups.Length > 0 || generals != null || jobs != null)
                throw new InvalidDataException("所选目录同时包含直接动作条和分组素材，请明确选择要追加的单组目录或分类导出根目录。");
            ValidateGroup(root);
            return [root];
        }
        if (Path.GetFileName(root).Equals("兵种", StringComparison.Ordinal))
            throw new InvalidDataException("兵种分类使用固定槽，不能批量追加到将领区；如需复用一组素材，请明确选择该动作条目录。");
        if (generals != null)
        {
            if (directGroups.Length > 0)
                throw new InvalidDataException("所选目录同时包含“将领”分类和未分类动作条组，请选择一种明确的素材来源。");
            return ResolveSAppendDirectories(generals);
        }
        if (jobs != null)
            throw new InvalidDataException("所选导出根目录只有“兵种”分类，不能将兵种固定槽批量追加到将领区。");
        if (directGroups.Length == 0)
            throw new InvalidDataException("所选目录中没有完整 S 动作条；请选择单组素材、包含素材组的目录，或含“将领/S_物理号”的新版导出根目录。");
        foreach (var directory in directGroups) ValidateGroup(directory);
        return directGroups.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string FrameKey(DecodedFrame frame) => FrameKey(frame.Descriptor);
    private static string FrameKey(FrameDescriptor frame)
        => $"{frame.SourceKey}|{frame.ImageNumber}|{frame.FrameIndex}|{frame.Turn}|{frame.Action}|{frame.Direction}";

    private static IReadOnlyList<DecodedFrame> NormalizeLegacySFrames(LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> frames)
    {
        if (asset.Kind != ResourceKind.SComposite || asset.SMapping == null) return frames;
        var result = new List<DecodedFrame>(frames.Count);
        foreach (var frame in frames)
        {
            var binding = asset.SMapping.Turns.FirstOrDefault(turn => turn.ImageNumber == frame.Descriptor.ImageNumber);
            if (binding == null)
            {
                result.Add(frame);
                continue;
            }
            result.Add(frame.Descriptor.Turn == binding.Turn
                ? frame
                : new DecodedFrame(frame.Descriptor with { Turn = binding.Turn }, frame.Bitmap,
                    frame.PaletteIndexes, frame.PaletteBgra));
        }
        return result;
    }
}
