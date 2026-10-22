using System.Security.Cryptography;

namespace ImageManager.Core;

/// <summary>The source state used to build a candidate, rather than the state first seen at commit time.</summary>
public sealed record MutationSourceSnapshot(bool Exists, long Length, string Sha256)
{
    public static MutationSourceSnapshot Capture(string path)
    {
        if (!File.Exists(path)) return new(false, 0, string.Empty);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(true, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    public void Verify(string path)
    {
        if (this != Capture(path))
            throw new IOException("资源在生成候选后已被外部修改，已拒绝覆盖，请重新读取后操作：" + path);
    }
}

/// <summary>Capture before any candidate reads, and verify after all builders have finished.</summary>
public sealed class MutationPlanningSnapshot
{
    private readonly IReadOnlyDictionary<string, MutationSourceSnapshot> _sources;

    private MutationPlanningSnapshot(IEnumerable<string> paths)
        => _sources = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, MutationSourceSnapshot.Capture, StringComparer.OrdinalIgnoreCase);

    public static MutationPlanningSnapshot Capture(IEnumerable<string> paths) => new(paths);

    public static MutationPlan Build(IEnumerable<string> paths, Func<MutationPlan> builder)
    {
        var snapshot = Capture(paths);
        return snapshot.Complete(builder());
    }

    public MutationPlan Complete(MutationPlan plan)
    {
        foreach (var pair in _sources) pair.Value.Verify(pair.Key);
        var sources = plan.SourceSnapshots?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, MutationSourceSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _sources)
        {
            if (sources.TryGetValue(pair.Key, out var previous) && previous != pair.Value)
                throw new IOException("生成候选期间资源已改变，请重新读取：" + pair.Key);
            sources[pair.Key] = pair.Value;
        }
        return plan with
        {
            Files = plan.Files.Select(file => _sources.TryGetValue(Path.GetFullPath(file.TargetPath), out var expected)
                ? file with { ExpectedSource = expected } : file).ToArray(),
            SourceSnapshots = sources
        };
    }
}
