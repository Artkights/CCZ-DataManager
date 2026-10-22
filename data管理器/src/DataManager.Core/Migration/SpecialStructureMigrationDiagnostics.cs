using DataManager.Core.Domain;
using DataManager.Core.Editing;
using System.Globalization;

namespace DataManager.Core.Migration;

/// <summary>
/// Maps native names and groups through their logical IDs and target-specific services.
/// Effect migration deliberately ignores every native name pool.  Names and concrete
/// wording belong to the project vocabulary; only the logical effect ID, effect value,
/// and assignment slots are migration data.  Name-pool layout/capacity/read-only
/// differences therefore never become migration changes or blockers.
/// </summary>
internal static class SpecialStructureMigrationDiagnostics
{
    public static void Add(ProjectWorkspace source, ProjectWorkspace target, List<MigrationChange> changes)
    {
        if (source.Definitions.Concat(target.Definitions).Any(definition =>
                definition.Key == "effect-names" || definition.Key.StartsWith("item-effect-names", StringComparison.Ordinal)))
            changes.Add(new MigrationChange("effect-name-policy", "特效名称迁移策略", "名称池", -1, null, null,
                MigrationChangeKind.Ignored,
                "特效名称、装备特效名称及名称池按迁移策略跳过，保留目标原值；特效号、特效值和分配关系独立迁移并复读，名称不同不会阻塞数值迁移。"));
        try
        {
            var groups = new AccessoryJobGroupService();
            var sourceGroups = groups.Read(source);
            var targetGroups = groups.Read(target);
            var errors = sourceGroups.Diagnostics.Where(message => message.StartsWith("错误：", StringComparison.Ordinal)).ToArray();
            var sourceDisplay = Display(sourceGroups);
            var targetDisplay = Display(targetGroups);
            if (sourceDisplay != targetDisplay || errors.Length > 0 || targetGroups.Diagnostics.Any(message => message.StartsWith("错误：", StringComparison.Ordinal)))
            {
                var preview = groups.Preview(target.RootPath, sourceGroups.Groups.Select(group => group.JobSeriesIds).ToArray());
                var writable = errors.Length == 0 && preview.CanWrite;
                var diagnostics = errors.Concat(preview.Diagnostics.Where(message => message.StartsWith("错误：", StringComparison.Ordinal))).ToArray();
                changes.Add(new MigrationChange("job-series-accessory-groups", "兵种系·辅助装备分组", "专用分组", -1,
                    sourceDisplay, targetDisplay, writable ? MigrationChangeKind.NativeStructureChanged : MigrationChangeKind.Incompatible,
                    writable
                        ? "按逻辑兵种系 ID 分组迁移；已通过目标 PE 地址、FF 分隔和 0x90 安全区验证，使用目标专用事务写入并复读。"
                        : "辅助分组无法通过源/目标布局和容量验证；保留目标原值。" + string.Join("；", diagnostics)));
            }
        }
        catch (Exception ex)
        {
            changes.Add(new MigrationChange("job-series-accessory-groups", "兵种系·辅助装备分组", "读取/布局证据", -1, null, null,
                MigrationChangeKind.Incompatible, $"专用辅助分组无法完成差异比较：{ex.Message}。本项不自动迁移。"));
        }
    }

    /// <summary>Applies only logical values, after ordinary target drafts have been saved.</summary>
    public static int ApplyMany(ProjectWorkspace target, IReadOnlyList<(MigrationChange Change, MigrationChoice Choice)> changes)
    {
        // Name-pool entries are intentionally ignored.  They are descriptive
        // text and can vary between projects; only numeric assignment/value
        // changes and the independently validated accessory groups are applied.
        var applied = 0;
        applied += AtomicItemMigrationMapper.ApplyBatch(target, changes.Where(item => item.Change.TableKey == AtomicItemMigrationMapper.TableKey).ToArray());
        foreach (var (change, choice) in changes.Where(item => item.Change.TableKey != "item-effect-name-pool" && item.Change.TableKey != "effect-names" && item.Change.TableKey != AtomicItemMigrationMapper.TableKey))
            if (Apply(target, change, choice)) applied++;
        return applied;
    }

    public static bool Apply(ProjectWorkspace target, MigrationChange change, MigrationChoice choice)
    {
        if (change.Kind != MigrationChangeKind.NativeStructureChanged)
            throw new InvalidOperationException("不是可写专用结构迁移项。");
        var text = Convert.ToString(change.SourceValue, CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("专用结构迁移缺少逻辑值。");
        switch (change.TableKey)
        {
            case AtomicItemMigrationMapper.TableKey:
                return AtomicItemMigrationMapper.Apply(target, change, choice);
            case "job-series-accessory-groups" when change.FieldName == "专用分组":
            {
                var groups = new AccessoryJobGroupService();
                if (choice == MigrationChoice.Merge && groups.Read(target).Groups.Count > 0) return false;
                IReadOnlyList<IReadOnlyList<int>> logicalGroups = text.Split('/', StringSplitOptions.TrimEntries)
                    .Select(group => (IReadOnlyList<int>)group.Split(',', StringSplitOptions.TrimEntries)
                        .Select(value => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray()).ToArray();
                groups.Write(target, logicalGroups);
                return true;
            }
            default: throw new InvalidOperationException($"没有专用逻辑迁移器：{change.TableKey}/{change.FieldName}。");
        }
    }

    private static string Display(AccessoryJobGroupReadResult result)
        => string.Join(" / ", result.Groups.Select(group => string.Join(",", group.JobSeriesIds)));
}
