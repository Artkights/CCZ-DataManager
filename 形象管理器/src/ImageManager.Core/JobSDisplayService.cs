using System.Text;

namespace ImageManager.Core;

/// <summary>Detailed job names use job IDs 0..79, independently of physical Unit slots.</summary>
public static class JobSDisplayService
{
    // Same 80 × 9-byte detailed-jobs table used by the 6.5/6.6 Data profiles.
    private const int NameOffset = 0xD18D0;
    private const int NameSize = 9;
    private const int JobCount = 80;

    public static IReadOnlyDictionary<int, string> LoadNames(ProjectProfile project)
    {
        var path = project.TryGetResource("Ekd5.exe");
        if (path == null || !File.Exists(path)) return new Dictionary<int, string>();
        var bytes = File.ReadAllBytes(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        var names = new Dictionary<int, string>();
        for (var id = 0; id < JobCount && bytes.Length >= NameOffset + (id + 1) * NameSize; id++)
        {
            var field = bytes.AsSpan(NameOffset + id * NameSize, NameSize);
            var end = field.IndexOf((byte)0);
            var name = gbk.GetString(end < 0 ? field : field[..end]).Trim();
            if (name.Length > 0) names[id] = name;
        }
        return names;
    }

    public static List<LogicalImageAsset> ApplyNames(IEnumerable<LogicalImageAsset> assets, IReadOnlyDictionary<int, string> names)
        => assets.Select(asset =>
        {
            if (asset.Kind != ResourceKind.SComposite || asset.SMode != SImageMode.Job) return asset;
            var physical = SResourceExchangeNaming.PhysicalId(asset);
            if (physical is < 1 or > 240) throw new InvalidDataException("兵种S物理图号必须为1～240。");
            var jobId = (physical - 1) / 3;
            var factionIndex = (physical - 1) % 3;
            var faction = factionIndex switch { 0 => "我军", 1 => "友军", _ => "敌军" };
            var name = names.TryGetValue(jobId, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : "未命名";
            return asset with { DisplayName = $"{jobId}：{name} {faction}" };
        }).ToList();
}
