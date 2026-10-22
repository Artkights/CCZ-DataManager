using System.Collections.Concurrent;
using System.Drawing;
using System.Security.Cryptography;

namespace ImageManager.Core;

public sealed record PaletteResolution(IReadOnlyList<Color> Colors, bool CanWrite, string Source, string Diagnostic);

public sealed class PaletteService
{
    private static readonly ConcurrentDictionary<string, PaletteResolution> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const string EmbeddedName = "ImageManager.Core.Palettes.tsb";

    public IReadOnlyList<Color> Load(ProjectProfile project) => Resolve(project).Colors;

    /// <summary>Includes palette content, including edits that preserve length and modification time.</summary>
    public string GetVersionKey(ProjectProfile project)
    {
        var paths = new[]
        {
            project.TryGetResource("Spalet.e5"), Path.Combine(project.RootPath, "Spalet.e5"),
            Path.Combine(project.RootPath, "E5", "Spalet.e5"), Path.Combine(project.RootPath, "tsb"),
            Path.Combine(project.RootPath, "Assets", "Palettes", "tsb"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Palettes", "tsb")
        };
        return string.Join('|', paths.Where(path => path != null).Cast<string>()
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(DependencyFingerprint)) + "|" + EmbeddedName;
    }

    private static string DependencyFingerprint(string path)
    {
        try { return File.Exists(path) ? Fingerprint(path) : path + "|missing"; }
        catch (IOException) { return path + "|unreadable"; }
        catch (UnauthorizedAccessException) { return path + "|unreadable"; }
    }

    public IReadOnlyList<Color> RequireWritePalette(ProjectProfile project)
    {
        var result = Resolve(project);
        if (!result.CanWrite) throw new InvalidDataException(result.Diagnostic);
        return result.Colors;
    }

    public PaletteResolution Resolve(ProjectProfile project)
    {
        var errors = new List<string>();
        var spaletPaths = new[] { project.TryGetResource("Spalet.e5"), Path.Combine(project.RootPath, "Spalet.e5"), Path.Combine(project.RootPath, "E5", "Spalet.e5") };
        foreach (var path in spaletPaths.Where(path => path != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var key = "Spalet|" + Fingerprint(path);
                if (Cache.TryGetValue(key, out var cached)) return cached;
                var archive = E5Archive.OpenFresh(path);
                foreach (var entry in archive.Entries.Where(entry => entry.DecodedLength >= 768))
                {
                    var bytes = archive.ReadDecoded(entry.ImageNumber);
                    var colors = Enumerable.Range(0, 256).Select(i => Color.FromArgb(255, bytes[i * 3 + 1], bytes[i * 3 + 2], bytes[i * 3])).ToArray();
                    return Cache[key] = new(colors, true, path, "项目 Spalet 调色板（B/R/G）。");
                }
                errors.Add(Path.GetFileName(path) + " 没有完整调色板");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
            { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
        }
        foreach (var path in new[] { Path.Combine(project.RootPath, "tsb"), Path.Combine(project.RootPath, "Assets", "Palettes", "tsb"), Path.Combine(AppContext.BaseDirectory, "Assets", "Palettes", "tsb") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var key = "tsb|" + Fingerprint(path);
                if (Cache.TryGetValue(key, out var cached)) return cached;
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 1024) { errors.Add(path + " 调色板不足 1024 字节"); continue; }
                return Cache[key] = new(ReadBgra(bytes), true, path, "tsb 调色板（B/G/R/保留）。");
            }
            catch (IOException ex) { errors.Add(ex.Message); }
        }
        using (var stream = typeof(PaletteService).Assembly.GetManifestResourceStream(EmbeddedName))
        {
            if (stream != null)
            {
                using var output = new MemoryStream(); stream.CopyTo(output);
                var bytes = output.ToArray();
                if (bytes.Length >= 1024)
                    return Cache.GetOrAdd(EmbeddedName, _ => new(ReadBgra(bytes), true, "内置 tsb", "未找到有效项目调色板，采用工具整合包内置 tsb。"));
            }
        }
        return new(Enumerable.Range(0, 256).Select(i => Color.FromArgb(255, i, i, i)).ToArray(), false,
            "只读灰度预览", "缺少有效 Spalet/tsb；只允许预览，禁止将灰度调色板用于保存。" + string.Join("；", errors));
    }

    private static IReadOnlyList<Color> ReadBgra(byte[] bytes)
        => Enumerable.Range(0, 256).Select(i => Color.FromArgb(255, bytes[i * 4 + 2], bytes[i * 4 + 1], bytes[i * 4])).ToArray();

    private static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Convert.ToHexString(SHA256.HashData(stream))}";
    }
}
