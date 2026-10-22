namespace ImageManager.Core;

public sealed class ImageCatalogService : IImageCatalogService
{
    private static readonly HashSet<string> ExcludedE5 = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data.e5", "Imsg.e5", "Star.e5", "Hexzmap.e5", "Font.e5", "SETTING.E5", "Spalet.e5", "Pmpalet.e5"
    };

    private readonly IReadOnlyList<IResourceCodec> _codecs;
    private readonly object _catalogSync = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, ImageResourceDescriptor>> _catalogSnapshots =
        new(StringComparer.OrdinalIgnoreCase);

    public ImageCatalogService()
    {
        _codecs = [new RResourceCodec(), new SResourceCodec(), new FaceResourceCodec(), new DllBitmapResourceCodec(),
            new StrategyAnimationResourceCodec(), new MapPairResourceCodec(), new PairedE5IconCodec(),
            new PmapResourceCodec(), new WeatherResourceCodec(), new DtAnimationResourceCodec(), new MarkDiagnosticCodec(),
            new RangeResourceCodec(), new GenericE5ResourceCodec()];
    }

    public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project)
    {
        var resources = new List<ImageResourceDescriptor>();
        AddComposite(resources, project, "Pmapobj.e5", "R 形象", ImageCategory.CharacterR, ResourceKind.RComposite);
        AddS(resources, project);
        AddFace(resources, project);

        if (project.TryGetResource("Item.e5") != null)
            AddE5(resources, project, "Item.e5", "宝物图标", ImageCategory.ItemIcon, "小图/大图配对");
        else
            AddDll(resources, project, "Itemicon.dll", "宝物图标", ImageCategory.ItemIcon);
        if (project.TryGetResource("Mtem.e5") != null)
            AddE5(resources, project, "Mtem.e5", "策略图标", ImageCategory.StrategyIcon, "小图/大图配对");
        else
            AddDll(resources, project, "Mgcicon.dll", "策略图标", ImageCategory.StrategyIcon);

        foreach (var item in new[]
                 {
                     ("Logo.e5", "背景/插图", ImageCategory.Scene), ("Mmap.e5", "场景背景", ImageCategory.Scene),
                     ("Tr.e5", "过场图片", ImageCategory.Scene), ("Gate.e5", "场景门图", ImageCategory.Scene),
                     ("Hitarea.e5", "攻击范围", ImageCategory.Range),
                     ("Effarea.e5", "效果范围", ImageCategory.Range), ("U_select.e5", "选择光标", ImageCategory.Interface),
                     ("Fb.e5", "半身像", ImageCategory.HalfBody), ("Meff.e5", "策略小动画", ImageCategory.StrategyAnimation)
                 })
            AddE5(resources, project, item.Item1, item.Item2, item.Item3, "标准 E5 条目");
        AddSpecialE5(resources, project, "Pmap.e5", "战场地形", ImageCategory.Scene, ResourceKind.PmapSpecial,
            "16 字节头 + 8bpp 平面");
        AddSpecialE5(resources, project, "Weather.e5", "天气图片", ImageCategory.Dynamic, ResourceKind.WeatherSpecial,
            "216×50，四帧纵向动画");
        AddSpecialE5(resources, project, "DT.e5", "DT 图片", ImageCategory.HalfBody, ResourceKind.DtAnimation,
            "记录表 + 嵌入 BMP");
        AddMapPairs(resources, project);

        foreach (var mcall in project.ResourcePaths.Where(pair => pair.Key.StartsWith("Mcall:", StringComparison.OrdinalIgnoreCase)))
        {
            var recognized = TryProbeStandaloneAnimation(project, mcall.Value, out var status);
            resources.Add(new ImageResourceDescriptor(mcall.Key, Path.GetFileNameWithoutExtension(mcall.Value),
                ImageCategory.StrategyAnimation, ResourceKind.McallAnimation, [mcall.Value], 1, recognized,
                recognized && !project.IsReadOnly, !recognized, status, "策略大动画整包"));
        }

        AddMarkDiagnostic(resources, project);

        AddDll(resources, project, "Cmdicon.dll", "命令图标", ImageCategory.Interface);
        DiscoverE5(resources, project);
        var result = resources.ToArray();
        lock (_catalogSync)
            _catalogSnapshots[Path.GetFullPath(project.RootPath)] = result.ToDictionary(item => item.Key,
                StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
    {
        var codec = _codecs.FirstOrDefault(item => item.CanHandle(resource));
        return codec?.ReadAssets(project, resource) ?? Array.Empty<LogicalImageAsset>();
    }

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => FindCodec(project, asset)?.DecodeFrames(project, asset) ?? Array.Empty<DecodedFrame>();

    public DecodedFrame? DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return FindCodec(project, asset)?.DecodePreviewFrame(project, asset, descriptor, cancellationToken);
    }

    private IResourceCodec? FindCodec(ProjectProfile project, LogicalImageAsset asset)
    {
        ImageResourceDescriptor? descriptor;
        lock (_catalogSync)
            descriptor = _catalogSnapshots.TryGetValue(Path.GetFullPath(project.RootPath), out var snapshot) &&
                         snapshot.TryGetValue(asset.ResourceKey, out var cached) ? cached : null;
        descriptor ??= BuildCatalog(project).FirstOrDefault(item =>
            item.Key.Equals(asset.ResourceKey, StringComparison.OrdinalIgnoreCase));
        return descriptor == null ? null : _codecs.FirstOrDefault(item => item.CanHandle(descriptor));
    }

    private static void AddComposite(List<ImageResourceDescriptor> list, ProjectProfile project, string key, string name,
        ImageCategory category, ResourceKind kind)
    {
        var path = project.TryGetResource(key);
        E5Archive? archive = null;
        var error = "资源不存在。";
        if (path == null || !E5Archive.TryOpen(path, out archive, out error))
        {
            if (path != null) list.Add(new(key, name, category, kind, [path], 0, false, false, false, error, "形象帧"));
            return;
        }
        list.Add(new(key, name, category, kind, [path], archive!.Entries.Count, true, !project.IsReadOnly, false,
            $"{archive.Entries.Count} 个物理条目", "形象帧"));
    }

    private static void AddS(List<ImageResourceDescriptor> list, ProjectProfile project)
    {
        var keys = new[] { "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" };
        var paths = keys.Select(project.TryGetResource).Where(path => path != null).Cast<string>().ToArray();
        if (paths.Length != 3) return;
        var archives = paths.Select(E5Archive.Open).ToArray();
        var count = archives.Min(archive => archive.Entries.Count);
        var counts = string.Join("；", keys.Select((key, index) => $"{key}：{archives[index].Entries.Count} 项"));
        var status = archives.Select(archive => archive.Entries.Count).Distinct().Count() == 1
            ? $"三个档案同步；物理槽 {count}"
            : $"档案数量不一致，仅展示共同物理槽 1～{count}；{counts}";
        list.Add(new("SComposite", "S 形象", ImageCategory.CharacterS, ResourceKind.SComposite, paths, count,
            true, !project.IsReadOnly, false, status, "mov/atk/spc"));
    }

    private static void AddFace(List<ImageResourceDescriptor> list, ProjectProfile project)
    {
        var path = project.TryGetResource("Face.e5");
        if (path == null || !E5Archive.TryOpen(path, out var archive, out _)) return;
        var paths = new List<string> { path };
        var tou = project.TryGetResource("Tou.dll");
        if (tou != null) paths.Add(tou);
        list.Add(new("Face", "人物头像", ImageCategory.Portrait, ResourceKind.FaceComposite, paths,
            archive!.Entries.Count, true, !project.IsReadOnly, false,
            tou == null ? "小头像降级模式" : "Face/Tou 双文件模式", "Data 头像号"));
    }

    private static void AddE5(List<ImageResourceDescriptor> list, ProjectProfile project, string key, string name,
        ImageCategory category, string usage)
    {
        var path = project.TryGetResource(key);
        E5Archive? archive = null;
        var error = "资源不存在。";
        if (path == null)
        {
            return;
        }
        var opened = key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase)
            ? E5Archive.TryOpenMeff(path, out archive, out error)
            : E5Archive.TryOpen(path, out archive, out error);
        if (!opened)
        {
            list.Add(new(key, name, category, ResourceKind.E5Indexed, [path], 0, false, false, false, error, usage));
            return;
        }
        list.Add(new(key, name, category, ResourceKind.E5Indexed, [path], archive!.Entries.Count, true,
            !project.IsReadOnly, false, $"{archive.Entries.Count} 个条目", usage));
    }

    private static void AddDll(List<ImageResourceDescriptor> list, ProjectProfile project, string key, string name, ImageCategory category)
    {
        var path = project.TryGetResource(key);
        if (path == null) return;
        list.Add(new(key, name, category, ResourceKind.DllBitmap, [path], 0, true, !project.IsReadOnly, false,
            "DLL RT_BITMAP", "小图/大图资源"));
    }

    private static void AddMapPairs(List<ImageResourceDescriptor> list, ProjectProfile project)
    {
        var directory = project.TryGetResource("MapDirectory");
        var hexzmap = project.TryGetResource("Hexzmap.e5");
        if (directory == null || hexzmap == null) return;
        var maps = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^M\d{3}\.(?:jpg|jpeg)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (maps.Length == 0) return;
        list.Add(new ImageResourceDescriptor("MapPairs", "战场地图", ImageCategory.Scene, ResourceKind.MapPair,
            maps, maps.Length, true, !project.IsReadOnly, false, $"{maps.Length} 个 JPG + Hexzmap 地形槽", "成对地图槽"));
    }

    private static void AddExperimentalE5(List<ImageResourceDescriptor> list, ProjectProfile project, string key, string name)
    {
        var path = project.TryGetResource(key);
        E5Archive? archive = null;
        var error = "资源不存在。";
        if (path == null || !E5Archive.TryOpen(path, out archive, out error))
        {
            if (path != null) list.Add(new ImageResourceDescriptor(key, name, ImageCategory.Experimental, ResourceKind.E5Indexed,
                [path], 0, false, false, true, error, "实验格式"));
            return;
        }
        list.Add(new ImageResourceDescriptor(key, name, ImageCategory.Experimental, ResourceKind.E5Indexed,
            [path], archive!.Entries.Count, true, false, true, "格式探针通过；实验写入关闭", "实验格式"));
    }

    private static void AddSpecialE5(List<ImageResourceDescriptor> list, ProjectProfile project, string key, string name,
        ImageCategory category, ResourceKind kind, string usage)
    {
        var path = project.TryGetResource(key);
        if (path == null) return;
        if (!E5Archive.TryOpen(path, out var archive, out var error))
        {
            list.Add(new ImageResourceDescriptor(key, name, category, kind, [path], 0, false, false, false, error, usage));
            return;
        }
        var weatherVerified = false;
        var pmapVerified = false;
        try
        {
            weatherVerified = kind == ResourceKind.WeatherSpecial && archive!.Entries.Count > 0 && archive.Entries.All(entry =>
                entry.DecodedLength == WeatherResourceCodec.FrameWidth * WeatherResourceCodec.FrameHeight * WeatherResourceCodec.FrameCount &&
                archive.ReadDecoded(entry.ImageNumber).Length == entry.DecodedLength);
            pmapVerified = kind == ResourceKind.PmapSpecial && archive!.Entries.Count > 0 && archive.Entries.All(entry =>
                PmapResourceCodec.IsVerifiedPayload(archive.ReadDecoded(entry.ImageNumber)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            // A structurally valid index may still contain a malformed compressed payload.
            // Preserve its diagnostic catalog entry without exposing a write operation.
            error = ex.Message;
        }
        list.Add(new ImageResourceDescriptor(key, name, category, kind, [path], archive!.Entries.Count, true,
            (weatherVerified || pmapVerified) && !project.IsReadOnly, false,
            pmapVerified ? $"{archive.Entries.Count} 个已验证16字节头与RAW8平面；按原画布导入并保留头部" :
            weatherVerified ? $"{archive.Entries.Count} 个四帧天气条带；允许按固定216×200画布导入"
                : $"{archive.Entries.Count} 个专用格式条目；结构写入待逐字节往返验证" + (string.IsNullOrWhiteSpace(error) ? "" : "；" + error), usage));
    }

    private static void AddMarkDiagnostic(List<ImageResourceDescriptor> list, ProjectProfile project)
    {
        var path = project.TryGetResource("Mark.e5");
        if (path == null) return;
        list.Add(new ImageResourceDescriptor("Mark.e5", "Mark 专用格式", ImageCategory.Experimental,
            ResourceKind.MarkRaw, [path], 0, false, false, true,
            "不是 0x110 标准 E5；只读诊断，未通过逐字节往返前不开放猜测写入", "Mark.DEC 专用布局"));
    }

    private static bool TryProbeStandaloneAnimation(ProjectProfile project, string path, out string status)
    {
        try
        {
            var diagnostics = StrategyAnimationPackageCodec.ProbeFile(path);
            status = "已识别整文件动画；允许文件级新增/删除、整包替换和帧平面写回" +
                     $"；{diagnostics}";
            return true;
        }
        catch (Exception ex)
        {
            status = "未知 Mcall 变体，只读诊断：" + ex.Message;
            return false;
        }
    }

    private static void DiscoverE5(List<ImageResourceDescriptor> list, ProjectProfile project)
    {
        var claimed = list.SelectMany(item => item.Paths).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A project can contain the same archive in the root and E5 folders.
        // Keep one deterministic descriptor per file name; the integrated
        // editor reports the ambiguity and requires an explicit path choice.
        var claimedNames = claimed.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { project.RootPath, Path.Combine(project.RootPath, "E5") })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.e5", SearchOption.TopDirectoryOnly))
            {
                if (claimed.Contains(Path.GetFullPath(path)) || claimedNames.Contains(Path.GetFileName(path)) ||
                    ExcludedE5.Contains(Path.GetFileName(path))) continue;
                if (!E5Archive.TryOpen(path, out var archive, out _)) continue;
                list.Add(new("Discovered:" + Path.GetFileName(path), "自动发现 " + Path.GetFileNameWithoutExtension(path),
                    ImageCategory.Discovered, ResourceKind.E5Indexed, [Path.GetFullPath(path)], archive!.Entries.Count,
                    true, !project.IsReadOnly, false, $"{archive.Entries.Count} 个条目", "自动发现 E5"));
                claimedNames.Add(Path.GetFileName(path));
            }
        }
    }
}
