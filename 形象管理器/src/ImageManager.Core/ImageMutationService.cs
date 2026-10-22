namespace ImageManager.Core;

public sealed class ImageMutationService : IImageMutationService
{
    private readonly HistoryService _history;
    private readonly IItemIconDerivationService _itemIcons = new ItemIconDerivationService();

    public ImageMutationService(HistoryService history) => _history = history;

    public MutationPlan PlanAppend(ProjectProfile project, ImageResourceDescriptor resource, IReadOnlyList<string> sources)
    {
        if (ImageImportPreparationService.Supports(resource) && resource.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
            return PrepareFileImport(new ImageImportRequest(project, resource, ImageImportOperation.Append,
                sources.Select((source, index) => new ImageImportAssignment(null,
                    resource.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon ? "large" : "image", source, index)).ToArray()));
        return MutationPlanningSnapshot.Build(resource.Paths, () => PlanAppendCore(project, resource, sources));
    }

    private MutationPlan PlanAppendCore(ProjectProfile project, ImageResourceDescriptor resource, IReadOnlyList<string> sources)
    {
        EnsureWritable(project);
        if (resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
        {
            if (sources.Count == 0) throw new InvalidOperationException("未选择新增动画包。");
            var packages = sources.Select(source => ReadAnimationPackage(project, source)).ToArray();
            var path = resource.Paths.Single();
            return Plan(MutationKind.Append, $"向 Meff 追加 {packages.Length} 个动画包",
                [new MutationFile(path, E5Archive.OpenFresh(path).Rebuild(appendDecoded: packages), "追加经过结构验证的策略动画包")],
                ["新增条目不会自动修改策略表中的 Meff 字段。"], false);
        }
        if (resource.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon)
        {
            if (sources.Count == 0) throw new InvalidOperationException("未选择宝物大图。");
            var bitmaps = sources.Select(path => new Bitmap(path)).ToArray();
            try
            {
                if (resource.Kind == ResourceKind.DllBitmap)
                    return Plan(MutationKind.Append, $"末尾新增 {bitmaps.Length} 个宝物图标",
                        [new MutationFile(resource.Paths.Single(),
                            DllBitmapMutation.BuildAppendItemCandidate(resource.Paths.Single(), bitmaps, _itemIcons),
                            "由大图卷积生成小图并同步追加")],
                        ["小图已由大图使用透明感知 2x2 方框卷积生成。"], false);
                if (resource.Key.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) || resource.Key.Equals("Mtem.e5", StringComparison.OrdinalIgnoreCase))
                {
                    var path = resource.Paths.Single();
                    var itemArchive = E5Archive.OpenFresh(path);
                    var pairedEntryCount = itemArchive.Entries.Count / 2 * 2;
                    if (pairedEntryCount < 2) throw new InvalidDataException("Item.e5 缺少可参考的大小图条目。");
                    var referenceSmall = itemArchive.ReadDecoded(pairedEntryCount - 1);
                    var referenceLarge = itemArchive.ReadDecoded(pairedEntryCount);
                    var itemPayloads = new List<byte[]>(bitmaps.Length * 2);
                    foreach (var bitmap in bitmaps)
                    {
                        var pair = BuildE5ItemPair(referenceSmall, referenceLarge, bitmap);
                        itemPayloads.Add(pair.Small);
                        itemPayloads.Add(pair.Large);
                    }
                    return Plan(MutationKind.Append, $"末尾新增 {bitmaps.Length} 个宝物图标",
                        [new MutationFile(path, AppendItemPairs(itemArchive, itemPayloads), "同步追加 Item 小图/大图，保留未配对尾条目")],
                        ["小图已由大图使用透明感知 2x2 方框卷积生成。"], false);
                }
            }
            finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
        }
        if (resource.Kind == ResourceKind.FaceComposite)
        {
            if (sources.Count == 0) throw new InvalidOperationException("未选择新增头像。");
            var facePath = resource.Paths.First(path => Path.GetFileName(path).Equals("Face.e5", StringComparison.OrdinalIgnoreCase));
            var faceArchive = E5Archive.OpenFresh(facePath);
            if (faceArchive.Entries.Count == 0) throw new InvalidDataException("Face.e5 没有可验证的目标编码模板。");
            var template = faceArchive.ReadDecoded(faceArchive.Entries.Count);
            var facePayloads = sources.Select(source =>
            {
                using var bitmap = BmpPayloadService.Decode(ReadSourceBytes(source));
                return FacePayloadService.Rebuild(project, template, bitmap);
            }).ToArray();
            var files = new List<MutationFile>
            {
                new(facePath, faceArchive.Rebuild(appendDecoded: facePayloads), "按目标编码追加 Face 小头像")
            };
            var touPath = resource.Paths.FirstOrDefault(path => Path.GetFileName(path).Equals("Tou.dll", StringComparison.OrdinalIgnoreCase));
            if (touPath != null)
            {
                var bitmaps = sources.Select(source => new Bitmap(source)).ToArray();
                try { files.Add(new MutationFile(touPath, DllBitmapMutation.BuildAppendSinglesCandidate(touPath, bitmaps), "同步追加 Tou 真彩头像")); }
                finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
            }
            var warnings = touPath == null ? new[] { "Tou.dll 缺失：本次按小头像降级模式只写 Face.e5。" } : Array.Empty<string>();
            return Plan(MutationKind.Append, $"末尾新增 {sources.Count} 个头像", files, warnings, false);
        }
        if (resource.Kind == ResourceKind.DllBitmap)
        {
            var bitmaps = sources.Select(path => new Bitmap(path)).ToArray();
            try
            {
                var path = resource.Paths.Single();
                return Plan(MutationKind.Append, $"向 {resource.DisplayName} 末尾新增 {bitmaps.Length} 个图标",
                    [new MutationFile(path, DllBitmapMutation.BuildAppendNativeSinglesCandidate(path, bitmaps), "按原生 ID 追加独立 RT_BITMAP，保留素材尺寸")],
                    ["每张素材追加一个原生 ID，现有稀疏 ID 保持；新增图标不会自动修改游戏引用。"], false);
            }
            finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
        }
        if (resource.Paths.Count != 1 || resource.Kind == ResourceKind.SComposite)
            throw new NotSupportedException("该多文件资源必须通过对应的同步导入入口新增。");
        var archive = E5Archive.OpenFresh(resource.Paths[0]);
        var payloads = sources.Select(source =>
        {
            var bytes = ReadSourceBytes(source);
            if (!ImageRendering.IsStandardImage(bytes))
                throw new InvalidDataException("普通图片新增仅接受可完整解码的 BMP、PNG 或 JPG；专用载荷请使用对应导入入口：" + source);
            _ = BmpPayloadService.GetStandardImageLength(bytes);
            using var decoded = BmpPayloadService.Decode(bytes);
            // Force complete decoding before any candidate is built. Merely
            // recognizing a PNG/JPEG signature does not establish a valid image.
            _ = BmpPayloadService.ReadPixels(decoded);
            return bytes;
        }).ToArray();
        if (payloads.Length == 0) throw new InvalidOperationException("未选择新增图片。");
        var candidate = archive.Rebuild(appendDecoded: payloads);
        return Plan(MutationKind.Append, $"向 {resource.DisplayName} 末尾新增 {payloads.Length} 个条目",
            [new MutationFile(resource.Paths[0], candidate, "末尾新增，已完整解码素材")],
            ["新增后资源容器会扩容；不自动修改引用编号。普通图片保留素材尺寸与编码；专用动作条须使用对应导入入口。"], false);
    }

    public MutationPlan PlanReplace(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets, IReadOnlyList<string> sources)
    {
        if (assets.Count > 0 && assets.Count == sources.Count &&
            assets.Select(asset => asset.ResourceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 &&
            assets.All(asset => asset.Kind is ResourceKind.E5Indexed or ResourceKind.FaceComposite or ResourceKind.DllBitmap
                or ResourceKind.WeatherSpecial or ResourceKind.PmapSpecial or ResourceKind.MapPair) &&
            assets.All(asset => asset.Category != ImageCategory.StrategyAnimation && !asset.ResourceKey.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase)))
        {
            // The compatibility API accepts multiple resource types, but every static group must
            // use the same prepared conversion as its single-resource UI and commit atomically.
            var plans = assets.Zip(sources).GroupBy(pair => pair.First.ResourceKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => PlanReplace(project, group.Select(pair => pair.First).ToArray(), group.Select(pair => pair.Second).ToArray())).ToArray();
            var files = plans.SelectMany(plan => plan.Files).ToArray();
            if (files.GroupBy(file => Path.GetFullPath(file.TargetPath), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidDataException("跨资源替换含重复写入档案，请将共享同一档案的目标合并为一次映射。");
            var snapshots = new Dictionary<string, MutationSourceSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in plans.SelectMany(plan => plan.SourceSnapshots ?? new Dictionary<string, MutationSourceSnapshot>()))
            {
                if (snapshots.TryGetValue(dependency.Key, out var previous) && previous != dependency.Value)
                    throw new IOException("准备跨资源候选期间依赖已变化，请重新导入：" + dependency.Key);
                dependency.Value.Verify(dependency.Key);
                snapshots[dependency.Key] = dependency.Value;
            }
            return new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Replace, $"按目标规格替换{assets.Count}个图片条目", files,
                plans.SelectMany(plan => plan.Warnings).Distinct().ToArray(), plans.Any(plan => plan.RequiresRiskConfirmation),
                new Dictionary<string, string> { ["ImportProtocol"] = "raster-file-v1", ["ResourceGroups"] = plans.Length.ToString() }, snapshots);
        }
        if (assets.Count > 0 && assets.Count == sources.Count &&
            assets.Select(asset => asset.ResourceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            var first = assets[0];
            var resource = new ImageResourceDescriptor(first.ResourceKey, first.ResourceKey, first.Category, first.Kind,
                assets.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                assets.Count, true, true, first.IsExperimental, first.Status, "");
            if (ImageImportPreparationService.Supports(resource) && first.Kind is not (ResourceKind.RComposite or ResourceKind.SComposite))
                return PrepareFileImport(new ImageImportRequest(project, resource, ImageImportOperation.Replace,
                    assets.Zip(sources).Select(pair => new ImageImportAssignment(pair.First,
                        pair.First.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon ? "large" : "image", pair.Second)).ToArray()));
        }
        return MutationPlanningSnapshot.Build(assets.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath),
            () => PlanReplaceCore(project, assets, sources));
    }

    private static MutationPlan PrepareFileImport(ImageImportRequest request)
    {
        using var prepared = new ImageImportPreparationService().PrepareAsync(request).GetAwaiter().GetResult();
        if (!prepared.CanCommit)
            throw new InvalidDataException(string.Join(Environment.NewLine,
                prepared.Rows.Where(row => row.Error != null).Select(row => row.TargetLabel + "：" + row.Error)));
        return prepared.Plan!;
    }

    private MutationPlan PlanReplaceCore(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets, IReadOnlyList<string> sources)
    {
        EnsureWritable(project);
        if (assets.Count == 0) throw new InvalidOperationException("未选择待替换图片。");
        if (assets.Count != sources.Count) throw new InvalidOperationException("普通替换要求素材数与选择数一致；R/S 多条目请使用动作条替换。");
        if (assets.All(asset => asset.ResourceKey.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase)))
        {
            var packages = sources.Select(source => ReadAnimationPackage(project, source)).ToArray();
            var path = assets[0].Frames.Single().SourcePath;
            if (assets.Any(asset => !asset.Frames.Single().SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("一次替换只能处理同一 Meff 档案内的动画条目。");
            var replacements = assets.Zip(packages).ToDictionary(pair => pair.First.Frames.Single().ImageNumber, pair => pair.Second);
            return Plan(MutationKind.Replace, $"替换 {assets.Count} 个 Meff 动画包",
                [new MutationFile(path, E5Archive.OpenFresh(path).Rebuild(replacementDecoded: replacements), "替换经过结构验证的策略动画包")],
                Array.Empty<string>(), false);
        }
        if (assets.All(asset => asset.Kind == ResourceKind.WeatherSpecial))
        {
            var weatherFiles = assets.Zip(sources)
                .GroupBy(pair => pair.First.Frames[0].SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var archive = E5Archive.OpenFresh(group.Key);
                    var replacements = new Dictionary<int, byte[]>();
                    foreach (var pair in group)
                    {
                        var descriptor = pair.First.Frames[0];
                        using var strip = BmpPayloadService.Decode(ReadSourceBytes(pair.Second));
                        replacements.Add(descriptor.ImageNumber, WeatherResourceCodec.RebuildPayload(project,
                            archive.ReadDecoded(descriptor.ImageNumber), strip));
                    }
                    return new MutationFile(group.Key, archive.Rebuild(replacementDecoded: replacements), "替换天气完整纵向动作条");
                }).ToArray();
            return Plan(MutationKind.Replace, $"替换 {assets.Count} 个天气动作条", weatherFiles,
                ["保留天气帧顺序和原调色板；一张素材对应一个完整纵向动画条。"], false);
        }
        if (assets.All(asset => asset.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon))
        {
            var bitmaps = sources.Select(path => new Bitmap(path)).ToArray();
            try { return PlanItemIconReplaceMany(assets, bitmaps); }
            finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
        }
        if (assets.All(asset => asset.Kind == ResourceKind.FaceComposite))
        {
            if (assets.Any(asset => asset.IsFixedSlot)) throw new InvalidOperationException("Face 0 八表情组请逐帧像素编辑，不能作为普通单图替换。");
            var facePath = assets[0].Frames.First(frame => frame.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase)).SourcePath;
            var faceArchive = E5Archive.OpenFresh(facePath);
            var replacements = assets.Zip(sources).ToDictionary(pair =>
                pair.First.Frames.First(frame => frame.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase)).ImageNumber,
                pair =>
                {
                    using var bitmap = BmpPayloadService.Decode(ReadSourceBytes(pair.Second));
                    return FacePayloadService.Rebuild(project,
                        faceArchive.ReadDecoded(pair.First.Frames.First(frame => frame.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase)).ImageNumber), bitmap);
                });
            var faceFiles = new List<MutationFile>
            {
                new(facePath, E5Archive.OpenFresh(facePath).Rebuild(replacementDecoded: replacements), "替换 Face 小头像")
            };
            var touFrames = assets.Select(asset => asset.Frames.FirstOrDefault(frame => frame.SourceKey.Equals("Tou.dll", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (touFrames.All(frame => frame != null))
            {
                var touPath = touFrames[0]!.SourcePath;
                var bitmaps = sources.Select(source => new Bitmap(source)).ToArray();
                try
                {
                    var dllReplacements = touFrames.Zip(bitmaps).Select(pair =>
                        (pair.First!.ImageNumber, pair.First.SourceOffset, pair.Second, pair.First.Width, pair.First.Height)).ToArray();
                    faceFiles.Add(new MutationFile(touPath, DllBitmapMutation.BuildReplacementCandidate(touPath, dllReplacements), "同步替换 Tou 真彩头像"));
                }
                finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
            }
            return Plan(MutationKind.Replace, $"替换 {assets.Count} 个头像", faceFiles,
                touFrames.Any(frame => frame == null) ? ["Tou.dll 缺失：按小头像降级模式写入。"] : Array.Empty<string>(), false);
        }
        if (assets.All(asset => asset.Kind == ResourceKind.DllBitmap))
        {
            var path = assets[0].Frames[0].SourcePath;
            if (assets.Any(asset => !asset.Frames[0].SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("一次 DLL 替换只能处理同一个资源文件。");
            var bitmaps = sources.Select(source => new Bitmap(source)).ToArray();
            try
            {
                var replacements = assets.Zip(bitmaps).SelectMany(pair => pair.First.Frames.Select(frame =>
                    (frame.ImageNumber, frame.SourceOffset, pair.Second, frame.Width, frame.Height))).ToArray();
                return Plan(MutationKind.Replace, $"替换 {assets.Count} 个 DLL 图标",
                    [new MutationFile(path, DllBitmapMutation.BuildReplacementCandidate(path, replacements), "替换 RT_BITMAP")],
                    Array.Empty<string>(), false);
            }
            finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
        }
        var grouped = assets.Zip(sources).GroupBy(pair => pair.First.Frames.Single().SourcePath, StringComparer.OrdinalIgnoreCase);
        var files = new List<MutationFile>();
        foreach (var group in grouped)
        {
            var archive = E5Archive.OpenFresh(group.Key);
            var replacements = group.ToDictionary(pair => pair.First.Frames.Single().ImageNumber,
                pair => RebuildImagePayload(project, archive.ReadDecoded(pair.First.Frames.Single().ImageNumber),
                    pair.First.Frames.Single(), pair.Second));
            files.Add(new MutationFile(group.Key, archive.Rebuild(replacementDecoded: replacements), "批量替换 E5 条目"));
        }
        return Plan(MutationKind.Replace, $"替换 {assets.Count} 个图片条目", files, Array.Empty<string>(), false);
    }

    public MutationPlan PlanReplaceSingleFrame(ProjectProfile project, LogicalImageAsset asset,
        FrameDescriptor descriptor, Bitmap edited, bool appendCopy = false)
        => MutationPlanningSnapshot.Build(asset.Frames.Select(frame => frame.SourcePath),
            () => PlanReplaceSingleFrameCore(project, asset, descriptor, edited, appendCopy));

    private MutationPlan PlanReplaceSingleFrameCore(ProjectProfile project, LogicalImageAsset asset,
        FrameDescriptor descriptor, Bitmap edited, bool appendCopy)
    {
        EnsureWritable(project);
        var weatherFrame = asset.Kind == ResourceKind.WeatherSpecial && descriptor.FrameIndex is >= 0 and < WeatherResourceCodec.FrameCount &&
            asset.Frames.Any(frame => frame.SourcePath.Equals(descriptor.SourcePath, StringComparison.OrdinalIgnoreCase) && frame.ImageNumber == descriptor.ImageNumber);
        if (!weatherFrame && !asset.Frames.Any(frame => AiAtlasNormalizer.FrameKey(frame) == AiAtlasNormalizer.FrameKey(descriptor)))
            throw new InvalidOperationException("待编辑帧不属于当前形象。");
        if (asset.Kind is ResourceKind.RComposite or ResourceKind.SComposite)
            return appendCopy ? new RsImportService().PlanAppendCopy(project, asset, descriptor, edited)
                : new RsImportService().PlanPixelEdit(project, asset, descriptor, edited);
        if (asset.Kind is ResourceKind.DllBitmap or ResourceKind.FaceComposite)
            throw new InvalidOperationException("头像及 DLL 资源应通过对应的成对编辑入口写入。");
        var archive = E5Archive.OpenFresh(descriptor.SourcePath);
        var original = archive.ReadDecoded(descriptor.ImageNumber);
        if (asset.Kind == ResourceKind.WeatherSpecial && appendCopy)
            throw new InvalidOperationException("天气使用固定逻辑编号，不能将一个动画帧追加为新的天气；请覆盖当前帧或替换完整动作条。");
        var replacement = asset.Kind == ResourceKind.WeatherSpecial
            ? WeatherResourceCodec.ReplaceFrame(project, original, descriptor.FrameIndex, edited)
            : RebuildImagePayload(project, original, descriptor, edited);
        var candidate = appendCopy ? archive.Rebuild(appendDecoded: [replacement])
            : archive.Rebuild(replacementDecoded: new Dictionary<int, byte[]> { [descriptor.ImageNumber] = replacement });
        return Plan(appendCopy ? MutationKind.Append : MutationKind.PixelEdit,
            appendCopy ? $"复制并追加 {asset.DisplayName}" : $"编辑 {asset.DisplayName} 第 {descriptor.FrameIndex + 1} 帧",
            [new MutationFile(descriptor.SourcePath,
                candidate,
                "保持图片编码、调色板及其他帧")], Array.Empty<string>(), false);
    }

    private static byte[] RebuildImagePayload(ProjectProfile project, byte[] original,
        FrameDescriptor descriptor, string sourcePath)
    {
        var source = ReadSourceBytes(sourcePath);
        if (!ImageRendering.IsStandardImage(source))
            throw new InvalidDataException("普通图片替换只接受可验证的 BMP、PNG 或 JPG；动画包请使用专用替换入口。");
        using var image = BmpPayloadService.Decode(source);
        return RebuildImagePayload(project, original, descriptor, image);
    }

    private static byte[] RebuildImagePayload(ProjectProfile project, byte[] original,
        FrameDescriptor descriptor, Bitmap edited)
    {
        if (ImageRendering.IsStandardImage(original)) return BmpPayloadService.Rebuild(original, edited);
        if (descriptor.Width > 0 && descriptor.Height > 0 && edited.Width == descriptor.Width && edited.Height == descriptor.Height &&
            (original.Length == edited.Width * edited.Height || original.Length == edited.Width * edited.Height + 2))
            return BmpPayloadService.RebuildRaw8(original, edited.Width, edited.Height, edited,
                new PaletteService().RequireWritePalette(project));
        throw new InvalidDataException($"{descriptor.SourceKey} 的条目不是已确认的标准图片或 RAW 像素平面，不能用普通图片覆盖其专用结构。");
    }

    public MutationPlan PlanDelete(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets)
        => MutationPlanningSnapshot.Build(assets.SelectMany(asset => asset.Frames).Select(frame => frame.SourcePath),
            () => PlanDeleteCore(project, assets));

    private MutationPlan PlanDeleteCore(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets)
    {
        EnsureWritable(project);
        if (assets.Count == 0) throw new InvalidOperationException("未选择待删除图片。");
        foreach (var asset in assets.Where(asset => asset.Kind == ResourceKind.SComposite))
        {
            var physical = SResourceExchangeNaming.PhysicalId(asset);
            if ((asset.SMode == SImageMode.Job) != (physical <= 240))
                throw new InvalidDataException("S 形象类型与物理图号不一致，不能执行删除。");
        }
        if (assets.All(asset => asset.Kind == ResourceKind.SComposite && asset.SMode == SImageMode.Job))
            return PlanClearJobSlots(assets);
        if (assets.Any(asset => asset.IsFixedSlot)) throw new InvalidOperationException("选择中包含固定结构槽，不能物理删除。");
        var files = new List<MutationFile>();
        foreach (var dllGroup in assets.Where(asset => asset.Kind == ResourceKind.DllBitmap)
                     .GroupBy(asset => asset.Frames[0].SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var ids = dllGroup.SelectMany(asset => asset.Frames).Select(frame => frame.ImageNumber).ToHashSet();
            var paired = dllGroup.All(asset => asset.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon);
            files.Add(new MutationFile(dllGroup.Key, paired
                    ? DllBitmapMutation.BuildCompactCandidate(dllGroup.Key, ids)
                    : DllBitmapMutation.BuildDeleteNativeCandidate(dllGroup.Key, ids),
                paired ? "删除 RT_BITMAP 并前移后续载荷" : "仅删除所选原生 RT_BITMAP ID，其他 ID 保持"));
        }
        foreach (var faceGroup in assets.Where(asset => asset.Kind == ResourceKind.FaceComposite)
                     .GroupBy(asset => asset.Frames.First(frame => frame.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase)).SourcePath,
                         StringComparer.OrdinalIgnoreCase))
        {
            var faceNumbers = faceGroup.Select(asset => asset.Frames.First(frame => frame.SourceKey.Equals("Face.e5", StringComparison.OrdinalIgnoreCase)).ImageNumber).ToHashSet();
            files.Add(new MutationFile(faceGroup.Key, E5Archive.OpenFresh(faceGroup.Key).Rebuild(deleteImageNumbers: faceNumbers),
                "删除 Face 小头像并前移"));
            var touFrames = faceGroup.SelectMany(asset => asset.Frames).Where(frame => frame.SourceKey.Equals("Tou.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (touFrames.Length > 0)
            {
                var touPath = touFrames[0].SourcePath;
                files.Add(new MutationFile(touPath, DllBitmapMutation.BuildCompactCandidate(touPath,
                    touFrames.Select(frame => frame.ImageNumber).ToHashSet()), "同步删除 Tou 真彩头像并前移"));
            }
        }
        var deleteByPath = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.Where(asset => asset.Kind is not (ResourceKind.DllBitmap or ResourceKind.FaceComposite)))
        foreach (var frame in asset.Frames.GroupBy(frame => (frame.SourcePath, frame.ImageNumber)).Select(group => group.First()))
        {
            if (!deleteByPath.TryGetValue(frame.SourcePath, out var numbers)) deleteByPath[frame.SourcePath] = numbers = [];
            numbers.Add(frame.ImageNumber);
        }
        files.AddRange(deleteByPath.Select(pair => new MutationFile(pair.Key,
            E5Archive.OpenFresh(pair.Key).Rebuild(deleteImageNumbers: pair.Value.OrderByDescending(value => value).ToArray()),
            "删除条目并前移后续编号")));
        var references = assets.SelectMany(asset => asset.ReferencedBy).Distinct().ToArray();
        var nativeIdsOnly = assets.All(asset => asset.Kind == ResourceKind.DllBitmap &&
            asset.Category is not (ImageCategory.ItemIcon or ImageCategory.StrategyIcon));
        var warnings = new List<string>
        {
            nativeIdsOnly ? "仅删除所选原生 RT_BITMAP ID，其他稀疏 ID 与内容保持；游戏对已删除 ID 的引用将缺少图像。"
                : "删除只重排资源，不更新 Data、物品、策略、脚本或地图引用；后续编号将前移。"
        };
        warnings.AddRange(references.Select(reference => "可能错位引用：" + reference));
        if (assets.All(asset => asset.Kind == ResourceKind.SComposite && asset.SMode == SImageMode.Person))
        {
            var deleted = assets.Select(SResourceExchangeNaming.PhysicalId).Distinct().Order().ToArray();
            warnings.Insert(0, "仅删除所选物理 Unit 图号：" + string.Join("、", deleted) + "；每项在 mov/atk/spc 各删除一条。");
            foreach (var pair in deleteByPath)
            {
                var count = E5Archive.OpenFresh(pair.Key).Entries.Count;
                var survivors = Enumerable.Range(deleted[0], count - deleted[0] + 1)
                    .Where(number => !pair.Value.Contains(number)).ToArray();
                warnings.Add(survivors.Length == 0 ? $"{Path.GetFileName(pair.Key)}：没有后续图号前移。" :
                    $"{Path.GetFileName(pair.Key)}：原物理 #{survivors[0]}～#{survivors[^1]} 中未删除的条目前移；Data S 引用不自动调整。");
            }
        }
        return Plan(MutationKind.Delete, nativeIdsOnly ? $"删除 {assets.Count} 个原生图标 ID，其他编号保持" : $"删除 {assets.Count} 个逻辑图片并重排", files, warnings, true,
            new Dictionary<string, string> { ["ReferencePolicy"] = nativeIdsOnly ? "delete-native-ids-only" : "reorder-only" });
    }

    private static MutationPlan PlanClearJobSlots(IReadOnlyList<LogicalImageAsset> assets)
    {
        // Job S slots occupy physical 1..240. Removing a physical
        // entry would shift every later job and character; deletion therefore
        // clears the selected slot in all three archives without renumbering.
        var files = assets.SelectMany(asset => asset.Frames)
            .GroupBy(frame => frame.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var archive = E5Archive.OpenFresh(group.Key);
                var replacements = new Dictionary<int, byte[]>();
                foreach (var slot in group.GroupBy(frame => frame.ImageNumber))
                {
                    var first = slot.First();
                    var height = first.Height * (slot.Max(frame => frame.FrameIndex) + 1);
                    var indexes = new byte[checked(first.Width * height)];
                    var original = archive.ReadDecoded(slot.Key);
                    byte[] cleared;
                    if (ImageRendering.TryReplace8BppBmpIndexes(original, first.Width, height, indexes, out var bmp)) cleared = bmp;
                    else if (!ImageRendering.IsStandardImage(original) && original.Length >= indexes.Length)
                    {
                        cleared = original.ToArray();
                        indexes.CopyTo(cleared, 0);
                    }
                    else throw new InvalidDataException("兵种 S 固定槽不是已验证的 RAW/8bpp BMP，不能安全清空：" + group.Key);
                    replacements.Add(slot.Key, cleared);
                }
                return new MutationFile(group.Key, archive.Rebuild(replacementDecoded: replacements), "清空兵种 S 固定槽，保持编号");
            }).ToArray();
        return Plan(MutationKind.Delete, $"清空 {assets.Count} 个兵种 S 形象", files,
            ["清空物理 Unit 图号：" + string.Join("、", assets.Select(SResourceExchangeNaming.PhysicalId).Order()) + "。",
             "仅清空所选兵种 S 的 mov/atk/spc 固定槽；不删除物理条目，不前移其他兵种或将领编号。"], true,
            new Dictionary<string, string> { ["ReferencePolicy"] = "fixed-slot-clear" });
    }

    public MutationPlan PlanWholeFileReplace(ProjectProfile project, string targetPath, string sourcePath)
        => MutationPlanningSnapshot.Build([targetPath, sourcePath], () => PlanWholeFileReplaceCore(project, targetPath, sourcePath));

    private MutationPlan PlanWholeFileReplaceCore(ProjectProfile project, string targetPath, string sourcePath)
    {
        EnsureWritable(project);
        if (!File.Exists(targetPath)) throw new FileNotFoundException("目标文件不存在。", targetPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("来源文件不存在。", sourcePath);
        return Plan(MutationKind.WholeFileReplace, "替换整个资源文件",
            [new MutationFile(Path.GetFullPath(targetPath), File.ReadAllBytes(sourcePath), "整文件替换")],
            ["整文件替换会保留完整写前快照。"], true);
    }

    public MutationPlan PlanItemIconEdit(ProjectProfile project, LogicalImageAsset asset, Bitmap large, bool appendCopy)
        => MutationPlanningSnapshot.Build(asset.Frames.Select(frame => frame.SourcePath),
            () => PlanItemIconEditCore(project, asset, large, appendCopy));

    private MutationPlan PlanItemIconEditCore(ProjectProfile project, LogicalImageAsset asset, Bitmap large, bool appendCopy)
    {
        EnsureWritable(project);
        if (asset.Category is not (ImageCategory.ItemIcon or ImageCategory.StrategyIcon)) throw new InvalidOperationException("该资源不是成对图标。");
        if (!appendCopy) return PlanItemIconReplaceMany([asset], [large]);
        if (asset.Kind == ResourceKind.DllBitmap)
            return Plan(MutationKind.Append, "复制并追加 " + asset.DisplayName,
                [new MutationFile(asset.Frames[0].SourcePath,
                    DllBitmapMutation.BuildAppendItemCandidate(asset.Frames[0].SourcePath, [large], _itemIcons),
                    "由大图卷积生成小图并同步追加")],
                ["小图已由大图自动派生；新编号不会自动写入物品表。"], false);
        var smallFrame = asset.Frames.Single(frame => frame.Action == "small");
        var largeFrame = asset.Frames.Single(frame => frame.Action == "large");
        var archive = E5Archive.OpenFresh(largeFrame.SourcePath);
        var pair = BuildE5ItemPair(archive.ReadDecoded(smallFrame.ImageNumber), archive.ReadDecoded(largeFrame.ImageNumber), large);
        return Plan(MutationKind.Append, "复制并追加 " + asset.DisplayName,
            [new MutationFile(largeFrame.SourcePath, AppendItemPairs(archive, [pair.Small, pair.Large]),
                "由大图卷积生成小图并同步追加")],
            ["小图已由大图自动派生；新编号不会自动写入物品表。"], false);
    }

    private static byte[] AppendItemPairs(E5Archive archive, IReadOnlyList<byte[]> pairedPayloads)
    {
        if (pairedPayloads.Count == 0 || pairedPayloads.Count % 2 != 0)
            throw new InvalidDataException("Item.e5 新增载荷必须是完整的小图/大图对。");
        if (archive.Entries.Count % 2 == 0) return archive.Rebuild(appendDecoded: pairedPayloads);
        // Real 6.6 archives can end in an unmatched entry. Keep every complete
        // pair at its existing ID and preserve the original stored tail bytes.
        return archive.Rebuild(insertBeforeDecoded: new Dictionary<int, IReadOnlyList<byte[]>>
        {
            [archive.Entries.Count] = pairedPayloads
        });
    }

    public MutationResult Commit(ProjectProfile project, MutationPlan plan)
    {
        EnsureWritable(project);
        if (plan.Files.Count == 0) throw new InvalidOperationException("事务没有目标文件。");
        var duplicate = plan.Files.GroupBy(file => Path.GetFullPath(file.TargetPath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null) throw new InvalidOperationException("事务包含重复目标：" + duplicate.Key);

        var snapshotDirectory = _history.SnapshotDirectory(project, plan.OperationId);
        var targets = new List<HistoryTarget>();
        var staged = new List<(MutationFile File, string? TempPath)>();
        var written = new List<HistoryTarget>();
        try
        {
            if (plan.SourceSnapshots != null)
                foreach (var source in plan.SourceSnapshots) source.Value.Verify(source.Key);
            foreach (var file in plan.Files)
            {
                var targetPath = Path.GetFullPath(file.TargetPath);
                file.ExpectedSource.Verify(targetPath);
                var beforeExists = File.Exists(targetPath);
                if (file.DeleteTarget && !beforeExists) throw new FileNotFoundException("待删除资源不存在。", targetPath);
                if (!beforeExists && !file.AllowCreate) throw new FileNotFoundException("目标资源不存在。", targetPath);
                if (!file.DeleteTarget) ValidateCandidate(project, targetPath, file.CandidateBytes);
                var safeName = targets.Count.ToString("D3") + "_" + Path.GetFileName(targetPath);
                var beforeSnapshot = Path.Combine(snapshotDirectory, safeName + ".before");
                var afterSnapshot = Path.Combine(snapshotDirectory, safeName + ".after");
                if (beforeExists) File.Copy(targetPath, beforeSnapshot, false);
                if (beforeExists) File.SetAttributes(beforeSnapshot, FileAttributes.Normal);
                if (beforeExists) ResourceFileRecovery.Verify(beforeSnapshot, true, file.ExpectedSource.Length, file.ExpectedSource.Sha256);
                if (!file.DeleteTarget) File.WriteAllBytes(afterSnapshot, file.CandidateBytes);
                string? temporary = null;
                if (!file.DeleteTarget)
                {
                    temporary = targetPath + ".imagemanager." + plan.OperationId + ".tmp";
                    staged.Add((file, temporary));
                    File.WriteAllBytes(temporary, file.CandidateBytes);
                }
                else staged.Add((file, null));
                targets.Add(new HistoryTarget
                {
                    TargetPath = targetPath,
                    SnapshotPath = beforeSnapshot,
                    AfterSnapshotPath = afterSnapshot,
                    BeforeSha256 = beforeExists ? HistoryService.HashFile(beforeSnapshot) : string.Empty,
                    AfterSha256 = file.DeleteTarget ? string.Empty : HistoryService.HashFile(afterSnapshot),
                    BeforeLength = beforeExists ? new FileInfo(beforeSnapshot).Length : 0,
                    AfterLength = file.DeleteTarget ? 0 : file.CandidateBytes.LongLength,
                    BeforeExists = beforeExists,
                    AfterExists = !file.DeleteTarget,
                    BeforeAttributes = beforeExists ? File.GetAttributes(targetPath) : null,
                    AfterAttributes = beforeExists ? File.GetAttributes(targetPath) : FileAttributes.Normal
                });
            }

            // Check every dependency again after staging, before the first real replacement.
            if (plan.SourceSnapshots != null)
                foreach (var source in plan.SourceSnapshots) source.Value.Verify(source.Key);
            for (var index = 0; index < staged.Count; index++)
            {
                var target = targets[index];
                ResourceFileRecovery.Verify(target.TargetPath, target.BeforeExists, target.BeforeLength, target.BeforeSha256);
                written.Add(target);
                if (target.BeforeExists && target.BeforeAttributes is { } attributes && (attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(target.TargetPath, attributes & ~FileAttributes.ReadOnly);
                if (staged[index].File.DeleteTarget) File.Delete(target.TargetPath);
                else File.Move(staged[index].TempPath!, target.TargetPath, true);
                if (target.AfterExists && target.AfterAttributes.HasValue)
                    File.SetAttributes(target.TargetPath, target.AfterAttributes.Value);
                E5Archive.Invalidate(target.TargetPath);
                ResourceFileRecovery.Verify(target.TargetPath, target.AfterExists, target.AfterLength, target.AfterSha256);
                if (target.AfterExists) ValidateFile(project, target.TargetPath);
            }

            // History persistence is part of the transaction. A locked/full journal
            // must restore the resources rather than report failure after changing them.
            var history = _history.Record(project, plan, targets);
            return new MutationResult(plan.OperationId, true, plan.Summary, history.Id,
                targets.Select(target => target.TargetPath).ToArray(), plan.Warnings);
        }
        catch (Exception ex)
        {
            var recoveryErrors = ResourceFileRecovery.RestoreAll(written, useAfter: false);
            return new MutationResult(plan.OperationId, false, plan.Summary, null,
                recoveryErrors.Count == 0 ? Array.Empty<string>() : written.Select(target => target.TargetPath).ToArray(),
                [ex.Message, .. recoveryErrors,
                    written.Count == 0 ? "未写入资源。" : recoveryErrors.Count == 0 ? "已逐文件恢复写前状态并核对长度与 SHA-256。"
                        : "部分文件无法恢复；写前/写后快照保留于：" + snapshotDirectory]);
        }
        finally
        {
            foreach (var item in staged)
                if (item.TempPath != null) ResourceFileRecovery.TryDelete(item.TempPath);
        }
    }

    private static MutationPlan Plan(MutationKind kind, string summary, IReadOnlyList<MutationFile> files,
        IReadOnlyList<string> warnings, bool risk, IReadOnlyDictionary<string, string>? metadata = null)
        => new(Guid.NewGuid().ToString("N"), kind, summary, files, warnings, risk, metadata);

    private MutationPlan PlanItemIconReplaceMany(IReadOnlyList<LogicalImageAsset> assets, IReadOnlyList<Bitmap> largeBitmaps)
    {
        if (assets.Count != largeBitmaps.Count || assets.Count == 0) throw new InvalidOperationException("宝物大图数量与选择数不一致。");
        if (assets.Select(asset => asset.Kind).Distinct().Count() != 1)
            throw new InvalidOperationException("一次只能替换同一种宝物图标资源。");
        if (assets[0].Kind == ResourceKind.DllBitmap)
        {
            var path = assets[0].Frames[0].SourcePath;
            var replacements = new List<(int Id, int Language, Bitmap Bitmap, int Width, int Height)>();
            var derived = new List<Bitmap>();
            try
            {
                for (var index = 0; index < assets.Count; index++)
                {
                    var small = assets[index].Frames.Single(frame => frame.Action == "small");
                    var large = assets[index].Frames.Single(frame => frame.Action == "large");
                    if (largeBitmaps[index].Size != new Size(large.Width, large.Height))
                        throw new InvalidDataException($"{assets[index].DisplayName} 大图尺寸必须为 {large.Width}x{large.Height}。");
                    var smallBitmap = _itemIcons.DeriveSmall(largeBitmaps[index], new Size(small.Width, small.Height), Color.Blue);
                    derived.Add(smallBitmap);
                    replacements.Add((small.ImageNumber, small.SourceOffset, smallBitmap, small.Width, small.Height));
                    replacements.Add((large.ImageNumber, large.SourceOffset, largeBitmaps[index], large.Width, large.Height));
                }
                return Plan(MutationKind.Replace, $"替换 {assets.Count} 个宝物大图并派生小图",
                    [new MutationFile(path, DllBitmapMutation.BuildReplacementCandidate(path, replacements), "同步替换 RT_BITMAP 大小图")],
                    ["小图已由大图使用透明感知 2x2 方框卷积生成。"], false);
            }
            finally { foreach (var bitmap in derived) bitmap.Dispose(); }
        }

        var sourcePath = assets[0].Frames[0].SourcePath;
        var archive = E5Archive.OpenFresh(sourcePath);
        var e5Replacements = new Dictionary<int, byte[]>();
        for (var index = 0; index < assets.Count; index++)
        {
            var small = assets[index].Frames.Single(frame => frame.Action == "small");
            var large = assets[index].Frames.Single(frame => frame.Action == "large");
            var pair = BuildE5ItemPair(archive.ReadDecoded(small.ImageNumber), archive.ReadDecoded(large.ImageNumber), largeBitmaps[index]);
            e5Replacements[small.ImageNumber] = pair.Small;
            e5Replacements[large.ImageNumber] = pair.Large;
        }
        return Plan(MutationKind.Replace, $"替换 {assets.Count} 个宝物大图并派生小图",
            [new MutationFile(sourcePath, archive.Rebuild(replacementDecoded: e5Replacements), "同步替换 Item.e5 大小图")],
            ["小图已由大图使用透明感知 2x2 方框卷积生成。"], false);
    }

    private (byte[] Small, byte[] Large) BuildE5ItemPair(byte[] originalSmall, byte[] originalLarge, Bitmap sourceLarge)
    {
        using var decodedSmall = ImageRendering.DecodeStandardImage(originalSmall);
        using var decodedLarge = ImageRendering.DecodeStandardImage(originalLarge);
        if (sourceLarge.Size != decodedLarge.Size)
            throw new InvalidDataException($"宝物大图尺寸必须为 {decodedLarge.Width}x{decodedLarge.Height}。");
        var largeKey = ItemIconDerivationService.DetectKeyColor(decodedLarge, false);
        var smallKey = ItemIconDerivationService.DetectKeyColor(decodedSmall, false);
        using var derivedSmall = _itemIcons.DeriveSmall(sourceLarge, decodedSmall.Size, largeKey);
        return (_itemIcons.EncodeStandardImageLike(originalSmall, derivedSmall, smallKey),
            _itemIcons.EncodeStandardImageLike(originalLarge, sourceLarge, largeKey));
    }

    private static byte[] ReadSourceBytes(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到图片素材。", path);
        return File.ReadAllBytes(path);
    }

    private static byte[] ReadAnimationPackage(ProjectProfile project, string path)
    {
        var bytes = ReadSourceBytes(path);
        _ = StrategyAnimationPackageCodec.Probe(bytes);
        using var decoded = new StrategyAnimationPackageCodec().Decode(project, bytes);
        return bytes;
    }

    private static void EnsureWritable(ProjectProfile project)
    {
        if (project.IsReadOnly) throw new InvalidOperationException("当前项目版本无法可靠识别，已禁止写入。");
    }

    private static void ValidateCandidate(ProjectProfile project, string targetPath, byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidDataException("候选文件为空。");
        if (Path.GetFileName(targetPath).StartsWith("Mcall", StringComparison.OrdinalIgnoreCase))
        {
            ValidateStandaloneAnimation(bytes);
            return;
        }
        if (Path.GetFileName(targetPath).Equals("Mark.e5", StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetFileName(targetPath).Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
        {
            ValidateMeffArchive(project, E5Archive.Parse(targetPath, bytes));
            return;
        }
        if (Path.GetExtension(targetPath).Equals(".e5", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(targetPath).Equals("Data.e5", StringComparison.OrdinalIgnoreCase))
            _ = E5Archive.Parse(targetPath, bytes);
    }

    private static void ValidateFile(ProjectProfile project, string path)
    {
        if (Path.GetFileName(path).StartsWith("Mcall", StringComparison.OrdinalIgnoreCase))
        {
            ValidateStandaloneAnimation(File.ReadAllBytes(path));
            return;
        }
        if (Path.GetFileName(path).Equals("Mark.e5", StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetFileName(path).Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
        {
            ValidateMeffArchive(project, E5Archive.OpenFresh(path));
            return;
        }
        if (Path.GetExtension(path).Equals(".e5", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(path).Equals("Data.e5", StringComparison.OrdinalIgnoreCase))
            _ = E5Archive.OpenFresh(path);
    }

    private static void ValidateMeffArchive(ProjectProfile project, E5Archive archive)
    {
        var codec = new StrategyAnimationPackageCodec();
        foreach (var entry in archive.Entries)
        {
            try
            {
                var bytes = archive.ReadDecoded(entry.ImageNumber);
                _ = StrategyAnimationPackageCodec.Probe(bytes);
                using var decoded = codec.Decode(project, bytes);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            { throw new InvalidDataException($"Meff 动画条目 {entry.ImageNumber - 1} 结构无效：{ex.Message}", ex); }
        }
    }

    private static void ValidateStandaloneAnimation(byte[] bytes)
    {
        if (bytes.Length < 40) throw new InvalidDataException("Mcall 动画包过短。");
        var records = bytes[0] + 1;
        var planes = bytes[1];
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        if (records <= 1 || planes <= 0 || width <= 0 || height <= 0 || width > 640 || height > 640)
            throw new InvalidDataException("Mcall 动画包头部无效。");
        var minimum = checked(records * 20L + (long)planes * width * height);
        if (minimum > bytes.LongLength) throw new InvalidDataException("Mcall 动画包帧平面不完整。");
        for (var record = 1; record < records; record++)
            if (bytes[record * 20] >= planes) throw new InvalidDataException($"Mcall 播放记录 {record} 引用越界。");
    }
}
