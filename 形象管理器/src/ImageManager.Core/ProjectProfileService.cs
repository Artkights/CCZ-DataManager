namespace ImageManager.Core;

public sealed class ProjectProfileService : IProjectProfileService
{
    private static readonly string[] KnownResources =
    [
        "Ekd5.exe", "Data.e5", "Pmapobj.e5", "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5",
        "Face.e5", "Tou.dll", "Itemicon.dll", "Mgcicon.dll", "Cmdicon.dll", "Item.e5", "Mtem.e5",
        "Logo.e5", "Mmap.e5", "Tr.e5", "Gate.e5", "Pmap.e5", "Hitarea.e5", "Effarea.e5",
        "U_select.e5", "Weather.e5", "Meff.e5", "Spalet.e5", "Pmpalet.e5", "DT.e5", "Fb.e5",
        "Mark.e5", "Hexzmap.e5"
    ];

    public ProjectProfile Detect(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("项目路径不能为空。", nameof(rootPath));
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("找不到项目目录：" + root);

        var diagnostics = new List<string>();
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in KnownResources)
        {
            var path = FindFile(root, name);
            if (path != null) resources[name] = path;
        }
        var mapDirectory = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals("Map", StringComparison.OrdinalIgnoreCase));
        if (mapDirectory != null) resources["MapDirectory"] = Path.GetFullPath(mapDirectory);

        var mcallFiles = EnumerateFiles(root, "Mcall*.e5").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in mcallFiles)
        {
            var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
            var identity = match.Success ? int.Parse(match.Value).ToString("00") : Path.GetFileNameWithoutExtension(path);
            resources[$"Mcall:{identity}"] = path;
        }

        var has66Set = resources.ContainsKey("Item.e5") &&
                       (resources.ContainsKey("Mtem.e5") || resources.ContainsKey("DT.e5") || resources.ContainsKey("Fb.e5"));
        var has65Set = resources.ContainsKey("Itemicon.dll") && resources.ContainsKey("Mgcicon.dll");
        // Engine identity and image container layout are independent. Some 6.6
        // distributions still use Itemicon/Mgcicon DLLs and carry an unrelated
        // RAW Item.e5. The PE version resource is the engine evidence; the
        // archive probe above still decides which icon codec is safe to use.
        var executableVersion = ReadExecutableVersion(resources, diagnostics);
        var version = executableVersion != ProjectVersion.Unknown ? executableVersion :
            has66Set ? ProjectVersion.Ccz66 : has65Set ? ProjectVersion.Ccz65 : ProjectVersion.Unknown;

        foreach (var required in new[] { "Ekd5.exe", "Data.e5", "Pmapobj.e5", "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" })
            if (!resources.ContainsKey(required)) diagnostics.Add("缺少核心文件：" + required);

        if (version == ProjectVersion.Unknown)
            diagnostics.Add("无法根据 Item/Mtem 或 Itemicon/Mgcicon 资源组合可靠识别版本，已只读打开。");
        else if (executableVersion == ProjectVersion.Unknown)
            diagnostics.Add($"已根据本地资源特征识别为 {(version == ProjectVersion.Ccz66 ? "6.6 修正版" : "6.5/6.5pl")}。");

        if (version == ProjectVersion.Ccz66 && !has66Set && has65Set)
            diagnostics.Add("6.6 引擎使用 DLL 图标资源；未验证几何的 RAW Item/Mtem 不按小图/大图配对读取。");

        if (resources.ContainsKey("Face.e5") && !resources.ContainsKey("Tou.dll"))
            diagnostics.Add("未找到 Tou.dll，人物头像使用小头像降级模式。");
        if (version != ProjectVersion.Unknown && resources.TryGetValue("Ekd5.exe", out var executablePath))
        {
            var layout = CharacterAssignmentLayoutProfile.For(version);
            var length = new FileInfo(executablePath).Length;
            if (length < layout.RequiredExecutableLength)
                diagnostics.Add($"人物指定表越界：Ekd5.exe 长度 {length}，至少需要 {layout.RequiredExecutableLength}；指定页将拒绝读写。");
            else
                diagnostics.Add($"人物指定表已验证：R@0x{layout.ROffset:X}、S@0x{layout.SOffset:X}、{layout.RowCount} 行（{layout.Evidence}）。");
        }

        return new ProjectProfile(
            root,
            new DirectoryInfo(root).Name,
            version,
            version == ProjectVersion.Unknown,
            resources,
            diagnostics);
    }

    private static ProjectVersion ReadExecutableVersion(IReadOnlyDictionary<string, string> resources,
        List<string> diagnostics)
    {
        if (!resources.TryGetValue("Ekd5.exe", out var path)) return ProjectVersion.Unknown;
        try
        {
            // The localized FileVersion text can be stale (for example
            // 1,0,0,1). Read VS_FIXEDFILEINFO's numeric private part instead.
            var privatePart = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FilePrivatePart;
            if (privatePart is not (5 or 6)) return ProjectVersion.Unknown;
            diagnostics.Add($"Ekd5.exe PE 固定版本资源 FilePrivatePart={privatePart}，识别为 6.{privatePart}；资源容器独立探测。");
            return privatePart == 6 ? ProjectVersion.Ccz66 : ProjectVersion.Ccz65;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            diagnostics.Add("无法读取 Ekd5.exe 固定版本资源，改用资源特征：" + ex.Message);
            return ProjectVersion.Unknown;
        }
    }

    private static string? FindFile(string root, string fileName)
    {
        var iconArchive = fileName.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) || fileName.Equals("Mtem.e5", StringComparison.OrdinalIgnoreCase);
        foreach (var directory in iconArchive ? new[] { Path.Combine(root, "E5"), root } : new[] { root, Path.Combine(root, "E5") })
        {
            if (!Directory.Exists(directory)) continue;
            var result = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (result != null && IsUsableResourcePath(result)) return Path.GetFullPath(result);
        }
        return null;
    }

    public static bool IsUsableResourcePath(string path)
    {
        if (!File.Exists(path)) return false;
        var name = Path.GetFileName(path);
        if (!name.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) && !name.Equals("Mtem.e5", StringComparison.OrdinalIgnoreCase)) return true;
        // Item.e5 at the project root can be a small extension DATA table.
        // It must never shadow the image archive stored in E5/Item.e5.
        try
        {
            return E5Archive.TryOpen(path, out var archive, out _) && archive is { Entries.Count: > 0 } &&
                ImageRendering.IsStandardImage(archive.ReadDecoded(1));
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        foreach (var directory in new[] { Path.Combine(root, "E5"), root })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)) yield return Path.GetFullPath(file);
        }
    }
}
