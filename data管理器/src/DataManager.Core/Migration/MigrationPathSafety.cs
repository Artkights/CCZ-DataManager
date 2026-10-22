namespace DataManager.Core.Migration;

internal static class MigrationPathSafety
{
    public static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"迁移项必须使用普通相对路径：{relative}");
        var absoluteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(absoluteRoot, relative));
        if (!full.StartsWith(absoluteRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"迁移项超出项目目录：{relative}");
        RequireNoLinks(full);
        return full;
    }

    public static void RequireNoLinks(string path)
    {
        // Check the final file, project root and its ancestors as well as child
        // directories. Otherwise a root junction can alias source and target,
        // or a final file link can make fingerprints refer outside the project.
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"迁移路径包含文件/目录链接，不能保证源目标独立：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
