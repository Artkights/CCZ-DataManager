using System.Text.RegularExpressions;

namespace ImageManager.Core;

public sealed class BatchRenameService : IBatchRenameService
{
    private readonly HistoryService _history;

    public BatchRenameService(HistoryService history) => _history = history;

    public RenamePlan Preview(string rootPath, RenameRule rule)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var errors = new List<string>();
        var warnings = new List<string>();
        Regex? regex = null;
        if (rule.UseRegex)
        {
            try { regex = new Regex(rule.Search, RegexOptions.CultureInvariant); }
            catch (ArgumentException ex) { errors.Add("正则表达式无效：" + ex.Message); }
        }

        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Concat(rule.IncludeDirectories
                ? Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)
                : Array.Empty<string>()).Where(path => !_history.IsInsideBackup(path)).ToArray();
        var items = new List<RenameItem>();
        var sequence = 1;
        foreach (var source in paths)
        {
            var directory = Path.GetDirectoryName(source)!;
            var extension = File.Exists(source) ? Path.GetExtension(source) : string.Empty;
            var stem = File.Exists(source) ? Path.GetFileNameWithoutExtension(source) : Path.GetFileName(source);
            var changed = regex != null ? regex.Replace(stem, rule.Replacement) : stem.Replace(rule.Search, rule.Replacement, StringComparison.Ordinal);
            changed = rule.Prefix + changed + rule.Suffix;
            if (!string.IsNullOrWhiteSpace(rule.Template))
            {
                changed = ExpandTemplate(rule.Template, source, sequence);
            }
            sequence++;
            if (changed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || string.IsNullOrWhiteSpace(changed))
            {
                errors.Add("非法目标文件名：" + changed);
                continue;
            }
            var target = Path.Combine(directory, changed + extension);
            if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.Ordinal))
                items.Add(new RenameItem(source, target, Directory.Exists(source)));
        }

        foreach (var duplicate in items.GroupBy(item => RenameTransaction.ResolveTarget(item.TargetPath, items), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            errors.Add("多个项目将改为同一名称：" + duplicate.Key);
        foreach (var item in items)
        {
            var target = RenameTransaction.ResolveTarget(item.TargetPath, items);
            if ((File.Exists(target) || Directory.Exists(target)) && !items.Any(source =>
                    source.SourcePath.Equals(target, StringComparison.OrdinalIgnoreCase) || source.IsDirectory &&
                    target.StartsWith(source.SourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                errors.Add("目标已经存在：" + target);
        }
        if (items.Count == 0 && errors.Count == 0) warnings.Add("没有名称发生变化。");
        items = items.Select(item => item with { ResolvedTargetPath = RenameTransaction.ResolveTarget(item.TargetPath, items) }).ToList();
        return new RenamePlan(root, items, errors, warnings);
    }

    public MutationResult Apply(ProjectProfile project, RenamePlan plan)
    {
        if (!plan.CanApply) return new MutationResult(Guid.NewGuid().ToString("N"), false, "批量改名预检未通过", null,
            Array.Empty<string>(), plan.Errors);
        if (project.IsReadOnly) return new MutationResult(Guid.NewGuid().ToString("N"), false, "当前项目只读，不能改名", null, [], []);
        var operationId = Guid.NewGuid().ToString("N");
        var targets = plan.Items.Select(item => new RenameHistoryTarget
        {
            SourcePath = item.SourcePath,
            TargetPath = RenameTransaction.ResolveTarget(item.TargetPath, plan.Items),
            IsDirectory = item.IsDirectory,
            BeforeFingerprint = item.SourceFingerprint
        }).ToArray();
        var historyPlan = new MutationPlan(operationId, MutationKind.Rename, $"批量改名 {plan.Items.Count} 项",
            [], plan.Warnings, false, new Dictionary<string, string> { ["RenameRoot"] = plan.RootPath });
        return RenameTransaction.Execute(plan.RootPath, operationId, historyPlan.Summary, targets, forward: true,
            persist: () => _history.Record(project, historyPlan, [], targets));
    }

    private static string ExpandTemplate(string template, string path, int sequence)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(name, @"(?<type>[RS])?(?<id>\d+)?(?:[_-]?(?<turn>turn\d+))?(?:[_-]?(?<action>mov|atk|spc))?(?:[_-]?(?<side>front|back))?", RegexOptions.IgnoreCase);
        return template
            .Replace("{type}", match.Groups["type"].Value, StringComparison.OrdinalIgnoreCase)
            .Replace("{id}", match.Groups["id"].Value, StringComparison.OrdinalIgnoreCase)
            .Replace("{turn}", match.Groups["turn"].Value, StringComparison.OrdinalIgnoreCase)
            .Replace("{action}", match.Groups["action"].Value, StringComparison.OrdinalIgnoreCase)
            .Replace("{side}", match.Groups["side"].Value, StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", sequence.ToString("D3"), StringComparison.OrdinalIgnoreCase);
    }

}
