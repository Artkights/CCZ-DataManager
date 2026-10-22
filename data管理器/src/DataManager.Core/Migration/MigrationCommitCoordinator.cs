using System.Globalization;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Models;

namespace DataManager.Core.Migration;

/// <summary>
/// Builds the complete migration in an isolated project before replacing any target file.
/// Per-file replacement is atomic; the backup journal makes a multi-file failure reversible.
/// </summary>
internal static class MigrationCommitCoordinator
{
    public static MigrationApplyResult Apply(MigrationPlan plan, IReadOnlyDictionary<int, MigrationChoice>? choices,
        bool includeAllWritable, Func<MigrationPlan, IReadOnlyDictionary<int, MigrationChoice>?, bool, MigrationApplyResult> build,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default,
        IMigrationResourceVerifier? resourceVerifier = null)
    {
        string? stagingRoot = null;
        IDisposable? stagingHistoryScope = null;
        string? backupRoot = null;
        bool? historyManifestExisted = null;
        var installed = new List<PreparedFile>();
        var temporaryPaths = new List<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("校验迁移快照", "检查源与目标是否在预览后改变"));
            MigrationService.ValidateRoots(plan.SourceRoot, plan.TargetRoot);
            if (!Directory.Exists(plan.SourceRoot) || !Directory.Exists(plan.TargetRoot))
                throw new DirectoryNotFoundException("源目录或目标目录不存在。");
            var selected = plan.Changes.Select((change, index) => (Change: change, Index: index))
                .Where(item => IsWritable(item.Change) && Choice(item.Index) != MigrationChoice.Skip).ToArray();
            var selectedResources = selected.Where(item => IsResource(item.Change)).ToArray();
            if (selectedResources.Any(item => IsEvidenceConfiguration(TargetRelative(item.Change))) &&
                selected.Any(item => !IsResource(item.Change) && item.Change.Kind != MigrationChangeKind.PhysicalFileAdded))
                throw new InvalidOperationException("所选 System.ini 可能改变布局证据或 DefID/AssID 物品边界；请先单独迁移配置，再重新预览并选择数据，避免使用旧配置映射逻辑字段。");
            ValidateSnapshot(plan);
            ValidateResources(plan, selectedResources.Select(item => item.Change));
            if (selected.Length == 0)
                return new MigrationApplyResult(true, 0, plan.Changes.Count, null, ["没有选择需要写入的迁移项。"]);

            CheckFreeSpace(plan, selectedResources.Select(item => item.Change));
            // Keep the build on the target volume. A large mod must not fill
            // the unrelated Windows system temporary drive.
            stagingRoot = ResolveInside(plan.TargetRoot, Path.Combine(".data管理器迁移临时", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(stagingRoot);
            stagingHistoryScope = ProjectHistoryPaths.UseProjectStorage(stagingRoot, Path.Combine(stagingRoot, ".data管理器临时历史"));
            progress?.Report(new("创建临时项目", "目标原件尚未修改"));
            // Specialized codecs may verify an already customized name pool by
            // replaying checked target history. Carry that evidence into staging,
            // then append only entries created by this migration to the real target.
            var existingHistory = new HistoryStore().Load(plan.TargetRoot);
            var existingHistoryIds = existingHistory.Select(entry => entry.OperationId).ToHashSet(StringComparer.Ordinal);
            if (existingHistory.Count > 0) new HistoryStore().Replace(stagingRoot, existingHistory);
            foreach (var pair in plan.TargetCoreFileFingerprints.Where(pair => pair.Value.Length > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyFile(ResolveInside(plan.TargetRoot, pair.Key), ResolveInside(stagingRoot, pair.Key));
            }
            CopyTargetLayoutEvidence(plan, stagingRoot);
            foreach (var item in selectedResources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new("复制临时资源", TargetRelative(item.Change)));
                var relative = TargetRelative(item.Change);
                var original = ResolveInside(plan.TargetRoot, relative);
                if (File.Exists(original)) CopyFile(original, ResolveInside(stagingRoot, relative));
            }
            var selectedCreations = plan.PhysicalFileCreations.Where(file => selected.Any(item =>
                item.Change.Kind == MigrationChangeKind.PhysicalFileAdded &&
                item.Change.TableDisplayName.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase))).ToArray();
            var stagedFingerprints = plan.TargetCoreFileFingerprints.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var creation in selectedCreations)
            {
                var path = ResolveInside(stagingRoot, creation.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[creation.Length]);
                stagedFingerprints[creation.RelativePath] = HashService.Sha256(path);
            }

            var stagedPlan = new MigrationPlan
            {
                SourceRoot = plan.SourceRoot,
                TargetRoot = stagingRoot,
                SourceProfileId = plan.SourceProfileId,
                SourceVariantId = plan.SourceVariantId,
                TargetProfileId = plan.TargetProfileId,
                TargetVariantId = plan.TargetVariantId,
                Changes = plan.Changes,
                PhysicalFileCreations = plan.PhysicalFileCreations,
                SourceCoreFileFingerprints = plan.SourceCoreFileFingerprints,
                TargetCoreFileFingerprints = stagedFingerprints
                , SourceEvidenceFingerprints = plan.SourceEvidenceFingerprints,
                TargetEvidenceFingerprints = plan.TargetEvidenceFingerprints
            };
            var stagedResult = build(stagedPlan, choices, includeAllWritable);
            if (!stagedResult.Success) return stagedResult with { BackupPath = null };
            var resourceDiagnostics = new List<string>();
            var resourceDependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (resourceVerifier != null)
                foreach (var relative in selectedResources.Select(item => TargetRelative(item.Change)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new("语义校验迁移资源", relative));
                    var result = resourceVerifier.ValidateFile(stagingRoot, plan.TargetRoot, relative,
                        File.ReadAllBytes(ResolveInside(stagingRoot, relative)), cancellationToken);
                    resourceDiagnostics.Add($"{relative}：{(result.Verified ? "语义验证通过" : "验证边界")}；{result.Detail}");
                    if (result.DependencyHashes != null)
                        foreach (var dependency in result.DependencyHashes)
                        {
                            if (resourceDependencies.TryGetValue(dependency.Key, out var previous) && !previous.Equals(dependency.Value, StringComparison.OrdinalIgnoreCase))
                                throw new IOException($"迁移依赖在校验期间改变：{dependency.Key}。");
                            resourceDependencies[dependency.Key] = dependency.Value;
                        }
                }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("校验临时项目", "复读布局、候选文件和SHA"));

            // Validate the entire staged physical project, then confirm both real projects
            // still match the preview before entering the commit phase.
            var stagedWorkspace = new ProjectWorkspace();
            stagedWorkspace.Open(stagingRoot, plan.TargetProfileId, plan.TargetVariantId);
            var createdHistory = stagedWorkspace.GetHistory().Where(entry => !existingHistoryIds.Contains(entry.OperationId)).ToArray();
            if (createdHistory.Any(entry => entry.Status == HistoryStatus.Active && entry.LayoutRevision != stagedWorkspace.ActiveProfile.LayoutRevision))
                throw new InvalidOperationException("所选配置/资源改变了迁移后的布局能力，保存历史无法按原布局撤回；请分开迁移配置/资源并重新预览数据。");
            var candidatePaths = plan.TargetCoreFileFingerprints.Keys
                .Concat(selectedResources.Where(item => Choice(item.Index) != MigrationChoice.Merge || item.Change.TargetValue == null)
                    .Select(item => TargetRelative(item.Change)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var prepared = new List<PreparedFile>();
            foreach (var relative in candidatePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = ResolveInside(stagingRoot, relative);
                if (!File.Exists(candidate)) continue;
                var target = ResolveInside(plan.TargetRoot, relative);
                var before = File.Exists(target) ? HashService.Sha256(target) : null;
                var after = HashService.Sha256(candidate);
                if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) continue;
                prepared.Add(new PreparedFile(relative, before, after, before == null ? null : File.GetAttributes(target)));
            }
            ValidateSnapshot(plan);
            ValidateResources(plan, selectedResources.Select(item => item.Change));
            foreach (var dependency in resourceDependencies)
                VerifyFile(ResolveInside(plan.TargetRoot, dependency.Key), dependency.Value, "资源语义校验依赖");
            if (prepared.Count == 0) return stagedResult with { BackupPath = null };
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("备份目标", $"{prepared.Count} 个待提交文件"));

            backupRoot = ProjectHistoryPaths.ResolveInside(ProjectHistoryPaths.GetMigrationRoot(plan.TargetRoot),
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupRoot);
            foreach (var pair in plan.TargetCoreFileFingerprints.Where(pair => pair.Value.Length > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Backup(pair.Key);
            }
            foreach (var item in prepared.Where(item => item.BeforeSha256 != null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Backup(item.RelativePath);
            }
            var historyManifest = Path.Combine(new HistoryStore().GetRoot(plan.TargetRoot), "manifest.jsonl");
            historyManifestExisted = File.Exists(historyManifest);
            if (historyManifestExisted.Value)
                CopyFile(historyManifest, Path.Combine(backupRoot, "history-manifest.jsonl"));
            WriteJournal("Prepared", null);
            cancellationToken.ThrowIfCancellationRequested();
            // Once replacements start, finish the transaction or roll it back.
            // A late UI cancellation must never leave a partly installed mod.
            progress?.Report(new("提交目标", "开始原子替换；此阶段取消请求将在完整提交后返回", 0, prepared.Count));

            foreach (var item in prepared)
            {
                progress?.Report(new("提交目标", item.RelativePath, installed.Count, prepared.Count));
                var candidate = ResolveInside(stagingRoot, item.RelativePath);
                var target = ResolveInside(plan.TargetRoot, item.RelativePath);
                VerifyFile(target, item.BeforeSha256, "提交前目标资源");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".data管理器迁移." + Guid.NewGuid().ToString("N") + ".tmp";
                temporaryPaths.Add(temporary);
                File.Copy(candidate, temporary);
                MakeWritable(temporary);
                VerifyFile(temporary, item.AfterSha256, "迁移候选文件");
                // Register before replacement so a failure after rename is still rolled back.
                installed.Add(item);
                MakeWritable(target);
                File.Move(temporary, target, true);
                if (item.BeforeAttributes is { } attributes) File.SetAttributes(target, attributes);
                VerifyFile(target, item.AfterSha256, "写后复读");
            }
            var targetWorkspace = new ProjectWorkspace();
            targetWorkspace.Open(plan.TargetRoot, plan.TargetProfileId, plan.TargetVariantId);
            if (resourceVerifier != null)
                foreach (var relative in selectedResources.Select(item => TargetRelative(item.Change)).Distinct(StringComparer.OrdinalIgnoreCase))
                    _ = resourceVerifier.ValidateFile(plan.TargetRoot, plan.TargetRoot, relative,
                        File.ReadAllBytes(ResolveInside(plan.TargetRoot, relative)), cancellationToken);
            foreach (var entry in createdHistory)
            {
                entry.OperationKind = "一键迁移";
                entry.Summary = $"迁移 {new DirectoryInfo(plan.SourceRoot).Name} → {new DirectoryInfo(plan.TargetRoot).Name}：{entry.Summary}";
                targetWorkspace.History.Append(plan.TargetRoot, entry);
            }
            targetWorkspace.Open(plan.TargetRoot, plan.TargetProfileId, plan.TargetVariantId);
            if (targetWorkspace.ActiveProfile.LayoutRevision != stagedWorkspace.ActiveProfile.LayoutRevision)
                throw new InvalidDataException("迁移目标复读后的布局能力与临时项目不一致，已中止并回滚。");
            WriteJournal("Committed", null);
            progress?.Report(new("迁移完成", $"已复读 {prepared.Count} 个目标文件", prepared.Count, prepared.Count));
            return stagedResult with
            {
                BackupPath = backupRoot,
                Messages = [.. stagedResult.Messages, .. resourceDiagnostics, $"临时副本校验及目标 SHA-256 复读完成；迁移历史：{Path.Combine(backupRoot, "migration.json")}"]
            };

            MigrationChoice Choice(int index) => includeAllWritable ? MigrationChoice.Overwrite
                : choices != null && choices.TryGetValue(index, out var choice) ? choice : MigrationChoice.Skip;
            void Backup(string relative)
            {
                var destination = ResolveInside(Path.Combine(backupRoot!, "files"), relative);
                if (!File.Exists(destination)) CopyFile(ResolveInside(plan.TargetRoot, relative), destination);
            }
            void WriteJournal(string status, string? error) => File.WriteAllText(Path.Combine(backupRoot!, "migration.json"),
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = "2.0", Status = status, Error = error, plan.CreatedAt,
                    CommittedAt = DateTime.Now, plan.SourceRoot, plan.TargetRoot,
                    plan.SourceProfileId, plan.SourceVariantId, plan.TargetProfileId, plan.TargetVariantId,
                    Files = prepared,
                    // Stable logical coordinates, compressed by consecutive IDs.
                    // Repeating a full object per scalar made a dense migration
                    // produce another hundred-megabyte journal twice per commit.
                    Choices = selected.Where(item => !IsResource(item.Change) && item.Change.Kind != MigrationChangeKind.PhysicalFileAdded)
                        .GroupBy(item => new { item.Change.TableKey, item.Change.FieldName, item.Change.Kind, Action = Choice(item.Index) })
                        .Select(group => new { group.Key.TableKey, group.Key.FieldName, Kind = group.Key.Kind.ToString(),
                            Choice = group.Key.Action.ToString(), RecordIdRanges = PackIds(group.Select(item => item.Change.Id)) }),
                    FileChoices = selected.Where(item => IsResource(item.Change) || item.Change.Kind == MigrationChangeKind.PhysicalFileAdded)
                        .Select(item => new { item.Change.TableKey, SourcePath = item.Change.TableDisplayName,
                            TargetPath = TargetRelative(item.Change), Choice = Choice(item.Index).ToString() }),
                    Unmapped = plan.Changes.Where(change => change.Kind is MigrationChangeKind.Incompatible or MigrationChangeKind.MissingSourceTable or MigrationChangeKind.MissingTargetTable)
                        .GroupBy(change => new { change.TableKey, change.FieldName, change.Kind, change.Reason })
                        .Select(group => new { group.Key.TableKey, group.Key.FieldName, Kind = group.Key.Kind.ToString(),
                            group.Key.Reason, RecordIdRanges = PackIds(group.Select(change => change.Id)) }),
                    Ignored = plan.Changes.Where(change => change.Kind == MigrationChangeKind.Ignored)
                        .GroupBy(change => new { change.TableKey, change.FieldName, change.Reason })
                        .Select(group => new { group.Key.TableKey, group.Key.FieldName, Kind = "Ignored",
                            group.Key.Reason, RecordIdRanges = PackIds(group.Select(change => change.Id)) })
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            progress?.Report(new("恢复目标", ex is OperationCanceledException ? "已取消，清理临时项目" : ex.Message));
            var failures = new List<string>();
            foreach (var item in installed.AsEnumerable().Reverse())
            {
                try
                {
                    var target = ResolveInside(plan.TargetRoot, item.RelativePath);
                    if (item.BeforeSha256 == null)
                    {
                        if (File.Exists(target)) { MakeWritable(target); File.Delete(target); }
                    }
                    else
                    {
                        if (File.Exists(target) && HashService.Sha256(target).Equals(item.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.BeforeAttributes is { } originalAttributes) File.SetAttributes(target, originalAttributes);
                            continue;
                        }
                        var snapshot = ResolveInside(Path.Combine(backupRoot!, "files"), item.RelativePath);
                        CopyFile(snapshot, target);
                        if (item.BeforeAttributes is { } attributes) File.SetAttributes(target, attributes);
                        VerifyFile(target, item.BeforeSha256, "回滚复读");
                    }
                }
                catch (Exception rollback) { failures.Add($"{item.RelativePath}：{rollback.Message}"); }
            }
            if (historyManifestExisted != null)
            {
                try
                {
                    var historyManifest = Path.Combine(new HistoryStore().GetRoot(plan.TargetRoot), "manifest.jsonl");
                    if (historyManifestExisted.Value)
                        CopyFile(Path.Combine(backupRoot!, "history-manifest.jsonl"), historyManifest);
                    else if (File.Exists(historyManifest)) File.Delete(historyManifest);
                }
                catch (Exception rollback) { failures.Add($"历史记录：{rollback.Message}"); }
            }
            if (backupRoot != null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(backupRoot, "failure.json"), JsonSerializer.Serialize(new
                    { Status = failures.Count == 0 ? "RolledBack" : "RollbackFailed", Error = ex.Message, RollbackErrors = failures }));
                }
                catch { /* The original error and rollback status remain in the returned result. */ }
            }
            return new MigrationApplyResult(false, 0, 0, backupRoot,
                [ex is OperationCanceledException ? "迁移已取消。" : $"迁移失败：{ex.Message}", failures.Count == 0 ? "目标已保持原状或从写前备份恢复。" : $"目标恢复失败：{string.Join("；", failures)}", "源目录未修改。"]);
        }
        finally
        {
            stagingHistoryScope?.Dispose();
            foreach (var path in temporaryPaths)
                try { if (File.Exists(path)) { MakeWritable(path); File.Delete(path); } } catch { }
            if (stagingRoot != null)
                try
                {
                    // Only this freshly-created, path-checked workspace is cleaned.
                    foreach (var file in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)) MakeWritable(file);
                    Directory.Delete(stagingRoot, true);
                    var parent = Path.GetDirectoryName(stagingRoot)!;
                    if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                }
                catch { }
        }
    }

    private static bool IsWritable(MigrationChange change)
        => change.Kind is MigrationChangeKind.Added or MigrationChangeKind.Changed or MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged or MigrationChangeKind.PhysicalFileAdded or MigrationChangeKind.NativeStructureChanged;

    private static IReadOnlyList<int[]> PackIds(IEnumerable<int> ids)
    {
        var values = ids.Distinct().Order().ToArray();
        var ranges = new List<int[]>();
        for (var index = 0; index < values.Length; index++)
        {
            var start = values[index];
            var end = start;
            while (index + 1 < values.Length && (long)values[index + 1] == (long)end + 1) end = values[++index];
            ranges.Add([start, end]);
        }
        return ranges;
    }

    private static void CopyTargetLayoutEvidence(MigrationPlan plan, string stagingRoot)
    {
        foreach (var relative in MigrationService.EvidenceConfigurations)
        {
            var path = ResolveInside(plan.TargetRoot, relative);
            if (File.Exists(path)) CopyFile(path, ResolveInside(stagingRoot, relative));
        }
        if (!plan.TargetProfileId.Equals("ccz-6.5", StringComparison.OrdinalIgnoreCase)) return;
        var target = new ProjectWorkspace();
        target.Open(plan.TargetRoot, plan.TargetProfileId, plan.TargetVariantId);
        if (!target.Definitions.Any(table => table.Key == "person-r-assign" && table.Capability == TableCapability.ReadOnly)) return;
        // Unknown engines with static evidence stay read-only. These archives
        // are needed only for that probe, and do not become commit candidates.
        foreach (var name in new[] { "Pmapobj.e5", "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" })
        {
            var path = ProjectFileLocator.Resolve(plan.TargetRoot, name);
            if (File.Exists(path)) CopyFile(path, ResolveInside(stagingRoot, Path.GetRelativePath(plan.TargetRoot, path)));
        }
    }

    private static bool IsResource(MigrationChange change)
        => change.Kind is MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged;

    private static bool IsEvidenceConfiguration(string relative)
        => MigrationService.EvidenceConfigurations.Any(path => path.Replace('/', Path.DirectorySeparatorChar)
            .Equals(relative.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    private static string TargetRelative(MigrationChange change) => change.TargetRelativePath ?? change.TableDisplayName;

    private static void ValidateSnapshot(MigrationPlan plan)
    {
        foreach (var pair in plan.SourceCoreFileFingerprints)
            VerifyFile(ResolveInside(plan.SourceRoot, pair.Key), pair.Value.Length == 0 ? null : pair.Value, "迁移源在预览后发生变化");
        foreach (var pair in plan.TargetCoreFileFingerprints)
            VerifyFile(ResolveInside(plan.TargetRoot, pair.Key), pair.Value.Length == 0 ? null : pair.Value, "迁移目标在预览后发生变化");
        foreach (var pair in plan.SourceEvidenceFingerprints)
            VerifyFile(ResolveInside(plan.SourceRoot, pair.Key), pair.Value.Length == 0 ? null : pair.Value, "迁移源配置在预览后发生变化");
        foreach (var pair in plan.TargetEvidenceFingerprints)
            VerifyFile(ResolveInside(plan.TargetRoot, pair.Key), pair.Value.Length == 0 ? null : pair.Value, "迁移目标配置在预览后发生变化");
    }

    private static void ValidateResources(MigrationPlan plan, IEnumerable<MigrationChange> resources)
    {
        foreach (var resource in resources)
        {
            VerifyFile(ResolveInside(plan.SourceRoot, resource.TableDisplayName), resource.SourceValue as string, "迁移源资源在预览后发生变化");
            VerifyFile(ResolveInside(plan.TargetRoot, TargetRelative(resource)), resource.TargetValue as string, "迁移目标资源在预览后发生变化");
        }
    }

    private static void VerifyFile(string path, string? expectedHash, string description)
    {
        MigrationPathSafety.RequireNoLinks(path);
        if (expectedHash == null)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"{description}：{path} 原本不存在。");
            return;
        }
        if (!File.Exists(path)) throw new IOException($"{description}：缺少 {path}。");
        if (!HashService.Sha256(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{description}：{path} SHA-256 已变化。");
    }

    private static string ResolveInside(string root, string relative)
        => MigrationPathSafety.Resolve(root, relative);

    private static void CopyFile(string source, string target)
    {
        MigrationPathSafety.RequireNoLinks(source);
        MigrationPathSafety.RequireNoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        MakeWritable(target);
        File.Copy(source, target, true);
        MakeWritable(target);
    }

    private static void MakeWritable(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void CheckFreeSpace(MigrationPlan plan, IEnumerable<MigrationChange> resources)
    {
        long physicalBytes = plan.TargetCoreFileFingerprints.Where(pair => pair.Value.Length > 0)
            .Sum(pair => new FileInfo(ResolveInside(plan.TargetRoot, pair.Key)).Length);
        long sourceBytes = 0, targetBytes = 0, largest = 0;
        foreach (var resource in resources)
        {
            var sourceLength = new FileInfo(ResolveInside(plan.SourceRoot, resource.TableDisplayName)).Length;
            var target = ResolveInside(plan.TargetRoot, TargetRelative(resource));
            sourceBytes = checked(sourceBytes + sourceLength);
            targetBytes = checked(targetBytes + (File.Exists(target) ? new FileInfo(target).Length : 0));
            largest = Math.Max(largest, sourceLength);
        }
        var required = checked(physicalBytes * 8 + sourceBytes * 2 + targetBytes * 2 + largest * 2 +
            plan.PhysicalFileCreations.Sum(file => (long)file.Length) * 4 + 64L * 1024 * 1024);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(plan.TargetRoot))!);
        if (drive.IsReady && drive.AvailableFreeSpace < required)
            throw new IOException($"目标磁盘可用空间不足：迁移临时项目、备份和提交预计需 {required / 1048576:N0} MB，可用 {drive.AvailableFreeSpace / 1048576:N0} MB。目标尚未提交修改。");
        var historyDrive = new DriveInfo(Path.GetPathRoot(ProjectHistoryPaths.GetMigrationRoot(plan.TargetRoot))!);
        if (!historyDrive.Name.Equals(drive.Name, StringComparison.OrdinalIgnoreCase))
        {
            var manifest = ProjectHistoryPaths.GetDataManifestForRead(plan.TargetRoot);
            var backupRequired = checked(physicalBytes + targetBytes + (File.Exists(manifest) ? new FileInfo(manifest).Length : 0) + 64L * 1024 * 1024);
            if (historyDrive.IsReady && historyDrive.AvailableFreeSpace < backupRequired)
                throw new IOException($"历史记录磁盘可用空间不足：迁移备份预计需 {backupRequired / 1048576:N0} MB，可用 {historyDrive.AvailableFreeSpace / 1048576:N0} MB。目标尚未提交修改。");
        }
    }

    private sealed record PreparedFile(string RelativePath, string? BeforeSha256, string AfterSha256, FileAttributes? BeforeAttributes);
}
