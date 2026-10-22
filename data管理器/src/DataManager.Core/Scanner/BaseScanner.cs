using DataManager.Core.Binary;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Core.Scanner;

public sealed class BaseScanner
{
    private readonly ProfileRegistry _registry;
    private readonly ProfileDetector _detector;

    public BaseScanner(ProfileRegistry? registry = null)
    {
        _registry = registry ?? new ProfileRegistry();
        _detector = new ProfileDetector(_registry);
    }

    public ScanReport Scan(string rootPath, IReadOnlyList<TableDefinition> definitions)
    {
        var match = _detector.Detect(rootPath);
        var resolved = _registry.Resolve(match.ProfileId, match.VariantId);
        return Scan(rootPath, resolved, match);
    }

    public ScanReport Scan(string rootPath, ResolvedProfile resolved, ProfileMatchResult match)
    {
        var reports = new List<ScanFileReport>();
        var diagnostics = new List<string>();
        diagnostics.AddRange(match.Evidence);
        diagnostics.AddRange(match.Conflicts.Select(conflict => $"证据冲突：{conflict}"));
        var expectedLengths = resolved.Variant.Fingerprints
            .SelectMany(fingerprint => fingerprint.FileLengths)
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Value).Distinct().OrderBy(value => value).ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var file in ProfileDetector.CoreFiles)
        {
            var path = ProjectFileLocator.Resolve(rootPath, file);
            if (!File.Exists(path))
            {
                reports.Add(new ScanFileReport(file, false, 0, 0, false, string.Empty, "文件不存在"));
                diagnostics.Add($"缺少核心文件：{file}");
                continue;
            }
            var length = new FileInfo(path).Length;
            var known = expectedLengths.GetValueOrDefault(file) ?? [];
            var lengthMatches = known.Length == 0 || known.Contains(length);
            var expected = known.Length == 0 ? 0 : known[0];
            var hash = HashService.Sha256(path);
            var message = known.Length == 0
                ? $"长度 {length:N0} 字节；当前变体没有固定长度约束"
                : lengthMatches
                    ? $"长度 {length:N0} 字节，符合当前变体已知样本"
                    : $"长度 {length:N0} 字节；当前变体已知长度为 {string.Join("/", known.Select(value => value.ToString("N0")))}";
            reports.Add(new ScanFileReport(Path.GetRelativePath(rootPath, path), true, length, expected, lengthMatches, hash, message));
        }

        var exePath = ProjectFileLocator.Resolve(rootPath, "Ekd5.exe");
        if (File.Exists(exePath))
        {
            var exeBytes = File.ReadAllBytes(exePath);
            diagnostics.Add(TryDescribePe(exeBytes, out var description)
                ? $"Ekd5.exe PE：{description}"
                : "Ekd5.exe 未通过 MZ/PE 文件头检查。");
        }

        var reader = new BinaryTableReader();
        var usable = 0;
        foreach (var definition in resolved.Tables)
        {
            var validation = reader.Validate(rootPath, definition);
            if (validation.IsUsable) usable++;
            else diagnostics.Add($"{definition.DisplayName}：{validation.Message}");
        }
        var allTables = usable == resolved.Tables.Count;
        var anyMissing = reports.Any(report => !report.Exists);
        var status = anyMissing || match.MatchKind == ProfileMatchKind.MissingFiles
            ? ScanStatus.MissingFiles
            : match.MatchKind == ProfileMatchKind.Exact && allTables
                ? ScanStatus.Matched
                : usable > resolved.Tables.Count / 2
                    ? ScanStatus.Partial
                    : ScanStatus.Unmatched;
        var fallbackText = match.MatchKind is ProfileMatchKind.Forced or ProfileMatchKind.Fallback
            ? "未匹配但按所选 Profile/布局读写"
            : match.MatchText;
        return new ScanReport
        {
            Status = status,
            Title = $"{fallbackText}：{resolved.Profile.DisplayName}",
            Summary = $"布局 {resolved.Variant.DisplayName}；{usable}/{resolved.Tables.Count} 张表可读取。扫描状态不限制写入。",
            Files = reports,
            Diagnostics = diagnostics,
            ProfileMatch = match,
            ProfileDisplayName = resolved.Profile.DisplayName,
            VariantDisplayName = resolved.Variant.DisplayName,
            LayoutRevision = resolved.LayoutRevision
        };
    }

    private static bool TryDescribePe(byte[] bytes, out string description)
    {
        description = string.Empty;
        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') return false;
        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        if (peOffset < 0 || peOffset > bytes.Length - 24 || bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E') return false;
        var machine = BitConverter.ToUInt16(bytes, peOffset + 4);
        var sectionCount = BitConverter.ToUInt16(bytes, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(bytes, peOffset + 20);
        if (peOffset + 24 + optionalHeaderSize + sectionCount * 40 > bytes.Length) return false;
        description = $"机器=0x{machine:X4}，节区={sectionCount}，可选头={optionalHeaderSize} 字节";
        return true;
    }
}
