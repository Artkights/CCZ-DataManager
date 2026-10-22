using ImageManager.Core;

namespace ImageManager.App;

/// <summary>Discovers candidates without resolving ambiguous files or guessing an S numbering base.</summary>
internal static class ImageImportSourceDiscovery
{
    internal const string FileFilter = ImageImportSourceDecoder.FileDialogFilter;
    internal const string RsFileFilter = "R/S动作条 (*.bmp;*.png;*.jpg;*.jpeg)|*.bmp;*.png;*.jpg;*.jpeg|所有文件 (*.*)|*.*";

    internal static bool IsImage(string path) => new[] { ".bmp", ".png", ".jpg", ".jpeg", ".gif", ".tif", ".tiff", ".ico", ".webp" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static bool IsRsImage(string path) => new[] { ".bmp", ".png", ".jpg", ".jpeg" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static string[] Files(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory).Where(IsImage).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    internal static string[] Candidates(string directory, string role)
    {
        var aliases = role switch
        {
            "front" => new[] { "front", "x", "1" },
            "back" => new[] { "back", "y", "2" },
            _ => new[] { role }
        };
        return Files(directory).Where(IsRsImage)
            .Where(path => aliases.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal static string[] TargetDirectories(string root, LogicalImageAsset target, RsImportNaming naming, bool allowDirect)
    {
        var name = target.Kind == ResourceKind.SComposite
            ? SResourceExchangeNaming.ImportBaseName(target, naming) : target.DisplayName;
        var directories = new List<string>();
        if (target.Kind == ResourceKind.SComposite && naming == RsImportNaming.Physical)
        {
            var categorized = Path.Combine(root, SResourceExchangeNaming.Category(target), name);
            if (Directory.Exists(categorized)) directories.Add(categorized);
        }
        var nested = Path.Combine(root, name);
        if (Directory.Exists(nested)) directories.Add(nested);
        if (directories.Count == 0 && (allowDirect || Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals(name, StringComparison.OrdinalIgnoreCase)))
            directories.Add(root);
        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string[] AppendDirectories(string root, ResourceKind kind)
    {
        bool HasRoles(string directory) => (kind == ResourceKind.RComposite ? new[] { "front", "back" } : new[] { "mov", "atk", "spc" })
            .Any(role => Candidates(directory, role).Length > 0);
        if (HasRoles(root)) return [root];
        if (kind == ResourceKind.SComposite)
        {
            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("兵种", StringComparison.Ordinal))
                throw new InvalidDataException("兵种分类使用固定槽。请选择具体的一组素材复用，或返回兵种页替换原槽。");
            var generals = Path.Combine(root, "将领");
            if (Directory.Exists(generals)) root = generals;
            else if (Directory.Exists(Path.Combine(root, "兵种")))
                throw new InvalidDataException("所选目录只有兵种分类；不能批量追加到将领区。");
        }
        return Directory.EnumerateDirectories(root).Where(HasRoles)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
