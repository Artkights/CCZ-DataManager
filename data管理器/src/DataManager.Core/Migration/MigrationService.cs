using System.Data;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Domain;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Core.Migration;

/// <summary>
/// Performs profile-aware logical migration. It intentionally compares table keys,
/// record IDs and field names instead of copying physical offsets between versions.
/// </summary>
public sealed class MigrationService
{
    private readonly IMigrationResourceConverter? _resourceConverter;
    public MigrationService(IMigrationResourceConverter? resourceConverter = null) => _resourceConverter = resourceConverter;
    internal static readonly string[] EvidenceConfigurations = ["System.ini", "RS/System.ini", "B形象指定器/6.6x形象指定器/System.ini", "B形象指定器/形象指定器6.5/System.ini"];
    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".e5", ".dll", ".exe", ".ini", ".eex", ".jpg", ".jpeg", ".png", ".bmp", ".gif",
        ".ico", ".webp", ".tif", ".tiff", ".avi", ".mp4", ".dat", ".bin", ".map",
        ".wav", ".mp3", ".ogg", ".mid", ".midi", ".wma", ".flac"
    };

    private static readonly HashSet<string> CoreFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data.e5", "Star.e5", "Ekd5.exe", "Imsg.e5"
    };

    private static readonly IReadOnlyDictionary<string, int> CreatablePhysicalFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Effect.e5"] = 1024 * 1024,
        ["Effectmsg.e5"] = 256 * 8,
        ["MeffStudy.e5"] = 1024 * 64,
        ["Forge.e5"] = 1024 * 64
    };

    public MigrationPlan Preview(string sourceRoot, string targetRoot, string? sourceProfile = null, string? sourceVariant = null, string? targetProfile = null, string? targetVariant = null,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateRoots(sourceRoot, targetRoot);
        sourceRoot = ResolveProjectRoot(sourceRoot);
        targetRoot = ResolveProjectRoot(targetRoot);
        ValidateRoots(sourceRoot, targetRoot);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("扫描源基底", sourceRoot));
        var source = new ProjectWorkspace();
        var target = new ProjectWorkspace();
        var sourceReport = source.Open(sourceRoot, sourceProfile, sourceVariant);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("扫描目标基底", targetRoot));
        var targetReport = target.Open(targetRoot, targetProfile, targetVariant);
        var changes = new List<MigrationChange>();
        var targetDocuments = target.Documents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var fileCreations = AddCreatablePhysicalDocuments(source, target, targetDocuments, changes);
        var createdTables = fileCreations.SelectMany(file => file.TableKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = source.Documents.Keys.Union(targetDocuments.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        var completedTables = 0;

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("比较数据表", key, completedTables++, keys.Length));
            // These native pools/groups are compared by their dedicated parsers below.
            if (key is "effect-names" or "item-effect-names-low" or "item-effect-names-high" or "item-effect-names-66" or "job-series-accessory-groups") continue;
            var sourceHas = source.Documents.TryGetValue(key, out var sourceDocument);
            var targetHas = targetDocuments.TryGetValue(key, out var targetDocument);
            if (!sourceHas || sourceDocument == null)
            {
                changes.Add(new MigrationChange(key, targetDocument?.Definition.DisplayName ?? key, string.Empty, 0, null, null, MigrationChangeKind.MissingSourceTable, "源项目没有可读取的表"));
                continue;
            }
            if (!targetHas || targetDocument == null)
            {
                changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, string.Empty, 0, null, null, MigrationChangeKind.MissingTargetTable, "目标项目没有可读取的表"));
                continue;
            }

            var targetById = targetDocument.Data.Rows.Cast<DataRow>()
                .ToDictionary(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture));
            foreach (DataRow sourceRow in sourceDocument.Data.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Convert.ToInt32(sourceRow["ID"], CultureInfo.InvariantCulture);
                if (key == "effect-messages" && id == 255)
                {
                    changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, string.Empty, id, null, null,
                        MigrationChangeKind.Ignored, "ID=255 是引擎保留成长槽，保留目标字节。"));
                    continue;
                }
                if (!targetById.TryGetValue(id, out var targetRow))
                {
                    changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, string.Empty, id, null, null, MigrationChangeKind.Incompatible, "目标没有同 ID 记录"));
                    continue;
                }

                if (AtomicItemMigrationMapper.TryMap(source, target, sourceDocument, targetDocument, sourceRow, targetRow, changes)) continue;
                if (ItemMigrationMapper.Prepare(source, target, sourceDocument, targetDocument, sourceRow, targetRow, changes, out var mappedItemValues)) continue;

                foreach (var sourceField in sourceDocument.Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable))
                {
                    if (!targetDocument.Data.Columns.Contains(sourceField.Name))
                    {
                        changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, sourceField.Name, id, sourceRow[sourceField.Name], null, MigrationChangeKind.Incompatible, "目标没有同名字段"));
                        continue;
                    }
                    var targetField = TableFieldIndex.Find(targetDocument.Definition, sourceField.Name);
                    if (targetField == null || targetField.Policy != FieldPolicy.Editable)
                    {
                        changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, sourceField.Name, id, sourceRow[sourceField.Name], targetRow[sourceField.Name], MigrationChangeKind.Incompatible, "目标字段只读或未定义"));
                        continue;
                    }
                    var sourceValue = mappedItemValues.TryGetValue(sourceField.Name, out var mappedItemValue) ? mappedItemValue
                        : sourceRow[sourceField.Name] == DBNull.Value ? null : sourceRow[sourceField.Name];
                    var targetValue = targetRow[sourceField.Name] == DBNull.Value ? null : targetRow[sourceField.Name];
                    if (ValuesEqual(sourceValue, targetValue)) continue;
                    if (sourceDocument.Definition.Capability != TableCapability.Editable)
                    {
                        changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, sourceField.Name, id, sourceValue, targetValue, MigrationChangeKind.Incompatible,
                            $"源表不可写：{sourceDocument.Definition.CapabilityEvidence}"));
                        continue;
                    }
                    // A same-name field can still be incompatible when a
                    // 6.6 variant changes its width, codec or valid range.
                    // Detect this while previewing so Apply never fails after
                    // the user has selected an apparently writable change.
                    var targetRowIndex = targetDocument.Data.Rows.IndexOf(targetRow);
                    try
                    {
                        target.ValidateCellValue(targetDocument, targetRowIndex, targetField.Name, sourceValue, validateEquipment: false);
                        if (key == "forge" && targetField.Name == "材料项数" && Convert.ToInt32(sourceValue, CultureInfo.InvariantCulture) > 8)
                            throw new InvalidDataException("材料项数必须在 0..8 范围内。");
                        var alias = MigrationFieldIsolation.FindConflict(target, targetDocument, targetRowIndex, targetField, sourceValue);
                        if (alias != null) throw new InvalidOperationException(alias);
                    }
                    catch (Exception ex)
                    {
                        changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, sourceField.Name, id, sourceValue, targetValue, MigrationChangeKind.Incompatible,
                            $"目标字段无法接受源值：{ex.Message}"));
                        continue;
                    }
                    var reason = mappedItemValues.ContainsKey(sourceField.Name) ? ItemMigrationMapper.Reason :
                        createdTables.Contains(key) ? "目标物理文件不存在；需要同时选择对应的新建文件项，按目标 Codec 写入逻辑值" :
                        source.ProfileMatch?.ProfileId == target.ProfileMatch?.ProfileId && source.ProfileMatch?.VariantId == target.ProfileMatch?.VariantId
                        ? null
                        : "跨 Profile/Variant 的逻辑字段映射";
                    changes.Add(new MigrationChange(key, sourceDocument.Definition.DisplayName, sourceField.Name, id, sourceValue, targetValue, MigrationChangeKind.Changed, reason));
                }
            }
        }

        // A selected item type and its person reference form one final draft.
        // Preview with the compatible proposed item fields, then Apply checks
        // again with only the user's actual selection before writing.
        var pendingFields = changes.Where(change => change.Kind is MigrationChangeKind.Changed or MigrationChangeKind.Added)
            .Where(change => targetDocuments.ContainsKey(change.TableKey))
            .Select(change => new CompositeCellEdit(change.TableKey, change.Id - targetDocuments[change.TableKey].Definition.BeginId,
                change.FieldName, change.SourceValue)).ToArray();
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            if (change.TableKey != "persons" || change.Kind is not (MigrationChangeKind.Changed or MigrationChangeKind.Added) ||
                !PersonEquipmentService.CategoryForField(change.FieldName).HasValue) continue;
            try
            {
                var document = targetDocuments[change.TableKey];
                target.ValidateCellValue(document, change.Id - document.Definition.BeginId, change.FieldName, change.SourceValue, pendingFields);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FormatException or OverflowException or InvalidOperationException)
            {
                changes[index] = change with { Kind = MigrationChangeKind.Incompatible, Reason = "目标默认装备无法接受源值：" + ex.Message };
            }
        }
        SpecialStructureMigrationDiagnostics.Add(source, target, changes);
        ExtendedItemMigrationMapper.Add(source, target, changes);

        var physicalFiles = source.Definitions.Concat(target.Definitions).Select(definition => definition.FileName)
            .Concat(ProfileDetector.CoreFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddResourceChanges(sourceRoot, targetRoot, physicalFiles, changes, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("校验文件指纹", "保存源与目标快照"));

        return new MigrationPlan
        {
            SourceRoot = Path.GetFullPath(sourceRoot),
            TargetRoot = Path.GetFullPath(targetRoot),
            SourceProfileId = sourceReport.ProfileMatch?.ProfileId ?? source.ActiveProfile.ProfileId,
            SourceVariantId = sourceReport.ProfileMatch?.VariantId ?? source.ActiveProfile.VariantId,
            TargetProfileId = targetReport.ProfileMatch?.ProfileId ?? target.ActiveProfile.ProfileId,
            TargetVariantId = targetReport.ProfileMatch?.VariantId ?? target.ActiveProfile.VariantId,
            Changes = changes,
            PhysicalFileCreations = fileCreations,
            SourceCoreFileFingerprints = CapturePhysicalFileFingerprints(sourceRoot, physicalFiles),
            TargetCoreFileFingerprints = CapturePhysicalFileFingerprints(targetRoot, physicalFiles),
            SourceEvidenceFingerprints = CaptureEvidenceFingerprints(sourceRoot),
            TargetEvidenceFingerprints = CaptureEvidenceFingerprints(targetRoot)
        };
    }

    public MigrationApplyResult Apply(MigrationPlan plan, IReadOnlyDictionary<int, MigrationChoice>? choices = null, bool includeAllWritable = false,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
        => MigrationCommitCoordinator.Apply(plan, choices, includeAllWritable,
            (staged, selected, all) => ApplyToTemporaryProject(staged, selected, all, progress, cancellationToken), progress, cancellationToken,
            _resourceConverter as IMigrationResourceVerifier);

    private MigrationApplyResult ApplyToTemporaryProject(MigrationPlan plan, IReadOnlyDictionary<int, MigrationChoice>? choices, bool includeAllWritable,
        IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(plan.TargetRoot)) return new MigrationApplyResult(false, 0, 0, null, ["目标目录不存在。"]);
        if (Path.GetFullPath(plan.SourceRoot).Equals(Path.GetFullPath(plan.TargetRoot), StringComparison.OrdinalIgnoreCase))
            return new MigrationApplyResult(false, 0, 0, null, ["源目录和目标目录不能相同。"]);

        var sourceFingerprintError = VerifyCoreFileFingerprints(plan.SourceRoot, plan.SourceCoreFileFingerprints);
        if (sourceFingerprintError != null)
            return new MigrationApplyResult(false, 0, 0, null, [$"迁移源在预览后发生变化：{sourceFingerprintError}", "源目录未修改。"]);
        var targetFingerprintError = VerifyCoreFileFingerprints(plan.TargetRoot, plan.TargetCoreFileFingerprints);
        if (targetFingerprintError != null)
            return new MigrationApplyResult(false, 0, 0, null, [$"迁移目标在预览后发生变化：{targetFingerprintError}", "请重新生成迁移预览。"]);

        // This method only builds a disposable staging project. The coordinator
        // owns the real target backup/rollback; a second full resource backup in
        // staging doubled disk use without protecting any additional original.
        var target = new ProjectWorkspace();
        try
        {
            target.Open(plan.TargetRoot, plan.TargetProfileId, plan.TargetVariantId);
            var rowsByTable = target.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Data.Rows.Cast<DataRow>()
                .Select((row, index) => (row, index)).ToDictionary(item => Convert.ToInt32(item.row["ID"], CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase);
            var fieldEdits = new List<CompositeCellEdit>();
            var applied = 0;
            var skipped = 0;
            var physicalApplied = false;
            var nativeChanges = new List<(MigrationChange Change, MigrationChoice Choice)>();
            foreach (var (change, index) in plan.Changes.Select((value, index) => (value, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index % 512 == 0) progress?.Report(new("生成迁移候选", change.TableDisplayName, index, plan.Changes.Count));
                if (change.Kind == MigrationChangeKind.PhysicalFileAdded)
                {
                    var creationChoice = includeAllWritable ? MigrationChoice.Overwrite
                        : choices != null && choices.TryGetValue(index, out var fileChoice) ? fileChoice : MigrationChoice.Skip;
                    if (creationChoice != MigrationChoice.Skip && File.Exists(Path.Combine(plan.TargetRoot, change.TableDisplayName))) applied++;
                    else skipped++;
                    continue;
                }
                if (change.Kind is MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged)
                {
                    var resourceChoice = includeAllWritable
                        ? MigrationChoice.Overwrite
                        : choices != null && choices.TryGetValue(index, out var selectedResource) ? selectedResource : MigrationChoice.Skip;
                    if (resourceChoice == MigrationChoice.Skip) { skipped++; continue; }
                    var relative = change.TableDisplayName;
                    var sourcePath = Path.Combine(plan.SourceRoot, relative);
                    var targetPath = Path.Combine(plan.TargetRoot, change.TargetRelativePath ?? relative);
                    if (!File.Exists(sourcePath)) { skipped++; continue; }
                    if (resourceChoice == MigrationChoice.Merge && File.Exists(targetPath))
                    {
                        // Resources have no safe byte-wise merge. Preserve an
                        // existing target resource and use Merge as
                        // "fill only missing" semantics.
                        skipped++;
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    var tempPath = targetPath + ".data管理器迁移." + Guid.NewGuid().ToString("N") + ".tmp";
                    if (!Path.GetExtension(sourcePath).Equals(Path.GetExtension(targetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        if (_resourceConverter?.CanConvert(relative, change.TargetRelativePath ?? relative) != true)
                            throw new InvalidOperationException($"没有可用的资源转换器：{relative} → {change.TargetRelativePath}。");
                        var converted = _resourceConverter.Convert(sourcePath, targetPath, progress, cancellationToken);
                        File.WriteAllBytes(tempPath, converted);
                    }
                    else File.Copy(sourcePath, tempPath, true);
                    MakeWritable(tempPath);
                    MakeWritable(targetPath);
                    File.Move(tempPath, targetPath, true);
                    applied++;
                    continue;
                }
                if (change.Kind is not (MigrationChangeKind.Changed or MigrationChangeKind.Added or MigrationChangeKind.NativeStructureChanged))
                {
                    skipped++;
                    continue;
                }
                var choice = includeAllWritable
                    ? MigrationChoice.Overwrite
                    : choices != null && choices.TryGetValue(index, out var selected) ? selected : MigrationChoice.Skip;
                if (choice == MigrationChoice.Skip)
                {
                    skipped++;
                    continue;
                }
                if (change.Kind == MigrationChangeKind.NativeStructureChanged)
                {
                    nativeChanges.Add((change, choice));
                    continue;
                }
                if (!target.Documents.TryGetValue(change.TableKey, out var document) || !document.Data.Columns.Contains(change.FieldName))
                {
                    skipped++;
                    continue;
                }
                if (!rowsByTable[change.TableKey].TryGetValue(change.Id, out var match))
                {
                    skipped++;
                    continue;
                }
                var row = match.row;
                var rowIndex = match.index;
                if (choice == MigrationChoice.Merge)
                {
                    var currentValue = row[change.FieldName] == DBNull.Value ? null : row[change.FieldName];
                    if (!IsEmptyValue(currentValue))
                    {
                        // Scalar physical fields have no safe concatenation
                        // operation. Merge therefore fills only an empty target
                        // cell and preserves an existing target value.
                        skipped++;
                        continue;
                    }
                }
                target.ValidateCellValue(document, rowIndex, change.FieldName, change.SourceValue, validateEquipment: false);
                var targetField = TableFieldIndex.Find(document.Definition, change.FieldName)
                    ?? throw new InvalidDataException($"目标字段不存在：{change.TableKey}/{change.FieldName}。");
                var alias = MigrationFieldIsolation.FindConflict(target, document, rowIndex, targetField, change.SourceValue);
                if (alias != null) throw new InvalidOperationException(alias);
                fieldEdits.Add(new CompositeCellEdit(change.TableKey, rowIndex, change.FieldName, change.SourceValue));
                applied++;
                physicalApplied = true;
            }
            if (applied == 0 && nativeChanges.Count == 0) return new MigrationApplyResult(true, 0, skipped, null, ["没有选择需要写入的迁移项。"]);
            cancellationToken.ThrowIfCancellationRequested();
            target.ApplyMigrationDraft(fieldEdits);
            progress?.Report(new("写入临时项目", $"{fieldEdits.Count} 个逻辑字段"));
            var save = physicalApplied ? target.SaveMigration() : new SaveResult(true, 0, 0, 0, null, ["已准备所选资源及新建物理文件。"]);
            if (!save.Saved)
            {
                var messages = save.Messages.ToList();
                messages.Add("临时候选未通过保存校验，真实目标尚未提交。");
                messages.Add("源目录未修改。");
                return new MigrationApplyResult(false, 0, skipped, null, messages);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("写入专用结构", $"{nativeChanges.Count} 个完整宝物记录/辅助分组变更"));
            var nativeApplied = SpecialStructureMigrationDiagnostics.ApplyMany(target, nativeChanges);
            applied += nativeApplied;
            skipped += nativeChanges.Count - nativeApplied;
            return new MigrationApplyResult(true, applied, skipped, null, nativeApplied == 0 ? save.Messages :
                [.. save.Messages, $"已按目标专用结构写入并复读 {nativeApplied} 项完整宝物记录/辅助分组；保留目标未迁移字节。"]);
        }
        catch (Exception ex)
        {
            return new MigrationApplyResult(false, 0, 0, null,
                [ex is OperationCanceledException ? "迁移已取消。" : $"迁移失败：{ex.Message}", "临时候选已放弃，真实目标尚未提交。", "源目录未修改。"]);
        }
    }

    private static Dictionary<string, string> CapturePhysicalFileFingerprints(string rootPath, IEnumerable<string> physicalFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in physicalFiles)
        {
            var path = ResolvePhysicalSource(rootPath, name);
            MigrationPathSafety.RequireNoLinks(path);
            // Empty means the file was absent. Its later appearance also invalidates the preview.
            result[Path.GetRelativePath(rootPath, path)] = File.Exists(path) ? HashService.Sha256(path) : string.Empty;
        }
        return result;
    }

    private static Dictionary<string, string> CaptureEvidenceFingerprints(string root)
        => EvidenceConfigurations.ToDictionary(relative => relative.Replace('/', Path.DirectorySeparatorChar), relative =>
        {
            var path = MigrationPathSafety.Resolve(root, relative);
            return File.Exists(path) ? HashService.Sha256(path) : string.Empty;
        }, StringComparer.OrdinalIgnoreCase);

    private static bool IsRootExtensionFile(string name)
        => name.Equals("Item.e5", StringComparison.OrdinalIgnoreCase) || name.Equals("Itemmsg.e5", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePhysicalSource(string root, string name)
        => IsRootExtensionFile(name)
            ? Directory.EnumerateFiles(root).FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Path.Combine(root, name)
            : ProjectFileLocator.Resolve(root, name);

    private static bool HasRootExtensionGeometry(string path)
        => File.Exists(path) && new FileInfo(path).Length == (Path.GetFileName(path).Equals("Item.e5", StringComparison.OrdinalIgnoreCase) ? 256 * 25 : 256 * 200);

    private static string? VerifyCoreFileFingerprints(string rootPath, IReadOnlyDictionary<string, string> expected)
    {
        if (expected.Count == 0) return null;
        foreach (var pair in expected)
        {
            var path = Path.Combine(rootPath, pair.Key);
            if (pair.Value.Length == 0)
            {
                if (File.Exists(path)) return $"{pair.Key} 在预览后新建";
                continue;
            }
            if (!File.Exists(path)) return $"缺少 {pair.Key}";
            var current = HashService.Sha256(path);
            if (!current.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) return $"{pair.Key} SHA-256 已变化";
        }
        return null;
    }

    private static bool ValuesEqual(object? left, object? right)
        => string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool IsEmptyValue(object? value)
        => value is null or DBNull || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture));

    private static void MakeWritable(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static IReadOnlyList<MigrationPhysicalFileCreation> AddCreatablePhysicalDocuments(ProjectWorkspace source,
        ProjectWorkspace target, Dictionary<string, TableDocument> documents, List<MigrationChange> changes)
    {
        var result = new List<MigrationPhysicalFileCreation>();
        var previewRoot = Path.Combine(Path.GetTempPath(), "DataManagerMigrationPreview_" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var group in target.Definitions.GroupBy(definition => definition.FileName, StringComparer.OrdinalIgnoreCase))
            {
                if (!CreatablePhysicalFiles.TryGetValue(group.Key, out var expectedLength)) continue;
                if (File.Exists(ProjectFileLocator.Resolve(target.RootPath, group.Key))) continue;
                var definitions = group.ToArray();
                // New files use the DLL's full physical capacity even when the
                // source only contains the 255 editable legacy rows.
                if (group.Key.Equals("Effectmsg.e5", StringComparison.OrdinalIgnoreCase) &&
                    definitions.Length == 1 && definitions[0].RowSize == 8)
                {
                    expectedLength = 256 * 8;
                    definitions = [definitions[0] with { RowCount = 256 }];
                }
                if (!definitions.Any(definition => source.Documents.ContainsKey(definition.Key))) continue;
                if (definitions.Any(definition => definition.Capability != TableCapability.Editable || definition.DataOffset < 0 ||
                    definition.RowSize <= 0 || definition.RowCount <= 0 || definition.EndOffsetExclusive > expectedLength) ||
                    definitions.Max(definition => definition.EndOffsetExclusive) != expectedLength) continue;
                Directory.CreateDirectory(previewRoot);
                File.WriteAllBytes(Path.Combine(previewRoot, group.Key), new byte[expectedLength]);
                var reader = new BinaryTableReader();
                foreach (var definition in definitions) documents[definition.Key] = reader.Read(previewRoot, definition);
                var relative = Path.GetRelativePath(target.RootPath, ProjectFileLocator.Resolve(target.RootPath, group.Key));
                result.Add(new MigrationPhysicalFileCreation(relative, expectedLength, definitions.Select(definition => definition.Key).ToArray()));
                changes.Add(new MigrationChange("__physical_file__", relative, string.Empty, -1, expectedLength, null,
                    MigrationChangeKind.PhysicalFileAdded, "使用目标 Profile 的已知几何创建零初始化文件，随后通过目标 Codec 写入所选逻辑字段；未拆字节保持目标默认零值"));
            }
        }
        finally { if (Directory.Exists(previewRoot)) Directory.Delete(previewRoot, true); }
        return result;
    }

    private void AddResourceChanges(string sourceRoot, string targetRoot, IReadOnlySet<string> physicalFiles, List<MigrationChange> changes,
        IProgress<MigrationProgress>? progress, CancellationToken cancellationToken)
    {
        var sourceFiles = EnumerateResourceFiles(sourceRoot, physicalFiles).ToArray();
        var targetFiles = EnumerateResourceFiles(targetRoot, physicalFiles).ToArray();
        var matchedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceIndex = 0;
        foreach (var relative in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("比较资源文件", relative, resourceIndex++, sourceFiles.Length));
            if (IsArchiveStoragePath(relative))
            {
                var name = Path.GetFileName(relative);
                var sourceAliases = sourceFiles.Where(path => IsArchiveStoragePath(path) && Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var targetAliases = targetFiles.Where(path => IsArchiveStoragePath(path) && Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (sourceAliases.Length > 1 || targetAliases.Length > 1)
                {
                    foreach (var alias in targetAliases) matchedTargets.Add(alias);
                    changes.Add(new MigrationChange("__resource__", relative, string.Empty, -1, null, null, MigrationChangeKind.Incompatible,
                        $"资源在根目录/E5 存在多个同名副本，无法唯一确定生效位置；源：{string.Join("、", sourceAliases)}；目标：{string.Join("、", targetAliases)}。请先消除同名歧义再迁移。"));
                    continue;
                }
            }
            var targetRelative = targetFiles.FirstOrDefault(path => path.Equals(relative, StringComparison.OrdinalIgnoreCase));
            // Both versions may place the same archive in the project root or E5.
            // Preserve the target's established location rather than creating a shadow copy.
            targetRelative ??= IsArchiveStoragePath(relative)
                ? targetFiles.FirstOrDefault(path => !matchedTargets.Contains(path) && IsArchiveStoragePath(path) &&
                    Path.GetFileName(path).Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase))
                : null;
            if (targetRelative == null && IsArchiveStoragePath(relative) && AlternativeImageStorage(Path.GetFileName(relative)) is { } alternative)
            {
                var targetAlternative = targetFiles.FirstOrDefault(path => IsArchiveStoragePath(path) &&
                    Path.GetFileName(path).Equals(alternative, StringComparison.OrdinalIgnoreCase));
                if (targetAlternative != null)
                {
                    matchedTargets.Add(targetAlternative);
                    var convertible = _resourceConverter?.CanConvert(relative, targetAlternative) == true;
                    changes.Add(new MigrationChange("__resource__", relative, string.Empty, -1,
                        HashService.Sha256(Path.Combine(sourceRoot, relative)), HashService.Sha256(Path.Combine(targetRoot, targetAlternative)),
                        convertible ? MigrationChangeKind.ResourceChanged : MigrationChangeKind.Incompatible,
                        convertible ? $"通过图像适配器转换 {Path.GetFileName(relative)} → {alternative}，保留逻辑编号并全量复读图像；保留目标资源位置"
                            : $"源图标存储 {Path.GetFileName(relative)} 与目标 {alternative} 格式不同，当前不提供 DLL ↔ E5 图像转换，不能通过复制生效")
                        { TargetRelativePath = targetAlternative });
                    continue;
                }
            }
            targetRelative ??= relative;
            matchedTargets.Add(targetRelative);
            var source = MigrationPathSafety.Resolve(sourceRoot, relative);
            var target = MigrationPathSafety.Resolve(targetRoot, targetRelative);
            var targetExists = File.Exists(target);
            var sourceHash = HashService.Sha256(source);
            var targetHash = targetExists ? HashService.Sha256(target) : null;
            if (string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetExtension(relative).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(relative).Equals(".dll", StringComparison.OrdinalIgnoreCase) && !IsKnownImageLibrary(Path.GetFileName(relative)))
            {
                changes.Add(new MigrationChange("__resource__", relative, string.Empty, -1, sourceHash, targetHash,
                    MigrationChangeKind.Incompatible,
                    "此文件包含程序/插件代码，不是已验证的图片资源库。数据迁移保留目标引擎；直接复制不同基底的插件不能证明接口、地址或加载条件兼容。")
                { TargetRelativePath = targetRelative });
                continue;
            }
            changes.Add(new MigrationChange("__resource__", relative, string.Empty, -1, sourceHash, targetHash,
                targetExists ? MigrationChangeKind.ResourceChanged : MigrationChangeKind.ResourceAdded,
                relative.Equals(targetRelative, StringComparison.OrdinalIgnoreCase)
                    ? "按相对路径迁移资源；不改写逻辑编号"
                    : $"保留目标资源位置：{relative} → {targetRelative}；不改写逻辑编号") { TargetRelativePath = targetRelative });
        }
        foreach (var relative in targetFiles.Where(path => !matchedTargets.Contains(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            changes.Add(new MigrationChange("__resource__", relative, string.Empty, -1, null,
                HashService.Sha256(Path.Combine(targetRoot, relative)), MigrationChangeKind.ResourceMissingSource,
                "源目录没有该资源，目标保持原状") { TargetRelativePath = relative });
        }
    }

    private static bool IsArchiveStoragePath(string relative)
        => string.IsNullOrEmpty(Path.GetDirectoryName(relative)) ||
           string.Equals(Path.GetDirectoryName(relative), "E5", StringComparison.OrdinalIgnoreCase);

    private static string? AlternativeImageStorage(string fileName)
        => fileName.ToLowerInvariant() switch
        {
            "itemicon.dll" => "Item.e5",
            "item.e5" => "Itemicon.dll",
            "mgcicon.dll" => "Mtem.e5",
            "mtem.e5" => "Mgcicon.dll",
            _ => null
        };

    private static bool IsKnownImageLibrary(string name)
        => name.Equals("Itemicon.dll", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Mgcicon.dll", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Tou.dll", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Cmdicon.dll", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Mapatr.dll", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateResourceFiles(string root, IReadOnlySet<string> physicalFiles)
    {
        if (!Directory.Exists(root)) yield break;
        // A table definition stores only a basename (for example Data.e5).
        // Comparing that basename against every resource would incorrectly
        // hide an image archive with the same name under E5. Resolve each
        // physical table exactly as the workspace does and exclude only that
        // concrete file. This keeps root/E5 image archives discoverable while
        // still preventing physical tables from being copied as resources.
        var physicalPaths = physicalFiles
            .Select(file => Path.GetFullPath(ResolvePhysicalSource(root, file)))
            .Where(File.Exists)
            // The optional root extension tables share names with image archives.
            // Match the reader's root-only, exact-length contract; an absent or
            // geometrically invalid extension must not hide an icon archive.
            .Where(path => !IsRootExtensionFile(Path.GetFileName(path)) || HasRootExtensionGeometry(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                // Never migrate tool history, backups, Git data, or follow junctions outside the project.
                if (Path.GetFileName(child).StartsWith(".", StringComparison.Ordinal) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                directories.Push(child);
            }
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var relative = Path.GetRelativePath(root, path);
                if (physicalPaths.Contains(Path.GetFullPath(path))) continue;
                // The extension DLL's Item.e5 contains 256×25 data records.
                // It is unrelated to E5/Item.e5, an indexed image archive.
                // These root data files require an engine-aware mapper, never filename aliasing.
                if (string.IsNullOrEmpty(Path.GetDirectoryName(relative)) && IsRootExtensionFile(Path.GetFileName(relative)) && HasRootExtensionGeometry(path)) continue;
                if (ResourceExtensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).Equals("SETTINGE5", StringComparison.OrdinalIgnoreCase)) yield return relative;
            }
        }
    }

    internal static void ValidateRoots(string sourceRoot, string targetRoot)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
        MigrationPathSafety.RequireNoLinks(source);
        MigrationPathSafety.RequireNoLinks(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("源目录与目标目录必须独立，不能相同或互相包含。");
    }

    public static string ResolveProjectRoot(string selectedRoot)
    {
        var root = Path.GetFullPath(selectedRoot);
        bool IsProject(string directory) => CoreFiles.All(name => Directory.EnumerateFiles(directory)
            .Any(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        MigrationPathSafety.RequireNoLinks(root);
        if (IsProject(root)) return root;
        var candidates = Directory.EnumerateDirectories(root).Where(directory =>
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 && !Path.GetFileName(directory).StartsWith('.'))
            .Where(IsProject).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new InvalidOperationException(candidates.Length == 0
            ? "所选目录及其下一层没有包含 Data.e5、Star.e5、Imsg.e5、Ekd5.exe 的完整游戏目录。"
            : "所选目录包含多个游戏基底，请直接选择实际游戏目录。");
    }
}
