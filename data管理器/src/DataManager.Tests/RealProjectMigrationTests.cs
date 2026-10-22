using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Migration;
using DataManager.Core.Models;
using DataManager.Images;

namespace DataManager.Tests;

/// <summary>
/// An opt-in real-project audit. Every write is confined to a newly-created
/// temporary target; complete manifests prove that both supplied bases stay intact.
/// Kept separate from the synthetic regression suite because real assets are large.
/// </summary>
internal static class RealProjectMigrationTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Run(string sourceRoot, string targetRoot, string reportDirectory, string phase = "inventory")
    {
        // The UI accepts an extracted archive directory.  Resolve both
        // selections before inventory so a wrapper folder containing the real
        // Data.e5/Ekd5.exe is never mistaken for an empty project.
        sourceRoot = MigrationService.ResolveProjectRoot(Path.GetFullPath(sourceRoot));
        targetRoot = MigrationService.ResolveProjectRoot(Path.GetFullPath(targetRoot));
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);
        var clock = Stopwatch.StartNew();
        void Stage(string text) => Console.WriteLine($"[{DateTimeOffset.Now:O}] +{clock.Elapsed.TotalSeconds:F2}s {text}");
        Stage("开始真实基底库存；只读打开原始目录");
        var source = new ProjectWorkspace(); source.Open(sourceRoot);
        Stage($"源打开完成：{source.ActiveProfile.ProfileId}/{source.ActiveProfile.VariantId}，{source.Documents.Count}张表");
        var target = new ProjectWorkspace(); target.Open(targetRoot);
        Stage($"目标打开完成：{target.ActiveProfile.ProfileId}/{target.ActiveProfile.VariantId}，{target.Documents.Count}张表");
        var sourceInventory = Inventory(source);
        var targetInventory = Inventory(target);
        WriteJson(reportDirectory, "源基底库存.json", sourceInventory);
        WriteJson(reportDirectory, "目标基底库存.json", targetInventory);
        Stage("核心表、能力、文件长度及全资源路径库存已记录");
        if (phase.Equals("inventory", StringComparison.OrdinalIgnoreCase)) return;

        var converter = new ImageMigrationResourceConverter();
        var service = new MigrationService(converter);
        Stage("开始迁移预览");
        var previewClock = Stopwatch.StartNew();
        var plan = service.Preview(sourceRoot, targetRoot);
        Stage($"迁移预览完成：{plan.Changes.Count}项，耗时{previewClock.Elapsed.TotalSeconds:F2}s");
        WritePreview(reportDirectory, plan, previewClock.Elapsed.TotalSeconds);
        WriteCoverage(reportDirectory, source, target, plan);
        ValidateExtensionBoundary(source, target, plan);
        if (phase.Equals("preview", StringComparison.OrdinalIgnoreCase)) return;

        Stage("计算原始源和原始目标全文件SHA-256");
        var sourceHashes = Manifest(sourceRoot);
        var targetHashes = Manifest(targetRoot);
        WriteJson(reportDirectory, "源原件SHA256.json", sourceHashes);
        WriteJson(reportDirectory, "目标原件SHA256.json", targetHashes);
        // Keep large real-project copies on the report volume by default. The
        // system temporary volume may have far less space than the game drive.
        var copyBase = Environment.GetEnvironmentVariable("CCZ_MIGRATION_AUDIT_TEMP_ROOT")
            ?? Path.Combine(reportDirectory, "临时测试基底");
        var copyRoot = Path.Combine(copyBase, "DataManagerRealMigration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copyRoot);
        foreach (var pair in targetHashes)
        {
            var path = Path.Combine(copyRoot, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(targetRoot, pair.Key), path);
        }
        Stage($"目标完整临时副本完成：{copyRoot}");
        var testPlan = service.Preview(sourceRoot, copyRoot);
        var selected = testPlan.Changes.Select((change, index) => (change, index))
            .Where(item => IsWritable(item.change) && (!phase.Equals("atomic", StringComparison.OrdinalIgnoreCase) || item.change.TableKey == "item-empty-slot-fill"))
            .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
        if (phase.Equals("atomic", StringComparison.OrdinalIgnoreCase))
            Require(selected.Count == 41, $"真实源应有41个明确辅助/道具到目标空闲槽的原子记录，实际{selected.Count}。");
        var expectedConversions = new Dictionary<int, string>();
        foreach (var pair in selected)
        {
            var change = testPlan.Changes[pair.Key];
            if (change.Kind is not (MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged) ||
                !converter.CanConvert(change.TableDisplayName, change.TargetRelativePath ?? change.TableDisplayName)) continue;
            Stage("独立生成转换候选并校验全部像素：" + change.TableDisplayName);
            var candidate = converter.Convert(Path.Combine(sourceRoot, change.TableDisplayName),
                Path.Combine(copyRoot, change.TargetRelativePath ?? change.TableDisplayName));
            expectedConversions.Add(pair.Key, HashService.Sha256(candidate));
        }
        Stage($"开始临时目标迁移：{selected.Count}项");
        var applyClock = Stopwatch.StartNew();
        var result = service.Apply(testPlan, selected, progress: new AuditProgress(Stage));
        Stage($"临时目标迁移返回：Success={result.Success}，耗时{applyClock.Elapsed.TotalSeconds:F2}s；{string.Join("；", result.Messages)}");
        WriteJson(reportDirectory, "迁移执行结果.json", new { TemporaryTarget = copyRoot, ApplySeconds = applyClock.Elapsed.TotalSeconds, Result = result });
        if (!result.Success)
        {
            var failureIntegrity = new List<string>();
            AssertManifest(sourceRoot, sourceHashes, failureIntegrity, "源原件");
            AssertManifest(targetRoot, targetHashes, failureIntegrity, "目标原件");
            WriteJson(reportDirectory, "失败后原件完整性.json", new { Failures = failureIntegrity,
                OriginalSourceUnchanged = failureIntegrity.All(f => !f.StartsWith("源原件", StringComparison.Ordinal)),
                OriginalTargetUnchanged = failureIntegrity.All(f => !f.StartsWith("目标原件", StringComparison.Ordinal)) });
        }
        Require(result.Success, "真实基底迁移失败：" + string.Join("；", result.Messages));

        Stage("重新打开临时目标，逐字段复读及资源SHA核对");
        var actual = new ProjectWorkspace(); actual.Open(copyRoot, testPlan.TargetProfileId, testPlan.TargetVariantId);
        var rowIndexes = actual.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Data.Rows.Cast<DataRow>()
            .ToDictionary(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture)));
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var pair in selected)
        {
            var change = testPlan.Changes[pair.Key];
            var key = change.Kind.ToString(); counts[key] = counts.GetValueOrDefault(key) + 1;
            if (change.Kind is MigrationChangeKind.Changed or MigrationChangeKind.Added)
            {
                if (!rowIndexes.TryGetValue(change.TableKey, out var rows) || !rows.TryGetValue(change.Id, out var row) ||
                    !row.Table.Columns.Contains(change.FieldName) || !Equal(row[change.FieldName], change.SourceValue))
                    failures.Add($"逻辑值不一致：{change.TableKey}/{change.Id}/{change.FieldName}");
            }
            else if (change.Kind is MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged)
            {
                var destination = Path.Combine(copyRoot, change.TargetRelativePath ?? change.TableDisplayName);
                var expectedHash = expectedConversions.GetValueOrDefault(pair.Key) ?? Convert.ToString(change.SourceValue, CultureInfo.InvariantCulture);
                if (!File.Exists(destination) || HashService.Sha256(destination) != expectedHash)
                    failures.Add("资源SHA不一致：" + change.TableDisplayName);
            }
            else if (change.Kind == MigrationChangeKind.NativeStructureChanged)
            {
                if (change.TableKey == "item-effect-name-pool")
                {
                    var slot = new EffectNamePoolService().Read(actual, EffectNameChannel.Item).FirstOrDefault(slot => slot.EffectId == change.Id);
                    if (!Equal(slot?.Name, change.SourceValue)) failures.Add("装备名称池复读不一致：" + change.Id);
                }
                else if (change.TableKey == "job-series-accessory-groups")
                {
                    var groups = new AccessoryJobGroupService().Read(actual);
                    var display = string.Join(" / ", groups.Groups.Select(group => string.Join(",", group.JobSeriesIds)));
                    if (!Equal(display, change.SourceValue)) failures.Add("辅助分组复读不一致");
                }
                else if (change.TableKey == "item-empty-slot-fill")
                {
                    using var payload = JsonDocument.Parse(Convert.ToString(change.SourceValue, CultureInfo.InvariantCulture)!);
                    var tableKey = payload.RootElement.GetProperty("PhysicalTableKey").GetString()!;
                    var row = rowIndexes[tableKey][change.Id];
                    foreach (var field in payload.RootElement.GetProperty("Values").EnumerateObject())
                        if (!Equal(row[field.Name], field.Value.GetString())) failures.Add($"原子空槽复读不一致：{change.Id}/{field.Name}");
                }
                else failures.Add("缺少专用结构验收器：" + change.TableKey);
            }
            else if (change.Kind == MigrationChangeKind.PhysicalFileAdded && !File.Exists(Path.Combine(copyRoot, change.TableDisplayName)))
                failures.Add("物理文件未创建：" + change.TableDisplayName);
        }
        // All untouched physical files, including target-only assets and documents,
        // must retain their original bytes. Selected core files are checked by
        // exact logical field readback above; source and original target are hashed fully.
        var touchedFiles = testPlan.TargetCoreFileFingerprints.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var index in selected.Keys)
        {
            var change = testPlan.Changes[index];
            if (change.Kind is MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged or MigrationChangeKind.PhysicalFileAdded)
                touchedFiles.Add(change.TargetRelativePath ?? change.TableDisplayName);
        }
        foreach (var pair in targetHashes.Where(pair => !touchedFiles.Contains(pair.Key)))
            if (!File.Exists(Path.Combine(copyRoot, pair.Key)) || HashService.Sha256(Path.Combine(copyRoot, pair.Key)) != pair.Value)
                failures.Add("非选中文件发生变化：" + pair.Key);
        AssertManifest(sourceRoot, sourceHashes, failures, "源原件");
        AssertManifest(targetRoot, targetHashes, failures, "目标原件");
        WriteJson(reportDirectory, "逐项验收结果.json", new { TemporaryTarget = copyRoot, Counts = counts, Failures = failures, OriginalSourceUnchanged = failures.All(f => !f.StartsWith("源原件", StringComparison.Ordinal)), OriginalTargetUnchanged = failures.All(f => !f.StartsWith("目标原件", StringComparison.Ordinal)) });
        Require(failures.Count == 0, string.Join("；", failures.Take(20)));
        Stage("REAL_PROJECT_MIGRATION_TESTS_OK：所有已选择逻辑字段、专用结构和资源复读一致；原始源/目标完整SHA不变");
    }

    private static object Inventory(ProjectWorkspace workspace)
    {
        var files = Directory.EnumerateFiles(workspace.RootPath, "*", SearchOption.AllDirectories)
            .Where(path => !HasHiddenDirectory(workspace.RootPath, path))
            .Select(path => new { Path = Path.GetRelativePath(workspace.RootPath, path), Bytes = new FileInfo(path).Length })
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            Root = workspace.RootPath, Profile = workspace.ActiveProfile.ProfileId, Variant = workspace.ActiveProfile.VariantId,
            Detection = workspace.ProfileMatch, Scan = workspace.LastScan, Files = files,
            Tables = workspace.Definitions.Select(definition => new
            {
                definition.Key, definition.DisplayName, definition.FileName, definition.DataOffset, definition.RowCount,
                definition.RowSize, definition.BeginId, definition.EndOffsetExclusive, definition.RecordCodecKey,
                Capability = definition.Capability.ToString(), definition.CapabilityEvidence,
                Loaded = workspace.Documents.ContainsKey(definition.Key),
                ActualRows = workspace.Documents.TryGetValue(definition.Key, out var document) ? document.Data.Rows.Count : 0,
                Fields = definition.Fields.Select(field => new { field.Name, field.Offset, field.Size, Kind = field.Kind.ToString(), Policy = field.Policy.ToString() })
            })
        };
    }

    private static void WritePreview(string directory, MigrationPlan plan, double seconds)
    {
        WriteJson(directory, "迁移差异汇总.json", new
        {
            plan.SourceRoot, plan.TargetRoot, plan.SourceProfileId, plan.SourceVariantId, plan.TargetProfileId, plan.TargetVariantId,
            Seconds = seconds, Total = plan.Changes.Count, Writable = plan.WritableChanges.Count,
            ByKind = plan.Changes.GroupBy(change => change.Kind).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            ByTable = plan.Changes.GroupBy(change => change.TableKey).Select(group => new { Table = group.Key, Count = group.Count(), Writable = group.Count(IsWritable), Kinds = group.GroupBy(change => change.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()) }),
            IncompatibleReasons = plan.Changes.Where(change => change.Kind == MigrationChangeKind.Incompatible).GroupBy(change => new { change.TableKey, change.Reason }).Select(group => new { group.Key.TableKey, group.Key.Reason, Count = group.Count() }),
            IgnoredReasons = plan.Changes.Where(change => change.Kind == MigrationChangeKind.Ignored).GroupBy(change => new { change.TableKey, change.Reason }).Select(group => new { group.Key.TableKey, group.Key.Reason, Count = group.Count() }),
            plan.PhysicalFileCreations
        });
        using var csv = new StreamWriter(Path.Combine(directory, "迁移逐项差异.csv"), false, new UTF8Encoding(true));
        csv.WriteLine("表键,表名称,记录编号,字段,源值,目标值,类型,目标路径,原因");
        foreach (var change in plan.Changes)
            csv.WriteLine(string.Join(",", new[] { change.TableKey, change.TableDisplayName, change.Id.ToString(CultureInfo.InvariantCulture), change.FieldName,
                Convert.ToString(change.SourceValue, CultureInfo.InvariantCulture), Convert.ToString(change.TargetValue, CultureInfo.InvariantCulture), change.Kind.ToString(), change.TargetRelativePath, change.Reason }.Select(Csv)));
    }

    private static Dictionary<string, string> Manifest(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), HashService.Sha256, StringComparer.OrdinalIgnoreCase);

    private static void AssertManifest(string root, IReadOnlyDictionary<string, string> expected, List<string> failures, string label)
    {
        var actual = Manifest(root);
        foreach (var pair in expected)
            if (!actual.TryGetValue(pair.Key, out var hash) || hash != pair.Value) failures.Add($"{label}SHA变化：{pair.Key}");
        foreach (var key in actual.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase)) failures.Add($"{label}出现新文件：{key}");
    }

    private static bool HasHiddenDirectory(string root, string path)
        => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith(".", StringComparison.Ordinal));
    private static bool IsWritable(MigrationChange change) => change.Kind is MigrationChangeKind.Added or MigrationChangeKind.Changed or MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged or MigrationChangeKind.PhysicalFileAdded or MigrationChangeKind.NativeStructureChanged;
    private static bool Equal(object? left, object? right) => string.Equals(Convert.ToString(left is DBNull ? null : left, CultureInfo.InvariantCulture), Convert.ToString(right is DBNull ? null : right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    private static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    private static void WriteJson(string root, string name, object value) => File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class AuditProgress(Action<string> stage) : IProgress<MigrationProgress>
    {
        private string _lastStage = string.Empty;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public void Report(MigrationProgress value)
        {
            if (value.Stage == _lastStage && _clock.Elapsed.TotalSeconds < 5) return;
            _lastStage = value.Stage; _clock.Restart();
            stage($"{value.Stage}：{value.Completed}/{value.Total}；{value.Detail}");
        }
    }

    private static void ValidateExtensionBoundary(ProjectWorkspace source, ProjectWorkspace target, MigrationPlan plan)
    {
        var item = Path.Combine(source.RootPath, "Item.e5");
        if (!File.Exists(item) || new FileInfo(item).Length != 6400 || File.Exists(Path.Combine(target.RootPath, "ExtendData.dll"))) return;
            Require(plan.Changes.Count(change => change.TableKey == "extended-items" && change.Kind == MigrationChangeKind.Ignored) == 1,
                "目标没有扩展物品消费者时应将 ID256～511 汇总为一条整段忽略诊断。");
        Require(plan.Changes.Count(change => change.TableKey == "extended-runtime-capability" && change.Kind == MigrationChangeKind.Incompatible) == 4,
            "未报告四张扩展表在目标缺少运行时消费者，数据落盘不能等同功能生效。");
        Require(!plan.WritableChanges.Any(change => change.TableKey == "__resource__" && change.TableDisplayName is "Item.e5" or "Itemmsg.e5"),
            "扩展Item数据被错误当作图像/普通文件迁移。");
    }

    private static void WriteCoverage(string directory, ProjectWorkspace source, ProjectWorkspace target, MigrationPlan plan)
    {
        var perTable = plan.Changes.GroupBy(change => change.TableKey).ToDictionary(group => group.Key, group => group.ToArray());
        var rows = new List<object>();
        foreach (var definition in source.Definitions)
        {
            source.Documents.TryGetValue(definition.Key, out var sourceDocument);
            target.Documents.TryGetValue(definition.Key, out var targetDocument);
            var changes = perTable.GetValueOrDefault(definition.Key) ?? [];
            var preserveDifferences = new List<object>();
            if (sourceDocument != null && targetDocument != null)
            {
                var targetFields = targetDocument.Definition.Fields.ToDictionary(field => field.Name);
                foreach (var field in definition.Fields.Where(field => field.Policy != FieldPolicy.Editable))
                {
                    if (!targetFields.TryGetValue(field.Name, out var targetField) || targetField.Size != field.Size) continue;
                    var differingIds = new List<int>();
                    for (var index = 0; index < sourceDocument.Data.Rows.Count; index++)
                    {
                        var id = definition.BeginId + index;
                        var targetIndex = id - targetDocument.Definition.BeginId;
                        if (targetIndex < 0 || targetIndex >= targetDocument.Data.Rows.Count) continue;
                        var sourceOffset = checked((int)(definition.DataOffset + (long)index * definition.RowSize + field.Offset));
                        var targetOffset = checked((int)(targetDocument.Definition.DataOffset + (long)targetIndex * targetDocument.Definition.RowSize + targetField.Offset));
                        if (!sourceDocument.OriginalFileBytes.AsSpan(sourceOffset, field.Size).SequenceEqual(targetDocument.OriginalFileBytes.AsSpan(targetOffset, targetField.Size)))
                            differingIds.Add(id);
                    }
                    if (differingIds.Count > 0) preserveDifferences.Add(new { field.Name, Policy = field.Policy.ToString(), Rows = differingIds.Count, FirstIds = differingIds.Take(40) });
                }
            }
            rows.Add(new
            {
                Table = definition.Key, definition.DisplayName, definition.FileName,
                SourceLoaded = sourceDocument != null, TargetLoaded = targetDocument != null,
                SourceCapability = definition.Capability.ToString(), definition.CapabilityEvidence,
                SourceRows = definition.RowCount, SourceEditableFields = definition.Fields.Count(field => field.Policy == FieldPolicy.Editable),
                SourcePreservedOrReadOnlyFields = definition.Fields.Count(field => field.Policy != FieldPolicy.Editable),
                WritableChangedCells = changes.Count(change => change.Kind == MigrationChangeKind.Changed),
                IncompatibleChanges = changes.Count(change => change.Kind == MigrationChangeKind.Incompatible),
                IgnoredChanges = changes.Count(change => change.Kind == MigrationChangeKind.Ignored),
                AtomicCompleteRows = definition.Key is "items-low" or "items-high"
                    ? plan.Changes.Count(change => change.TableKey == "item-empty-slot-fill" && change.Id >= definition.BeginId && change.Id < definition.BeginId + definition.RowCount) : 0,
                MissingTableDiagnostics = changes.Where(change => change.Kind is MigrationChangeKind.MissingSourceTable or MigrationChangeKind.MissingTargetTable),
                PreservedDifferences = preserveDifferences
            });
        }
        WriteJson(directory, "逐表字段覆盖和保留差异.json", rows);
    }
}
