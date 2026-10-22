using System.Diagnostics;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.Profiles;

public sealed class ProfileDetector
{
    public static readonly string[] CoreFiles = ["Data.e5", "Star.e5", "Ekd5.exe", "Imsg.e5"];
    private readonly ProfileRegistry _registry;

    public ProfileDetector(ProfileRegistry registry)
    {
        _registry = registry;
    }

    public ProfileMatchResult Detect(string rootPath)
    {
        var evidence = new List<string>();
        var conflicts = new List<string>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var name in CoreFiles)
        {
            var path = ProjectFileLocator.Resolve(rootPath, name);
            if (!File.Exists(path))
            {
                missing.Add(name);
                continue;
            }
            lengths[name] = new FileInfo(path).Length;
            hashes[name] = HashService.Sha256(path);
        }

        var candidates = AllCandidates().ToArray();
        var exact = candidates.FirstOrDefault(candidate =>
            candidate.Fingerprint.Sha256.Count == CoreFiles.Length &&
            CoreFiles.All(file => hashes.TryGetValue(file, out var hash) && candidate.Fingerprint.Sha256.TryGetValue(file, out var expected) && hash.Equals(expected, StringComparison.OrdinalIgnoreCase)));
        if (exact != default)
        {
            evidence.Add($"四个核心文件 SHA-256 完整匹配样本：{exact.Fingerprint.SampleId}");
            return Result(exact.Profile, exact.Variant, missing.Count > 0 ? ProfileMatchKind.MissingFiles : ProfileMatchKind.Exact, "完整核心文件 SHA-256", exact.Fingerprint.SampleId, evidence, conflicts, hashes);
        }

        Candidate? selected = null;
        var selectionMethod = string.Empty;
        if (hashes.TryGetValue("Ekd5.exe", out var exeHash))
        {
            var exeMatches = candidates.Where(candidate => candidate.Fingerprint.Sha256.TryGetValue("Ekd5.exe", out var expected) && exeHash.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exeMatches.Length > 0)
            {
                selected = exeMatches[0];
                selectionMethod = "Ekd5.exe SHA-256";
                evidence.Add($"Ekd5.exe SHA-256 匹配样本：{selected.Value.Fingerprint.SampleId}");
            }
        }

        int privatePart = 0;
        var exePath = ProjectFileLocator.Resolve(rootPath, "Ekd5.exe");
        if (File.Exists(exePath))
        {
            try
            {
                privatePart = FileVersionInfo.GetVersionInfo(exePath).FilePrivatePart;
                if (privatePart is 5 or 6) evidence.Add($"PE 版本资源 FilePrivatePart={privatePart}");
            }
            catch (Exception ex)
            {
                evidence.Add($"PE 版本资源读取失败：{ex.Message}");
            }
        }

        if (selected is { } hashCandidate && privatePart is 5 or 6 && !hashCandidate.Profile.Version.EndsWith(privatePart.ToString(), StringComparison.Ordinal))
        {
            conflicts.Add($"EXE 指纹指向 {hashCandidate.Profile.DisplayName}，PE 版本资源指向 6.{privatePart}。按 EXE 指纹优先。");
        }

        if (selected == null && privatePart is 5 or 6)
        {
            var profileId = privatePart == 6 ? "ccz-6.6" : "ccz-6.5";
            var profile = _registry.GetProfile(profileId);
            selected = new Candidate(profile, SelectVariantByLayoutEvidence(profile, exePath, lengths, evidence), new ProfileFingerprint { SampleId = "PE 版本资源" });
            selectionMethod = "PE 版本资源";
        }

        if (selected == null)
        {
            var lengthMatches = candidates.Where(candidate => candidate.Fingerprint.FileLengths.Count > 0 && candidate.Fingerprint.FileLengths.All(pair => lengths.TryGetValue(pair.Key, out var length) && length == pair.Value)).ToArray();
            if (lengthMatches.Length == 1)
            {
                selected = lengthMatches[0];
                selectionMethod = "核心文件长度组合";
                evidence.Add($"核心文件长度组合匹配样本：{selected.Value.Fingerprint.SampleId}");
            }
            else if (lengthMatches.Length > 1)
            {
                var distinct = lengthMatches.Select(match => $"{match.Profile.Id}/{match.Variant.Id}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (distinct.Length == 1)
                {
                    selected = lengthMatches[0];
                    selectionMethod = "核心文件长度组合";
                }
                else conflicts.Add($"文件长度同时匹配多个布局：{string.Join("、", distinct)}。");
            }
        }

        if (selected == null && File.Exists(exePath))
        {
            var anchor = DetectItemAnchor(exePath);
            if (anchor != null)
            {
                var profile = _registry.GetProfile("ccz-6.6");
                var variantId = anchor == "15" ? "66-standard-v1" : "66-legacy-data-v1";
                selected = new Candidate(profile, profile.Variants.Single(variant => variant.Id == variantId), new ProfileFingerprint { SampleId = $"离线物品索引锚点 {anchor} 字节" });
                selectionMethod = "离线机器码锚点";
                evidence.Add(anchor == "15" ? "离线机器码包含物品基址 0x49F820（15 字节名称布局）" : "离线机器码包含物品基址 0x4A1140（17 字节名称布局）");
            }
            else if (DetectEffectShape(exePath) == "16")
            {
                var profile = _registry.GetProfile("ccz-6.6");
                selected = new Candidate(profile, profile.Variants.Single(variant => variant.Id == "66-standard-v1"), new ProfileFingerprint { SampleId = "0xD9000 表形态" });
                selectionMethod = "离线表结构锚点";
                evidence.Add("Ekd5.exe+0xD9000 呈现已知 6.6 标准样本的 255 x 16 稀疏表形态。");
            }
        }

        if (selected == null)
        {
            var fallback = _registry.GetProfile("ccz-6.5");
            evidence.Add("没有足够证据确定版本；按规则回退到 6.5 默认布局。路径名称未参与版本裁决。");
            return Result(fallback, fallback.Variants.Single(variant => variant.Id == fallback.DefaultVariantId), missing.Count > 0 ? ProfileMatchKind.MissingFiles : ProfileMatchKind.Fallback, "默认回退", null, evidence, conflicts, hashes);
        }

        var matchKind = missing.Count > 0 ? ProfileMatchKind.MissingFiles : conflicts.Count > 0 ? ProfileMatchKind.Partial : ProfileMatchKind.Compatible;
        if (selected.Value.Profile.Id == "ccz-6.6" && selected.Value.Variant.Id == "66-standard-v1" && selected.Value.Fingerprint.SampleId == "PE 版本资源")
        {
            evidence.Add("已确认 6.6，但布局证据不足；按规则使用 66-standard-v1。");
        }
        return Result(selected.Value.Profile, selected.Value.Variant, matchKind, selectionMethod, selected.Value.Fingerprint.SampleId, evidence, conflicts, hashes);
    }

    public ProfileMatchResult Force(ProfileMatchResult detected, string profileId, string variantId)
    {
        var profile = _registry.GetProfile(profileId);
        var variant = profile.Variants.Single(item => item.Id.Equals(variantId, StringComparison.OrdinalIgnoreCase));
        return new ProfileMatchResult
        {
            ProfileId = profile.Id,
            VariantId = variant.Id,
            MatchKind = ProfileMatchKind.Forced,
            DetectionMethod = "用户手动覆盖（仅当前会话）",
            IsManualOverride = true,
            SampleId = detected.SampleId,
            Evidence = [.. detected.Evidence, $"手动选择 {profile.DisplayName} / {variant.DisplayName}；未匹配但按所选 Profile/布局读写。"],
            Conflicts = [.. detected.Conflicts],
            CoreFileFingerprints = new Dictionary<string, string>(detected.CoreFileFingerprints, StringComparer.OrdinalIgnoreCase)
        };
    }

    private ProfileVariant SelectVariantByLayoutEvidence(DataProfile profile, string exePath, IReadOnlyDictionary<string, long> lengths, List<string> evidence)
    {
        if (profile.Id != "ccz-6.6") return profile.Variants.Single(variant => variant.Id == profile.DefaultVariantId);
        var anchor = DetectItemAnchor(exePath);
        if (anchor != null)
        {
            evidence.Add(anchor == "15" ? "离线物品索引锚点选择 15 字节名称布局。" : "离线物品索引锚点选择 17 字节名称布局。");
            return profile.Variants.Single(variant => variant.Id == (anchor == "15" ? "66-standard-v1" : "66-legacy-data-v1"));
        }
        var lengthMatch = profile.Variants.SelectMany(variant => variant.Fingerprints.Select(fingerprint => (Variant: variant, Fingerprint: fingerprint)))
            .Where(item => item.Fingerprint.FileLengths.Count > 0 && item.Fingerprint.FileLengths.All(pair => lengths.TryGetValue(pair.Key, out var value) && value == pair.Value))
            .Select(item => item.Variant).DistinctBy(variant => variant.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (lengthMatch.Length == 1)
        {
            evidence.Add($"文件长度组合选择布局：{lengthMatch[0].DisplayName}");
            return lengthMatch[0];
        }
        var shape = DetectEffectShape(exePath);
        if (shape != null)
        {
            evidence.Add(shape == "16" ? "0xD9000 表形态选择 16 字节兵种特效布局。" : "0xD9000 表形态选择 8 字节兵种特效布局。");
            return profile.Variants.Single(variant => variant.Id == (shape == "16" ? "66-standard-v1" : "66-legacy-data-v1"));
        }
        evidence.Add("离线物品索引锚点和长度组合均不能唯一确定布局；默认使用 66-standard-v1。");
        return profile.Variants.Single(variant => variant.Id == profile.DefaultVariantId);
    }

    private static string? DetectItemAnchor(string exePath)
    {
        var bytes = File.ReadAllBytes(exePath);
        var has15 = Contains(bytes, BitConverter.GetBytes(0x49F820));
        var has17 = Contains(bytes, BitConverter.GetBytes(0x4A1140));
        return has15 == has17 ? null : has15 ? "15" : "17";
    }

    private static string? DetectEffectShape(string exePath)
    {
        const int offset = 0xD9000;
        const int rows = 255;
        var bytes = File.ReadAllBytes(exePath);
        if (bytes.Length < offset + rows * 16) return null;
        var nonEmpty8 = 0;
        var nonEmpty16 = 0;
        for (var row = 0; row < rows; row++)
        {
            if (bytes.AsSpan(offset + row * 8, 8).IndexOfAnyExcept((byte)0) >= 0) nonEmpty8++;
            if (bytes.AsSpan(offset + row * 16, 16).IndexOfAnyExcept((byte)0) >= 0) nonEmpty16++;
        }
        if (nonEmpty16 <= 32 && nonEmpty8 <= 64) return "16";
        if (nonEmpty16 >= 96 && nonEmpty8 >= 192) return "8";
        return null;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    private IEnumerable<Candidate> AllCandidates()
        => _registry.Profiles.SelectMany(profile => profile.Variants.SelectMany(variant => variant.Fingerprints.Select(fingerprint => new Candidate(profile, variant, fingerprint))));

    private static ProfileMatchResult Result(DataProfile profile, ProfileVariant variant, ProfileMatchKind kind, string method, string? sample, List<string> evidence, List<string> conflicts, Dictionary<string, string> hashes)
        => new()
        {
            ProfileId = profile.Id,
            VariantId = variant.Id,
            MatchKind = kind,
            DetectionMethod = method,
            SampleId = sample,
            Evidence = evidence,
            Conflicts = conflicts,
            CoreFileFingerprints = hashes
        };

    private readonly record struct Candidate(DataProfile Profile, ProfileVariant Variant, ProfileFingerprint Fingerprint);
}
