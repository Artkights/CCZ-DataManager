using System.Data;
using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.History;
using DataManager.Core.Models;
using DataManager.Core.Profiles;
using DataManager.Core.Scanner;
using DataManager.Core.Appearance;

namespace DataManager.Core.Editing;

public sealed class ProjectWorkspace
{
    private readonly RecordCodecRegistry _codecs = new();
    private readonly BinaryTableReader _reader;
    private readonly BinaryTableWriter _writer = new();
    private readonly HistoryStore _history = new();
    private readonly CsvService _csv = new();
    private readonly Dictionary<string, TableDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TableDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleDefinition> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProfileRegistry _profiles = new();
    private readonly ProfileDetector _detector;
    private bool _recordingCellEdit;
    private readonly List<IWorkspaceDraftParticipant> _draftParticipants = [];

    public ProjectWorkspace()
    {
        _reader = new BinaryTableReader(_codecs);
        _detector = new ProfileDetector(_profiles);
        ActiveProfile = _profiles.Resolve("ccz-6.5", "65-standard-v1");
        ApplyResolvedProfile(ActiveProfile);
        Commands = new EditCommandManager();
    }

    public string RootPath { get; private set; } = string.Empty;
    public EditCommandManager Commands { get; }
    public IReadOnlyDictionary<string, TableDocument> Documents => _documents;
    public IReadOnlyList<TableDefinition> Definitions => _definitions.Values.ToArray();
    public IReadOnlyList<ModuleDefinition> Modules => _modules.Values.ToArray();
    public ScanReport? LastScan { get; private set; }
    public ProfileMatchResult? ProfileMatch { get; private set; }
    public ResolvedProfile ActiveProfile { get; private set; }
    public IReadOnlyList<DataProfile> AvailableProfiles => _profiles.Profiles;
    public bool IsDirty => _documents.Values.Any(document => document.IsDirty) || _draftParticipants.Any(draft => draft.HasPendingChanges);
    public HistoryStore History => _history;

    public void RegisterDraftParticipant(IWorkspaceDraftParticipant participant)
    {
        if (!_draftParticipants.Contains(participant)) _draftParticipants.Add(participant);
    }

    public void DiscardNativeDrafts()
    {
        foreach (var draft in _draftParticipants.ToArray()) draft.Discard();
    }

    public ScanReport Open(string rootPath, string? profileId = null, string? variantId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
        _draftParticipants.Clear();
        RootPath = ProjectRootLocator.Resolve(rootPath);
        _documents.Clear();
        var detected = _detector.Detect(RootPath);
        cancellationToken.ThrowIfCancellationRequested();
        ProfileMatch = string.IsNullOrWhiteSpace(profileId)
            ? detected
            : _detector.Force(detected, profileId, variantId ?? _profiles.GetProfile(profileId).DefaultVariantId);
        ActiveProfile = _profiles.Resolve(ProfileMatch.ProfileId, ProfileMatch.VariantId);
        ApplyResolvedProfile(ActiveProfile);
        var scanner = new BaseScanner(_profiles);
        LastScan = scanner.Scan(RootPath, ActiveProfile, ProfileMatch);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _documents[definition.Key] = ReadSharedSource(definition, sourceFiles);
            }
            catch (Exception ex)
            {
                // Keep the rest of the project editable when an auxiliary table is missing.
                LastScan.Diagnostics.Add($"{definition.DisplayName} 未加载：{ex.Message}");
            }
        }
        AppendTextDecodeDiagnostics(LastScan);
        AddParentReferenceNames();
        AddDerivedItemFields();
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Clear();
        return LastScan;
    }

    public ScanReport Rescan()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) throw new InvalidOperationException("尚未打开项目。");
        var detected = _detector.Detect(RootPath);
        ProfileMatch = ProfileMatch?.IsManualOverride == true
            ? _detector.Force(detected, ActiveProfile.ProfileId, ActiveProfile.VariantId)
            : detected;
        if (!ProfileMatch.IsManualOverride &&
            (!ProfileMatch.ProfileId.Equals(ActiveProfile.ProfileId, StringComparison.OrdinalIgnoreCase) ||
             !ProfileMatch.VariantId.Equals(ActiveProfile.VariantId, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveProfile = _profiles.Resolve(ProfileMatch.ProfileId, ProfileMatch.VariantId);
            ApplyResolvedProfile(ActiveProfile);
            ReloadAllDocuments();
        }
        LastScan = new BaseScanner(_profiles).Scan(RootPath, ActiveProfile, ProfileMatch);
        AppendTextDecodeDiagnostics(LastScan);
        return LastScan;
    }

    private void AppendTextDecodeDiagnostics(ScanReport report)
    {
        foreach (var document in _documents.Values)
            if (document.Data.ExtendedProperties["TextDecodeWarnings"] is IEnumerable<string> warnings)
                report.Diagnostics.AddRange(warnings);
    }

    private void ApplyResolvedProfile(ResolvedProfile resolved)
    {
        resolved = new ProfileAugmentationService().Apply(RootPath, resolved);
        ActiveProfile = resolved;
        _definitions.Clear();
        foreach (var definition in resolved.Tables) _definitions.Add(definition.Key, definition);
        _modules.Clear();
        foreach (var module in resolved.Modules) _modules.Add(module.Key, module);
    }

    public TableDocument GetDocument(string tableKey)
        => _documents.TryGetValue(tableKey, out var document)
            ? document
            : throw new InvalidOperationException($"表未加载：{tableKey}");

    public IReadOnlyList<HistoryEntry> GetHistory() => _history.Load(RootPath);

    public void RecordCellEdit(TableDocument document, int rowIndex, string columnName, object? oldValue, object? newValue)
    {
        if (Commands.IsApplying || _recordingCellEdit || ValuesEqual(oldValue, newValue)) return;
        var field = document.Definition.Fields.FirstOrDefault(candidate => candidate.Name == columnName);
        if (field?.Policy != FieldPolicy.Editable) return;
        // A bound grid has already assigned its value before this callback.
        // Return to the prior draft first, then use the same protected operation
        // as business-page edits. Nested binding notifications are ignored.
        _recordingCellEdit = true;
        try
        {
            document.Data.Rows[rowIndex][columnName] = oldValue ?? DBNull.Value;
            ExecuteDraftOperation([document], () => document.Data.Rows[rowIndex][columnName] = newValue ?? DBNull.Value);
        }
        finally { _recordingCellEdit = false; }
        Commands.Push(new DelegateEditCommand(
            $"{document.Definition.DisplayName} ID={document.Definition.BeginId + rowIndex} {columnName}",
            () =>
            {
                ExecuteDraftOperation([document], () => document.Data.Rows[rowIndex][columnName] = oldValue ?? DBNull.Value);
            },
            () =>
            {
                ExecuteDraftOperation([document], () => document.Data.Rows[rowIndex][columnName] = newValue ?? DBNull.Value);
            }));
    }

    public void ValidateCellValue(TableDocument document, int rowIndex, string columnName, object? value,
        IReadOnlyList<CompositeCellEdit>? pendingEdits = null, bool validateEquipment = true)
    {
        if (document.Definition.Capability != TableCapability.Editable)
        {
            var detail = string.IsNullOrWhiteSpace(document.Definition.CapabilityEvidence)
                ? "当前布局未提供可靠写入证据"
                : document.Definition.CapabilityEvidence;
            throw new InvalidOperationException($"表不可写：{document.Definition.DisplayName}（{detail}）。");
        }
        var field = TableFieldIndex.Find(document.Definition, columnName)
            ?? throw new InvalidOperationException($"字段不存在：{columnName}");
        if (field.Policy != FieldPolicy.Editable) throw new InvalidOperationException($"字段不可编辑：{columnName}");
        if (rowIndex < 0 || rowIndex >= document.Data.Rows.Count) throw new ArgumentOutOfRangeException(nameof(rowIndex));
        if (document.Definition.Key == "effect-messages" && document.Definition.BeginId + rowIndex == 255 &&
            !ValuesEqual(document.GetOriginal(rowIndex, field.Name), value))
            throw new InvalidOperationException("特效成长 ID=255 为保留槽，只允许原值回导和无损保存。");
        var rowOffset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        var row = document.OriginalFileBytes.AsSpan(rowOffset, document.Definition.RowSize);
        try
        {
            _ = _codecs.Get(document.Definition.RecordCodecKey).EncodeChangedField(row, field, value);
            if (document.Definition.Key == "shops" && field.ReferenceTable == "items" && !ValuesEqual(document.GetOriginal(rowIndex, field.Name), value))
                ShopValueValidator.ValidateItem(this, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            if (validateEquipment && document.Definition.Key == "persons" && PersonEquipmentService.CategoryForField(field.Name).HasValue &&
                !ValuesEqual(document.GetOriginal(rowIndex, field.Name), value))
                PersonEquipmentService.Validate(this, field.Name, Convert.ToInt32(value, CultureInfo.InvariantCulture), pendingEdits);
            if (document.Definition.Key == EquipmentTypeCatalogService.DisplayTableKey &&
                !ValuesEqual(document.GetOriginal(rowIndex, field.Name), value) &&
                Convert.ToInt32(value, CultureInfo.InvariantCulture) is var displayValue &&
                displayValue != EquipmentTypeCatalogService.HiddenValue && displayValue != EquipmentTypeCatalogService.VisibleValues[rowIndex])
                throw new InvalidOperationException("装备类型显示只能选择正常显示值或 255（隐藏）。");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or OverflowException)
        {
            var context = $"{document.Definition.DisplayName} ID={document.Definition.BeginId + rowIndex} 字段“{field.Name}”（槽偏移 0x{rowOffset + field.Offset:X}）：{ex.Message}";
            if (ex is ArgumentOutOfRangeException) throw new ArgumentOutOfRangeException(columnName, value, context);
            if (ex is ArgumentException) throw new ArgumentException(context, columnName, ex);
            throw new InvalidDataException(context, ex);
        }
    }

    public void ApplyBatch(TableDocument document, IReadOnlyList<int> rows, string columnName, string operation, string input)
        => ApplyBatchMultiple(document, rows, [columnName], operation, input);

    public void ApplyBatchMultiple(TableDocument document, IReadOnlyList<int> rows, IReadOnlyList<string> columnNames, string operation, string input)
    {
        if (rows.Count == 0) throw new InvalidOperationException("没有选中可修改的行。");
        if (columnNames.Count == 0) throw new InvalidOperationException("没有选中可修改的字段。");
        var before = TableSnapshot.Capture(document.Data);
        var proposals = new List<(int Row, string Column, object Value)>();
        foreach (var columnName in columnNames.Distinct(StringComparer.Ordinal))
        {
            var column = document.Data.Columns[columnName];
            if (column == null) throw new InvalidOperationException($"字段不存在：{columnName}");
            if (column.ReadOnly) throw new InvalidOperationException($"字段只读：{columnName}");
            var field = column.ExtendedProperties["Field"] as TableFieldDefinition;
            var source = document.Data.Rows[rows[0]][columnName];
            foreach (var rowIndex in rows.Distinct())
            {
                if (rowIndex < 0 || rowIndex >= document.Data.Rows.Count) throw new ArgumentOutOfRangeException(nameof(rows), rowIndex, "选中行超出表范围。");
                var row = document.Data.Rows[rowIndex];
                var old = row[columnName] == DBNull.Value ? null : row[columnName];
                var value = operation == "将首行复制到选中行"
                    ? source
                    : ComputeBatchValue(old, column.DataType, field, operation, input);
                ValidateCellValue(document, rowIndex, columnName, value);
                proposals.Add((rowIndex, columnName, value));
            }
        }
        ExecuteDraftOperation([document], () =>
        {
            foreach (var proposal in proposals) document.Data.Rows[proposal.Row][proposal.Column] = proposal.Value;
        });
        if (!IsDifferent(document.Data, before)) return;
        var after = TableSnapshot.Capture(document.Data);
        Commands.Push(new DelegateEditCommand(
            $"批量修改 {document.Definition.DisplayName}（{rows.Count} 行、{columnNames.Count} 字段）",
            () =>
            {
                RestoreDraftSnapshots([document], new Dictionary<string, TableSnapshot> { [document.Definition.Key] = before });
            },
            () =>
            {
                RestoreDraftSnapshots([document], new Dictionary<string, TableSnapshot> { [document.Definition.Key] = after });
            }));
    }

    public int ApplyFindReplace(TableDocument document, IReadOnlyList<int> rows, string columnName, string find, string replace)
    {
        if (string.IsNullOrEmpty(find)) throw new ArgumentException("查找内容不能为空。", nameof(find));
        if (!document.Data.Columns.Contains(columnName)) throw new InvalidOperationException($"字段不存在：{columnName}");
        var column = document.Data.Columns[columnName]!;
        if (column.ReadOnly) throw new InvalidOperationException($"字段只读：{columnName}");

        var before = TableSnapshot.Capture(document.Data);
        var proposals = new List<(int Row, string Value)>();
        foreach (var rowIndex in rows.Distinct().Where(index => index >= 0 && index < document.Data.Rows.Count))
        {
            var row = document.Data.Rows[rowIndex];
            var oldValue = Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? string.Empty;
            var newValue = oldValue.Replace(find, replace, StringComparison.Ordinal);
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;
            ValidateCellValue(document, rowIndex, columnName, newValue);
            proposals.Add((rowIndex, newValue));
        }

        var changed = proposals.Count;
        if (changed == 0) return 0;
        ExecuteDraftOperation([document], () =>
        {
            foreach (var proposal in proposals) document.Data.Rows[proposal.Row][column] = proposal.Value;
        });
        var after = TableSnapshot.Capture(document.Data);
        Commands.Push(new DelegateEditCommand(
            $"查找替换 {document.Definition.DisplayName} / {columnName}（{changed} 行）",
            () =>
            {
                RestoreDraftSnapshots([document], new Dictionary<string, TableSnapshot> { [document.Definition.Key] = before });
            },
            () =>
            {
                RestoreDraftSnapshots([document], new Dictionary<string, TableSnapshot> { [document.Definition.Key] = after });
            }));
        return changed;
    }

    public CsvImportResult ImportCsv(TableDocument document, string path)
    {
        var before = TableSnapshot.Capture(document.Data);
        CsvImportResult? result = null;
        ExecuteDraftOperation([document], () =>
        {
            result = _csv.Import(document, path, context: CsvContextFor(document));
            foreach (var change in document.EnumerateChanges())
            {
                ValidateCellValue(document, change.RowIndex, change.Field.Name, change.NewValue);
            }
        });
        if (result!.ChangedCells > 0)
        {
            var after = TableSnapshot.Capture(document.Data);
            Commands.Push(new DelegateEditCommand(
                $"导入 CSV：{document.Definition.DisplayName}",
                () =>
                {
                    RestoreDraftSnapshots([document], new Dictionary<string, TableSnapshot> { [document.Definition.Key] = before });
                },
                () =>
                {
                    RestoreDraftSnapshots([document], new Dictionary<string, TableSnapshot> { [document.Definition.Key] = after });
                }));
        }
        return result;
    }

    /// <summary>
    /// Imports a set of physical-table CSV files as one undoable page edit.
    /// All files are parsed and applied to memory first; if one table fails,
    /// every earlier table is restored before the exception is propagated.
    /// </summary>
    public CsvBundleImportResult ImportCsvBundle(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var documents = files.Keys
            .Where(key => _documents.ContainsKey(key))
            .Select(key => _documents[key])
            .Distinct()
            .ToArray();
        if (documents.Length == 0) return new CsvBundleImportResult(new Dictionary<string, CsvImportResult>(), 0);
        var before = documents.ToDictionary(document => document.Definition.Key, document => TableSnapshot.Capture(document.Data), StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, CsvImportResult>(StringComparer.OrdinalIgnoreCase);
        ExecuteDraftOperation(documents, () =>
        {
            foreach (var (key, path) in files)
            {
                if (!_documents.TryGetValue(key, out var document)) throw new InvalidOperationException($"表未加载：{key}");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var result = _csv.Import(document, path, context: CsvContextFor(document));
                // CsvService validates field ranges and types while parsing,
                // but the active record codec may impose additional
                // Variant-specific constraints. Validate the resulting cells
                // before allowing the bundle to become a draft.
                results[key] = result;
            }
            // All tables are present before category-dependent references are
            // checked, regardless of the CSV dictionary's order.
            foreach (var (key, result) in results)
                foreach (var change in result.Changes)
                    ValidateCellValue(_documents[key], change.RowIndex, change.ColumnName, change.NewValue);
        });
        var changed = results.Values.Sum(result => result.ChangedCells);
        if (changed == 0) return new CsvBundleImportResult(results, 0);
        var after = documents.ToDictionary(document => document.Definition.Key, document => TableSnapshot.Capture(document.Data), StringComparer.OrdinalIgnoreCase);
        Commands.Push(new DelegateEditCommand(
            $"导入 CSV：{results.Count} 张物理表",
            () =>
            {
                RestoreDraftSnapshots(documents, before);
            },
            () =>
            {
                RestoreDraftSnapshots(documents, after);
            }));
        return new CsvBundleImportResult(results, changed);
    }

    public void ExportCsv(TableDocument document, string path) => _csv.Export(document, path, CsvContextFor(document));

    private CsvContext CsvContextFor(TableDocument document)
        => new(ActiveProfile.ProfileId, ActiveProfile.VariantId, document.Definition.Key, ActiveProfile.LayoutRevision);

    /// <summary>
    /// Applies edits that span multiple physical tables as one draft command.
    /// All cells are validated before any row is changed; the existing Save
    /// path then performs file-level SHA checks and writes the files together.
    /// </summary>
    public CompositeApplyResult ApplyCompositeTransaction(string description, IReadOnlyList<CompositeCellEdit> edits)
    {
        if (edits == null || edits.Count == 0) return CompositeApplyResult.NoChanges();

        var proposals = new List<(TableDocument Document, int RowIndex, string FieldName, object? OldValue, object? NewValue)>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var duplicateKey = $"{edit.TableKey}\u001f{edit.RowIndex}\u001f{edit.FieldName}";
            if (!keys.Add(duplicateKey))
                throw new InvalidOperationException($"事务包含重复字段：{edit.TableKey}[{edit.RowIndex}].{edit.FieldName}");
            if (!_documents.TryGetValue(edit.TableKey, out var document)) throw new InvalidOperationException($"表未加载：{edit.TableKey}");
            if (edit.RowIndex < 0 || edit.RowIndex >= document.Data.Rows.Count) throw new ArgumentOutOfRangeException(nameof(edits), edit.RowIndex, $"表 {edit.TableKey} 的行号超出范围。");
            var field = document.Definition.Fields.FirstOrDefault(candidate => candidate.Name.Equals(edit.FieldName, StringComparison.Ordinal));
            if (field == null || field.Policy != FieldPolicy.Editable || !document.Data.Columns.Contains(edit.FieldName))
                throw new InvalidOperationException($"字段不可编辑：{edit.TableKey}.{edit.FieldName}");
            ValidateCellValue(document, edit.RowIndex, edit.FieldName, edit.NewValue, edits);
            var oldValue = document.Data.Rows[edit.RowIndex][edit.FieldName] == DBNull.Value ? null : document.Data.Rows[edit.RowIndex][edit.FieldName];
            if (ValuesEqual(oldValue, edit.NewValue)) continue;
            proposals.Add((document, edit.RowIndex, edit.FieldName, oldValue, edit.NewValue));
        }
        if (proposals.Count == 0) return CompositeApplyResult.NoChanges();

        var changedDocuments = proposals.Select(proposal => proposal.Document).Distinct().ToArray();
        var before = changedDocuments.ToDictionary(document => document.Definition.Key, document => TableSnapshot.Capture(document.Data), StringComparer.OrdinalIgnoreCase);
        ExecuteDraftOperation(changedDocuments, () =>
        {
            foreach (var proposal in proposals) proposal.Document.Data.Rows[proposal.RowIndex][proposal.FieldName] = proposal.NewValue ?? DBNull.Value;
        });
        var after = changedDocuments.ToDictionary(document => document.Definition.Key, document => TableSnapshot.Capture(document.Data), StringComparer.OrdinalIgnoreCase);
        Commands.Push(new DelegateEditCommand(
            string.IsNullOrWhiteSpace(description) ? "编辑业务页" : description,
            () =>
            {
                RestoreDraftSnapshots(changedDocuments, before);
            },
            () =>
            {
                RestoreDraftSnapshots(changedDocuments, after);
            }));
        return new CompositeApplyResult(true, proposals.Count, [$"已应用 {proposals.Count} 个跨表字段修改。"]);
    }

    // Migration owns a disposable staging workspace. Save still validates every
    // field, records byte history and verifies SHA; per-cell UI commands and
    // repeated derived summaries have no role in that temporary project.
    internal void ApplyMigrationDraft(IReadOnlyList<CompositeCellEdit> edits)
    {
        foreach (var edit in edits)
        {
            var document = GetDocument(edit.TableKey);
            ValidateCellValue(document, edit.RowIndex, edit.FieldName, edit.NewValue, edits);
        }
        foreach (var edit in edits)
            GetDocument(edit.TableKey).Data.Rows[edit.RowIndex][edit.FieldName] = edit.NewValue ?? DBNull.Value;
    }

    public SaveResult Save() => Save([]);

    public SaveResult Save(IReadOnlyList<PreparedBinaryEdit> extraEdits)
        => Save(extraEdits, []);

    public SaveResult Save(IReadOnlyList<PreparedBinaryEdit> extraEdits, IReadOnlyList<PreparedFileEdit> fileEdits)
    {
        try
        {
            var participants = _draftParticipants.Where(draft => draft.HasPendingChanges).ToArray();
            var binary = extraEdits.Concat(participants.SelectMany(draft => draft.PrepareBinaryEdits())).ToArray();
            var files = fileEdits.Concat(participants.SelectMany(draft => draft.PrepareFileEdits())).ToArray();
            return SaveCore(recordCellHistory: true, binary, files, participants);
        }
        catch (Exception ex) { return new SaveResult(false, 0, 0, 0, null, [$"保存中止：{ex.Message}"]); }
    }

    // A migration can contain hundreds of thousands of cells. Its byte-level
    // snapshots retain complete undo/redo without a redundant JSON object for
    // every scalar cell; ordinary editing keeps detailed field history.
    internal SaveResult SaveMigration() => SaveCore(recordCellHistory: false, []);

    private SaveResult SaveCore(bool recordCellHistory, IReadOnlyList<PreparedBinaryEdit> extraEdits,
        IReadOnlyList<PreparedFileEdit>? fileEdits = null, IReadOnlyList<IWorkspaceDraftParticipant>? participants = null)
    {
        fileEdits ??= [];
        participants ??= [];
        if (string.IsNullOrWhiteSpace(RootPath)) return new SaveResult(false, 0, 0, 0, null, ["尚未打开项目。"]);
        var changesByFile = new Dictionary<string, List<TableCellChange>>(StringComparer.OrdinalIgnoreCase);
        var allChanges = new List<TableCellChange>();
        foreach (var document in _documents.Values)
        {
            if (document.Definition.Key == "forge" && document.IsDirty)
                foreach (var dirtyRow in document.EnumerateChanges().Select(change => change.RowIndex).Distinct())
                    ExtendDataService.ValidateForgeDraft(document, dirtyRow);
            foreach (var change in document.EnumerateChanges())
            {
                try { ValidateCellValue(document, change.RowIndex, change.Field.Name, change.NewValue); }
                catch (Exception ex)
                {
                    return new SaveResult(false, 0, allChanges.Count + 1, 0, null, [$"保存中止：{document.Definition.DisplayName} ID={change.Id} {change.Field.Name}：{ex.Message}"]);
                }
                allChanges.Add(change);
                var path = Path.GetFullPath(document.FilePath);
                if (!changesByFile.TryGetValue(path, out var changes)) changesByFile[path] = changes = [];
                changes.Add(change);
            }
        }
        var extraByFile = new Dictionary<string, List<PreparedBinaryEdit>>(StringComparer.OrdinalIgnoreCase);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath)) + Path.DirectorySeparatorChar;
        foreach (var edit in extraEdits)
        {
            var path = Path.GetFullPath(Path.Combine(RootPath, edit.RelativePath));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("专用补丁超出当前项目目录。");
            if (!extraByFile.TryGetValue(path, out var edits)) extraByFile[path] = edits = [];
            edits.Add(edit);
            changesByFile.TryAdd(path, []);
        }
        var replacements = new Dictionary<string, PreparedFileEdit>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in fileEdits)
        {
            var path = Path.GetFullPath(Path.Combine(RootPath, edit.RelativePath));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("完整文件候选超出当前项目目录。");
            if (!replacements.TryAdd(path, edit))
                throw new InvalidDataException($"同一文件有多个完整候选：{edit.RelativePath}。");
            if (changesByFile.ContainsKey(path))
                throw new InvalidDataException($"完整候选与普通字节修改不能同时覆盖文件：{edit.RelativePath}。");
            changesByFile.Add(path, []);
        }
        if (changesByFile.Count == 0) return new SaveResult(true, 0, 0, 0, null, ["没有未保存修改。"]);
        var recoverDrafts = _documents.Values.Select(document => document.CaptureSaveRecovery())
            .Concat(participants.Select(draft => draft.CaptureSaveRecovery())).ToArray();

        var fileChanges = new List<FileByteChange>();
        var tableHistory = new List<HistoryTableChange>();
        var candidateBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in changesByFile)
        {
            var current = File.ReadAllBytes(group.Key);
            var expectedHashes = _documents.Values.Where(document => string.Equals(Path.GetFullPath(document.FilePath), group.Key, StringComparison.OrdinalIgnoreCase)).Select(document => document.OriginalFileHash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var beforeHash = HashService.Sha256(current);
            if (replacements.TryGetValue(group.Key, out var replacement))
            {
                if (!beforeHash.Equals(replacement.ExpectedFileHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"完整文件候选基线冲突：{replacement.RelativePath} 已被工具外修改。");
                var replacementBytes = (byte[])replacement.CandidateBytes.Clone();
                replacement.CandidateVerifier?.Invoke((byte[])replacementBytes.Clone());
                candidateBytes[group.Key] = replacementBytes;
                // Equal-length complete candidates can replay through the
                // existing sparse-patch format. Keep complete snapshots when
                // length changes; all paths still retain whole-file hashes.
                var changesLength = current.Length != replacementBytes.Length;
                IReadOnlyList<ByteChunk> replacementChunks = changesLength
                    ? [new ByteChunk(0, current, replacementBytes)] : BuildDifferenceChunks(current, replacementBytes);
                var wholeSnapshot = changesLength;
                if (!changesLength && replacementChunks.Sum(chunk => 80L + 8L * ((chunk.Length + 2L) / 3)) >=
                    8L * ((current.LongLength + 2L) / 3) + 80)
                {
                    // Many scattered edits can cost more JSON metadata than a
                    // snapshot. Preserve the compact existing whole-file form
                    // in that case instead of growing history unexpectedly.
                    replacementChunks = [new ByteChunk(0, current, replacementBytes)]; wholeSnapshot = true;
                }
                fileChanges.Add(new FileByteChange(Path.GetRelativePath(RootPath, group.Key), beforeHash,
                    HashService.Sha256(replacementBytes), replacementBytes.Length,
                    replacementChunks)
                    { WholeFileReplacement = wholeSnapshot, BeforeLength = wholeSnapshot ? current.Length : null });
                continue;
            }
            if (expectedHashes.Count > 0 && expectedHashes.Any(hash => !hash.Equals(beforeHash, StringComparison.OrdinalIgnoreCase)))
            {
                return new SaveResult(false, 0, allChanges.Count, 0, null, [$"保存中止：{Path.GetFileName(group.Key)} 已被工具外修改。"]);
            }

            var after = (byte[])current.Clone();
            var assigned = new bool[current.Length];
            var chunks = new List<ByteChunk>();
            foreach (var change in group.Value)
            {
                var rowOffset = checked((int)(change.Table.DataOffset + (long)change.RowIndex * change.Table.RowSize));
                var originalRow = current.AsSpan(rowOffset, change.Table.RowSize);
                var patches = _codecs.Get(change.Table.RecordCodecKey).EncodeChangedField(originalRow, change.Field, change.NewValue);
                foreach (var patch in patches)
                {
                    if (patch.Offset < 0 || patch.Offset + patch.Bytes.Length > change.Table.RowSize)
                    {
                        throw new InvalidDataException($"{change.Table.DisplayName}.{change.Field.Name} 的 codec 写入越过行边界。");
                    }
                    var offset = checked(rowOffset + patch.Offset);
                    var old = current.AsSpan(offset, patch.Bytes.Length).ToArray();
                    for (var byteIndex = 0; byteIndex < patch.Bytes.Length; byteIndex++)
                    {
                        var at = offset + byteIndex;
                        if (assigned[at] && after[at] != patch.Bytes[byteIndex])
                            throw new InvalidDataException($"关联表对 {Path.GetFileName(group.Key)} +0x{at:X} 提出了不同值，请检查重叠的文本或字段。");
                        assigned[at] = true;
                    }
                    Buffer.BlockCopy(patch.Bytes, 0, after, offset, patch.Bytes.Length);
                    if (!old.AsSpan().SequenceEqual(patch.Bytes)) chunks.Add(new ByteChunk(offset, old, patch.Bytes));
                    if (recordCellHistory) tableHistory.Add(new HistoryTableChange(
                        change.Table.DisplayName,
                        change.RowIndex,
                        change.Id,
                        change.Field.Name,
                        Convert.ToString(change.OldValue, CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(change.NewValue, CultureInfo.InvariantCulture) ?? string.Empty,
                        offset,
                        patch.Bytes.Length));
                }
            }
            if (extraByFile.TryGetValue(group.Key, out var prepared))
            {
                foreach (var edit in prepared)
                {
                    if (!beforeHash.Equals(edit.ExpectedFileHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"专用补丁基线冲突：{edit.RelativePath} 已在准备后发生变化。");
                    foreach (var patch in edit.Patches)
                    {
                        if (patch.Offset < 0 || patch.ExpectedBytes.Length != patch.NewBytes.Length ||
                            patch.Offset > current.LongLength - patch.NewBytes.Length)
                            throw new InvalidDataException($"专用补丁越界或改变长度：{edit.RelativePath}。");
                        var offset = checked((int)patch.Offset);
                        if (!current.AsSpan(offset, patch.ExpectedBytes.Length).SequenceEqual(patch.ExpectedBytes))
                            throw new IOException($"专用补丁原始字节不一致：{edit.RelativePath} +0x{offset:X}。");
                        for (var index = 0; index < patch.NewBytes.Length; index++)
                        {
                            var at = offset + index;
                            if (assigned[at] && after[at] != patch.NewBytes[index])
                                throw new InvalidDataException($"普通字段与专用补丁在 {edit.RelativePath} +0x{at:X} 提出了不同值。");
                            assigned[at] = true;
                            after[at] = patch.NewBytes[index];
                        }
                        if (!patch.ExpectedBytes.AsSpan().SequenceEqual(patch.NewBytes))
                            chunks.Add(new ByteChunk(offset, patch.ExpectedBytes, patch.NewBytes));
                    }
                }
            }
            VerifySemantics(group.Key, after, group.Value, prepared);
            candidateBytes[group.Key] = after;
            IReadOnlyList<ByteChunk> historyChunks = !recordCellHistory && chunks.Count > 4096
                ? [new ByteChunk(0, current, after)] : BuildDifferenceChunks(current, after);
            fileChanges.Add(new FileByteChange(Path.GetRelativePath(RootPath, group.Key), beforeHash, HashService.Sha256(after), after.Length, historyChunks));
        }

        AppearanceSyncSession? appearanceSync = null;
        var appearanceUpdates = AppearanceSyncUpdates.FromChanges(allChanges);
        if (appearanceUpdates.Count > 0)
        {
            var exePath = Path.GetFullPath(ProjectFileLocator.Resolve(RootPath, "Ekd5.exe"));
            var executable = candidateBytes.TryGetValue(exePath, out var exeCandidate)
                ? exeCandidate : File.ReadAllBytes(exePath);
            appearanceSync = SaveAppearanceSyncService.Prepare(RootPath, ActiveProfile.ProfileId, executable, appearanceUpdates);
        }

        var history = new HistoryEntry
        {
            SchemaVersion = fileEdits.Count > 0 ? "2.1" : "2.0",
            OperationKind = allChanges.Count == 0 && (extraEdits.Count > 0 || fileEdits.Count > 0) ? "专用二进制保存" : "数据保存",
            Status = HistoryStatus.Pending,
            ProfileId = ActiveProfile.ProfileId,
            VariantId = ActiveProfile.VariantId,
            LayoutRevision = ActiveProfile.LayoutRevision,
            DetectionMethod = ProfileMatch?.DetectionMethod ?? "未知",
            CoreFileFingerprints = CaptureCoreFileFingerprints(),
            Summary = $"保存 {changesByFile.Count} 个文件、{allChanges.Count} 个字段",
            Modules = allChanges.Select(change => change.Table.Module).Concat(extraEdits.Select(edit => edit.Module))
                .Concat(fileEdits.Select(edit => edit.Module)).Distinct(StringComparer.Ordinal).ToList(),
            Files = fileChanges,
            Tables = tableHistory
        };
        var written = new List<FileByteChange>();
        var historyRegistered = false;
        try
        {
            _history.Append(RootPath, history);
            historyRegistered = true;
            foreach (var fileChange in fileChanges)
            {
                // Register the file before writing.  BinaryTableWriter verifies
                // the resulting hash, so a failure after File.WriteAllBytes
                // still needs to be considered for rollback.
                written.Add(fileChange);
                _writer.Write(fileChange, RootPath, useNewBytes: true);
            }
            // Reopen complete on-disk files before committing history or accepting any draft.
            var verifiedDocuments = new Dictionary<string, TableDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in changesByFile)
            {
                var bytes = File.ReadAllBytes(file.Key);
                if (!bytes.AsSpan().SequenceEqual(candidateBytes[file.Key]))
                    throw new IOException($"写后完整文件与候选不一致：{Path.GetFileName(file.Key)}。");
                extraByFile.TryGetValue(file.Key, out var prepared);
                VerifySemantics(file.Key, bytes, file.Value, prepared);
                if (replacements.TryGetValue(file.Key, out var replacement))
                    replacement.CandidateVerifier?.Invoke((byte[])bytes.Clone());
                foreach (var document in _documents.Values.Where(document => Path.GetFullPath(document.FilePath).Equals(file.Key, StringComparison.OrdinalIgnoreCase)))
                    verifiedDocuments[document.Definition.Key] = _reader.ReadBytes(file.Key, document.Definition, bytes);
            }
            if (appearanceSync != null)
            {
                appearanceSync.Commit();
                fileChanges.AddRange(appearanceSync.AppliedFiles.Select(file => file.Change));
                history.AppearanceSyncMessages = [appearanceSync.Report.Summary, .. appearanceSync.Report.Diagnostics];
                history.Summary += "；" + appearanceSync.Report.Summary;
                if (appearanceSync.AppliedFiles.Count > 0) history.Modules.Add("存档形象同步");
            }
            history.Status = HistoryStatus.Active;
            _history.Update(RootPath, history);
            foreach (var document in _documents.Values)
            {
                if (changesByFile.ContainsKey(Path.GetFullPath(document.FilePath)))
                {
                    document.MarkSaved(verifiedDocuments[document.Definition.Key]);
                }
            }
            AddParentReferenceNames();
            AddDerivedItemFields();
            foreach (var draft in participants) draft.AcceptSaved();
            Commands.Clear();
            var changedBytes = recordCellHistory ? fileChanges.Sum(change => change.Chunks.Sum(chunk => chunk.Length))
                : fileChanges.Sum(change => change.Chunks.Sum(chunk => chunk.NewBytes.Where((value, index) => value != chunk.OldBytes[index]).Count()));
            var savedMessage = $"已保存：{fileChanges.Count} 个文件、{allChanges.Count} 个字段、{changedBytes} 字节；候选及磁盘语义复读通过。";
            if (appearanceSync?.Report.HasWarnings == true) savedMessage = "形象已保存，部分存档未同步。 " + savedMessage;
            return new SaveResult(true, fileChanges.Count, allChanges.Count, changedBytes, history,
                appearanceSync == null ? [savedMessage] : [savedMessage + " " + appearanceSync.Report.Summary, .. appearanceSync.Report.Diagnostics])
                { AppearanceSync = appearanceSync?.Report };
        }
        catch (Exception ex)
        {
            var rollbackErrors = new List<string>();
            try { appearanceSync?.Rollback(); }
            catch (Exception rollback) { rollbackErrors.Add("存档形象回滚：" + rollback.Message); }
            foreach (var recover in recoverDrafts)
            {
                try { recover(); }
                catch (Exception memoryException) { rollbackErrors.Add($"恢复草稿失败：{memoryException.Message}"); }
            }
            foreach (var fileChange in written.AsEnumerable().Reverse())
            {
                try
                {
                    var path = Path.Combine(RootPath, fileChange.RelativePath);
                    if (!File.Exists(path))
                    {
                        rollbackErrors.Add($"回滚文件不存在：{fileChange.RelativePath}");
                        continue;
                    }
                    var currentHash = HashService.Sha256(path);
                    if (currentHash.Equals(fileChange.BeforeHash, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!currentHash.Equals(fileChange.AfterHash, StringComparison.OrdinalIgnoreCase))
                    {
                        rollbackErrors.Add($"回滚跳过：{fileChange.RelativePath} 已被外部修改。");
                        continue;
                    }
                    _writer.Write(fileChange, RootPath, useNewBytes: false);
                }
                catch (Exception rollbackException) { rollbackErrors.Add(rollbackException.Message); }
            }
            if (historyRegistered)
            {
                history.Status = rollbackErrors.Count == 0 ? HistoryStatus.Failed : HistoryStatus.Conflict;
                try { _history.Update(RootPath, history); }
                catch (Exception historyException) { rollbackErrors.Add($"历史状态更新失败：{historyException.Message}"); }
            }
            var details = rollbackErrors.Count == 0 ? string.Empty : $" 回滚异常：{string.Join("；", rollbackErrors)}";
            return new SaveResult(false, changesByFile.Count, allChanges.Count, 0, historyRegistered ? history : null, [$"保存失败：{ex.Message}{details}"]);
        }
    }

    private void VerifySemantics(string path, byte[] candidate, IReadOnlyList<TableCellChange> changes, IReadOnlyList<PreparedBinaryEdit>? prepared)
    {
        foreach (var change in changes)
        {
            var offset = checked((int)(change.Table.DataOffset + (long)change.RowIndex * change.Table.RowSize));
            var decoded = _codecs.Get(change.Table.RecordCodecKey).Decode(candidate.AsSpan(offset, change.Table.RowSize), change.Field);
            if (!ValuesEqual(decoded, change.NewValue))
                throw new InvalidDataException($"语义复读不一致：{Path.GetFileName(path)} / {change.Table.DisplayName} ID={change.Id} {change.Field.Name}。");
        }
        if (prepared != null)
            foreach (var edit in prepared) edit.CandidateVerifier?.Invoke((byte[])candidate.Clone());
    }

    private static IReadOnlyList<ByteChunk> BuildDifferenceChunks(byte[] before, byte[] after)
    {
        var result = new List<ByteChunk>();
        for (var offset = 0; offset < before.Length; offset++)
        {
            if (before[offset] == after[offset]) continue;
            var start = offset;
            while (offset + 1 < before.Length && before[offset + 1] != after[offset + 1]) offset++;
            var length = offset - start + 1;
            result.Add(new ByteChunk(start, before.AsSpan(start, length).ToArray(), after.AsSpan(start, length).ToArray()));
        }
        return result;
    }

    public HistoryOperationResult UndoSaved(string? operationId = null)
    {
        var result = _history.Undo(RootPath, ActiveProfile.ProfileId, ActiveProfile.VariantId, ActiveProfile.LayoutRevision, operationId);
        if (result.Success) ReloadLoadedDocuments();
        return result;
    }

    public HistoryOperationResult RedoSaved(string? operationId = null)
    {
        var result = _history.Redo(RootPath, ActiveProfile.ProfileId, ActiveProfile.VariantId, ActiveProfile.LayoutRevision, operationId);
        if (result.Success) ReloadLoadedDocuments();
        return result;
    }

    public void ReloadLoadedDocuments()
    {
        var keys = _documents.Keys.ToArray();
        var sourceFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!_definitions.TryGetValue(key, out var definition)) continue;
            try { _documents[key] = ReadSharedSource(definition, sourceFiles); }
            catch { _documents.Remove(key); }
        }
        AddParentReferenceNames();
        AddDerivedItemFields();
        DiscardNativeDrafts();
        Commands.Clear();
    }

    private void ReloadAllDocuments()
    {
        _documents.Clear();
        var sourceFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            try { _documents[definition.Key] = ReadSharedSource(definition, sourceFiles); }
            catch { }
        }
        AddParentReferenceNames();
        AddDerivedItemFields();
        DiscardNativeDrafts();
        Commands.Clear();
    }

    private TableDocument ReadSharedSource(TableDefinition definition, IDictionary<string, byte[]> sourceFiles)
    {
        var validation = _reader.Validate(RootPath, definition);
        if (!validation.IsUsable)
            throw new InvalidDataException($"{definition.DisplayName}：{validation.Message}（{definition.FileName}）");
        if (!sourceFiles.TryGetValue(validation.Path, out var bytes))
            sourceFiles[validation.Path] = bytes = File.ReadAllBytes(validation.Path);
        // Documents never mutate their saved source bytes. Sharing one read
        // also prevents a large, appended EXE being retained once per table.
        return _reader.ReadBytes(validation.Path, definition, bytes);
    }

    private Dictionary<string, string> CaptureCoreFileFingerprints()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ProfileDetector.CoreFiles)
        {
            var path = ProjectFileLocator.Resolve(RootPath, name);
            if (File.Exists(path)) result[name] = HashService.Sha256(path);
        }
        return result;
    }

    private void AddDerivedItemFields()
    {
        if (!_documents.TryGetValue("detailed-job-growth", out var growth)) return;
        _documents.TryGetValue("detailed-jobs", out var detailedJobs);

        foreach (var key in new[] { "items-low", "items-high", "items-extended" })
        {
            if (!_documents.TryGetValue(key, out var itemDocument)) continue;
            AddDerivedColumn(itemDocument.Data, "物品分类");
            AddDerivedColumn(itemDocument.Data, "可装备部队");
            itemDocument.Data.Columns["物品分类"]!.ReadOnly = false;
            itemDocument.Data.Columns["可装备部队"]!.ReadOnly = false;
            foreach (DataRow row in itemDocument.Data.Rows)
            {
                var rowIndex = itemDocument.Data.Rows.IndexOf(row);
                var semantics = ItemTypeService.Resolve(itemDocument, rowIndex);
                row["物品分类"] = semantics.CategoryName;
                row["可装备部队"] = semantics.PermissionColumn is { } equipmentColumn
                    ? BuildEquipmentSummary(growth.Data, detailedJobs?.Data, equipmentColumn)
                    : "不适用";
            }
            itemDocument.Data.Columns["物品分类"]!.ReadOnly = true;
            itemDocument.Data.Columns["可装备部队"]!.ReadOnly = true;
        }
    }

    private void AddParentReferenceNames()
    {
        foreach (var document in _documents.Values)
        {
            var parentKey = document.Definition.ParentKey;
            if (string.IsNullOrWhiteSpace(parentKey) || !_documents.TryGetValue(parentKey, out var parent) || !parent.Data.Columns.Contains("名称")) continue;
            AddDerivedColumn(document.Data, "名称");
            document.Data.Columns["名称"]!.ReadOnly = false;
            var names = parent.Data.Rows.Cast<DataRow>().ToDictionary(
                row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture),
                row => Convert.ToString(row["名称"], CultureInfo.InvariantCulture) ?? string.Empty);
            foreach (DataRow row in document.Data.Rows)
            {
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                row["名称"] = names.GetValueOrDefault(id, string.Empty);
            }
            document.Data.Columns["名称"]!.SetOrdinal(1);
            document.Data.Columns["名称"]!.ReadOnly = true;
        }
    }

    private static bool NeedsItemDerivedRefresh(string tableKey)
        => tableKey is "items-low" or "items-high" or "items-extended" or "detailed-job-growth" or "detailed-jobs" or "job-series" or "strategies";

    private void ExecuteDraftOperation(IReadOnlyList<TableDocument> documents, Action operation)
    {
        var distinct = documents.Distinct().ToArray();
        var before = distinct.ToDictionary(document => document.Definition.Key, document => TableSnapshot.Capture(document.Data), StringComparer.OrdinalIgnoreCase);
        try
        {
            operation();
            foreach (var document in distinct.Where(document => document.Definition.Key == "forge"))
                foreach (var dirtyRow in document.EnumerateChanges().Select(change => change.RowIndex).Distinct())
                    ExtendDataService.ValidateForgeDraft(document, dirtyRow);
            foreach (var document in distinct) RefreshDerivedFor(document.Definition.Key);
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            // Complete every table recovery before running any derived refresh.
            foreach (var document in distinct)
            {
                try { before[document.Definition.Key].Restore(document.Data); }
                catch (Exception restore) { failures.Add(new InvalidOperationException($"恢复草稿失败：{document.Definition.Key}：{restore.Message}", restore)); }
            }
            foreach (var document in distinct)
            {
                try { RefreshDerivedFor(document.Definition.Key); }
                catch (Exception refresh) { failures.Add(new InvalidOperationException($"恢复派生显示失败：{document.Definition.Key}：{refresh.Message}", refresh)); }
            }
            if (failures.Count > 0) throw new AggregateException("草稿操作失败，恢复异常已汇总。", new[] { original }.Concat(failures));
            throw;
        }
    }

    private void RestoreDraftSnapshots(IReadOnlyList<TableDocument> documents, IReadOnlyDictionary<string, TableSnapshot> snapshots)
        => ExecuteDraftOperation(documents, () =>
        {
            foreach (var document in documents) snapshots[document.Definition.Key].Restore(document.Data);
        });

    private void RefreshDerivedFor(string tableKey)
    {
        if (NeedsItemDerivedRefresh(tableKey))
        {
            AddParentReferenceNames();
            AddDerivedItemFields();
        }
        if (tableKey is "persons") AddParentReferenceNames();
    }

    private static void AddDerivedColumn(DataTable table, string name)
    {
        if (table.Columns.Contains(name)) return;
        var column = table.Columns.Add(name, typeof(string));
        column.ReadOnly = false;
        column.ExtendedProperties["Derived"] = true;
    }

    private static string BuildEquipmentSummary(DataTable growth, DataTable? detailedJobs, string equipmentColumn)
    {
        if (!growth.Columns.Contains(equipmentColumn)) return $"未找到装备列：{equipmentColumn}";
        var allowed = growth.Rows.Cast<DataRow>()
            .Where(row => Convert.ToInt32(row[equipmentColumn], CultureInfo.InvariantCulture) != 0)
            .Select(row =>
            {
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                var name = detailedJobs?.Rows.Cast<DataRow>()
                    .FirstOrDefault(job => Convert.ToInt32(job["ID"], CultureInfo.InvariantCulture) == id)?["名称"];
                return name == null ? $"ID={id}" : $"ID={id} {name}";
            })
            .ToArray();
        return allowed.Length == 0 ? "无" : string.Join("、", allowed);
    }

    private static bool IsDifferent(DataTable table, TableSnapshot snapshot)
    {
        // Snapshot restoration is intentionally tested through a clone to avoid exposing internal state.
        var clone = table.Copy();
        snapshot.Restore(clone);
        foreach (DataRow row in table.Rows)
        {
            var index = table.Rows.IndexOf(row);
            foreach (DataColumn column in table.Columns)
            {
                if (column.ColumnName == "ID" || column.ExtendedProperties.Contains("Derived")) continue;
                if (!Equals(row[column], clone.Rows[index][column.ColumnName])) return true;
            }
        }
        return false;
    }

    private static object ComputeBatchValue(object? old, Type type, TableFieldDefinition? field, string operation, string input)
    {
        if (operation == "查找替换")
        {
            var parts = input.Split("=>", 2, StringSplitOptions.None);
            if (parts.Length != 2) throw new FormatException("查找替换的值格式必须是：查找内容=>替换内容。");
            var replaced = (Convert.ToString(old, CultureInfo.InvariantCulture) ?? string.Empty).Replace(parts[0], parts[1], StringComparison.Ordinal);
            return ConvertForColumn(replaced, type);
        }

        if (type == typeof(string))
        {
            if (operation != "统一赋值") throw new InvalidOperationException("文本字段只支持统一赋值或查找替换。");
            return input;
        }

        if (field == null || field.Kind is not (FieldKind.UInt8 or FieldKind.UInt16 or FieldKind.UInt32)) throw new InvalidOperationException("当前字段不是可批量计算的数值字段。");
        var current = Convert.ToInt64(old ?? 0, CultureInfo.InvariantCulture);
        var inputNumber = BinaryValueCodec.ParseInteger64(input, long.MinValue, long.MaxValue, field.Name);
        var target = operation switch
        {
            "数值增加" => current + inputNumber,
            "数值减少" => current - inputNumber,
            "统一赋值" => inputNumber,
            _ => throw new InvalidOperationException($"不支持的批量操作：{operation}")
        };
        BinaryValueCodec.ParseInteger64(target.ToString(CultureInfo.InvariantCulture), field.Minimum, field.Maximum, field.Name);
        return field.Kind == FieldKind.UInt32
            ? checked((long)target)
            : checked((int)target);
    }

    private static object ConvertForColumn(string value, Type type)
    {
        if (type == typeof(string)) return value;
        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture)!;
    }

    private static bool ValuesEqual(object? left, object? right)
        => string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static IReadOnlyList<ByteChunk> MergeChunks(IEnumerable<ByteChunk> input)
        => ByteChunkMerger.MergeAdjacent(input);
}
