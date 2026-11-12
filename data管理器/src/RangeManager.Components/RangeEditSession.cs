using System.Text;
using System.Text.Json;
using DataManager.Core.Editing;

namespace RangeManager.App;

/// <summary>One draft for both tabs. Neither text editing nor undo writes game files.</summary>
public sealed class RangeEditSession : IDisposable
{
    private sealed record TextDraft(string Text, string? Error);
    private sealed record Snapshot(List<RangeBinding> Bindings, Dictionary<(RangeKey Key, int Group), TextDraft> Text,
        RangeExtensionMapping Extension);
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly Dictionary<(RangeKey Key, int Group), TextDraft> _text = [];
    private List<RangeBinding> _bindings = [];
    private List<RangeBinding> _baseline = [];
    private Dictionary<RangeKey, string> _baselineValues = [];
    private readonly Dictionary<string, string> _diskHashes = new(StringComparer.OrdinalIgnoreCase);
    private RangeExtensionMapping _extension = null!;
    private E5RangeImages _previewImages = null!;
    private int _loadRevision;
    private long _draftRevision;
    private Snapshot? _pendingCommandBefore;
    /// <summary>Null keeps the standalone undo stacks; a host may route commands to its shared history.</summary>
    public Action<IEditCommand>? CommandSink { get; set; }
    public GameBase Game { get; private set; } = null!;
    public IReadOnlyList<RangeBinding> Bindings => _bindings;
    public RangeExtensionMapping Extension => _extension;
    public int LoadRevision => _loadRevision;
    /// <summary>Monotonic draft generation; undo, reload and save recovery never restore an old generation.</summary>
    public long DraftRevision => Interlocked.Read(ref _draftRevision);
    public RangeValidationFailure? LastValidationFailure { get; private set; }
    public RangeKey? LastValidationFailureKey => LastValidationFailure?.Key;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => _bindings.Any(b => b.IsModified) || _text.Values.Any(t => t.Error != null) || _extension.IsDirty;
    public event EventHandler? Changed;

    public RangeEditSession(string root, byte[]? executableImage = null, bool recoverStandaloneTransaction = true)
    {
        if (recoverStandaloneTransaction) RangeSaveTransaction.Recover(root);
        Load(root, executableImage);
    }

    private void Load(string root, byte[]? executableImage = null)
    {
        _loadRevision++;
        Interlocked.Increment(ref _draftRevision);
        LastValidationFailure = null;
        Game?.Dispose();
        Game = new GameBase(root, executableImage);
        _previewImages?.Dispose();
        _previewImages = new E5RangeImages(root);
        _extension = RangeExtensionMapping.Read(Game);
        var native = NativeRangeCatalog.Read(Game).ToDictionary(b => b.Key);
        foreach (var managed in ManagedRangeRuntime.ReadBindings(Game))
        {
            if (native.TryGetValue(managed.Key, out var original) && !original.Editable)
            {
                managed.Editable = false;
                managed.ReadOnlyReason = original.ReadOnlyReason;
                managed.Special = original.Special;
            }
            native[managed.Key] = managed;
        }
        // Registered records are only writable when the current native consumers
        // still match the audited contract; metadata cannot grant that capability.
        foreach (var kind in Enum.GetValues<RangeKind>())
        {
            string contractError = WriteContractError(kind);
            if (contractError.Length == 0) continue;
            foreach (var binding in native.Values.Where(b => b.Key.Kind == kind))
            {
                binding.Editable = false;
                binding.ReadOnlyReason = contractError;
            }
        }
        _bindings = RangeModelCopies.Clone(native.Values.OrderBy(b => b.Key.Kind).ThenBy(b => b.Key.Id));
        foreach (var binding in _bindings) binding.IsModified = false;
        _baseline = RangeModelCopies.Clone(_bindings);
        _baselineValues = _bindings.ToDictionary(b => b.Key, Fingerprint);
        _text.Clear();
        _undo.Clear();
        _redo.Clear();
        _pendingCommandBefore = null;
        _diskHashes.Clear();
        _diskHashes[Game.ExePath] = Game.Exe.OriginalSha256;
        foreach (string name in new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" })
        {
            string path = ResolveResourcePath(Game.RootPath, name);
            _diskHashes[path] = RangeSaveTransaction.Hash(path);
        }
    }

    private static string ResolveResourcePath(string root, string name)
    {
        string inE5 = Path.Combine(root, "E5", name);
        string path = File.Exists(inE5) ? inE5 : Path.Combine(root, name);
        return File.Exists(path) ? path : inE5;
    }

    public RangeBinding Get(RangeKey key) => _bindings.FirstOrDefault(b => b.Key == key)
        ?? throw new KeyNotFoundException("范围编号不存在：" + key);
    public IReadOnlyList<RangeBinding> Aliases(RangeKey key)
    {
        string identity = Get(key).Definition.Identity;
        return _bindings.Where(b => b.Definition.Identity == identity).ToArray();
    }
    public IReadOnlyList<CellOffset> Preview(RangeKey key, RangePreviewContext context)
        => NativeRangeCatalog.Expand(Get(key), context);
    public IReadOnlyList<CellOffset> GroupPreview(RangeKey key, int group, RangePreviewContext context)
        => NativeRangeCatalog.ExpandGroup(Get(key), group, context);
    public RangePixelTiles CreatePreviewTiles(RangeKey key)
        => _previewImages.CreateFixedPixelTiles();

    /// <summary>
    /// Gets an owned copy of the actual Hitarea.e5 entry for an extension
    /// mapping value.  Extension values are native range IDs, so their image
    /// preview follows the attack-range archive's ID+1 entry mapping.  A
    /// missing/invalid E5 entry returns null and never affects the mapping
    /// draft or its save path.
    /// </summary>
    public Bitmap? CreateExtensionRangePreview(int rangeId)
        => _previewImages.Copy(attack: true, shapeId: rangeId);

    /// <summary>Returns whether the selected Pierce binding may change its centre.</summary>
    public bool CanChangeCenter(RangeKey key, out string error)
    {
        error = "";
        if (key.Kind != RangeKind.Pierce)
        {
            error = "攻击范围固定以自身为中心";
            return false;
        }
        var binding = Get(key);
        if (!binding.Editable)
        {
            error = binding.ReadOnlyReason.Length == 0 ? "该穿透编号不可编辑" : binding.ReadOnlyReason;
            return false;
        }
        if (Game.Version == EngineVersion.V65 && key.Id == 0x0A)
        {
            error = "6.5 穿透 0A 使用独立原生谓词，不能切换中心";
            return false;
        }
        if (Game.Version == EngineVersion.V65 && key.Id == 0x0B && !binding.IsManaged)
        {
            error = "6.5 穿透 0B 是原生组合分支，中心语义不能单独切换";
            return false;
        }
        string contractError = WriteContractError(RangeKind.Pierce);
        if (contractError.Length != 0)
        {
            error = contractError;
            return false;
        }
        return true;
    }

    /// <summary>Changes only the binding centre; shared geometry and aliases keep their own centres.</summary>
    public bool UpdateCenter(RangeKey key, RangeCenterMode center, out string error)
    {
        error = "";
        if (!Enum.IsDefined(center)) { error = "范围中心模式无效"; return false; }
        var binding = Get(key);
        if (binding.Center == center) return true;
        if (!CanChangeCenter(key, out error)) return false;
        using var edit = BeginEdit();
        binding.Center = center;
        if (binding.IsManaged) binding.Name = PierceName(binding.Definition.Groups.FirstOrDefault()?.Geometry ?? RangeGeometryKind.Mirror, center);
        RecalculateModified();
        OnChanged();
        return true;
    }
    public string GetText(RangeKey key, int group) => _text.TryGetValue((key, group), out var draft)
        ? draft.Text : Get(key).Definition.Groups[group].Text;
    public string? GetTextError(RangeKey key, int group) => _text.TryGetValue((key, group), out var draft) ? draft.Error : null;

    public bool UpdateText(RangeKey key, int groupIndex, string text, out string error)
    {
        var binding = Get(key);
        EnsureEditable(binding);
        if (GetText(key, groupIndex) == text && GetTextError(key, groupIndex) == null) { error = ""; return true; }
        using var edit = BeginEdit();
        try
        {
            var cells = ParseGenerators(text, binding.Definition.Groups[groupIndex].Geometry);
            binding.Definition.Groups[groupIndex].Generators = cells;
            ClearAliasDrafts(key, groupIndex);
            _text[(key, groupIndex)] = new(text, null);
            RecalculateModified();
            error = "";
            OnChanged();
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or OverflowException)
        {
            error = ex.Message;
            _text[(key, groupIndex)] = new(text, error);
            OnChanged();
            return false;
        }
    }

    public void EditCell(RangeKey key, int groupIndex, CellOffset worldCell, RangePreviewContext context, bool add)
    {
        var binding = Get(key);
        EnsureEditable(binding);
        if (GetTextError(key, groupIndex) is { } error) throw new InvalidDataException(error);
        var generator = NativeRangeCatalog.ToGenerator(binding, groupIndex, worldCell, context);
        if (generator == null) return;
        var group = binding.Definition.Groups[groupIndex];
        // SidePair stores one signed canonical side, while the native expansion
        // exposes both signs. Keep the group's stored sign when converting a
        // clicked world cell back into text so either side removes the same group.
        int storedSide = group.Generators.FirstOrDefault(g => g.Dy != 0)?.Dy ?? 0;
        if (storedSide == 0) storedSide = generator.Dy == 0 ? 1 : generator.Dy;
        var canonical = group.Geometry == RangeGeometryKind.SidePair
            ? new CellOffset(generator.Dx, Math.Abs(generator.Dy) * Math.Sign(storedSide))
            : ParseGenerators($"{generator.Dx},{generator.Dy}", group.Geometry).SingleOrDefault();
        if (canonical == null) return;
        var normalized = ParseGenerators(group.Text, group.Geometry);
        bool exists = normalized.Contains(canonical);
        if (exists == add) return;
        using var edit = BeginEdit();
        group.Generators = add ? [.. normalized, canonical] : normalized.Where(g => g != canonical).ToList();
        ClearAliasDrafts(key, groupIndex);
        RecalculateModified();
        OnChanged();
    }

    public RangeKey Add(RangeKind kind, RangeGeometryKind geometry = RangeGeometryKind.Mirror,
        RangeCenterMode center = RangeCenterMode.Target, bool excludePrimaryTarget = false)
    {
        // Preserve the legacy Add(Attack) call while keeping attack bindings
        // intrinsically self-centered. Pierce retains Target as its default.
        if (kind == RangeKind.Attack) center = RangeCenterMode.Self;
        string contractError = GetAddError(kind);
        if (contractError.Length != 0) throw new InvalidDataException(contractError);
        if (kind == RangeKind.Attack && geometry != RangeGeometryKind.Mirror)
            throw new InvalidDataException("攻击范围使用原生镜像生成元");
        if (kind == RangeKind.Attack && excludePrimaryTarget)
            throw new InvalidDataException("攻击范围不支持排除主目标");
        int first = kind == RangeKind.Attack ? 0x26 : 0x0C;
        int id = Enumerable.Range(first, 0xFF - first).FirstOrDefault(i => _bindings.All(b => b.Key != new RangeKey(kind, i)), -1);
        if (id < 0) throw new InvalidDataException("该类范围的有效单字节编号已经用完");
        using var edit = BeginEdit();
        var key = new RangeKey(kind, id);
        _bindings.Add(new RangeBinding
        {
            Key = key, Name = PierceName(geometry, center),
            Definition = new() { Identity = $"managed:{kind}:{id}:{Guid.NewGuid():N}", Groups = [new RangeGroup { Geometry = geometry, Name = geometry == RangeGeometryKind.Mirror ? "镜像生成元" : "方向生成元" }] },
            Center = center, ExcludePrimaryTarget = excludePrimaryTarget,
            IsManaged = true, IsModified = true, Source = "EXE 内扩展定义"
        });
        _bindings = _bindings.OrderBy(b => b.Key.Kind).ThenBy(b => b.Key.Id).ToList();
        OnChanged();
        return key;
    }

    private static string PierceName(RangeGeometryKind geometry, RangeCenterMode center) =>
        $"{(center == RangeCenterMode.Self ? "自身中心" : "目标中心")}·{(geometry == RangeGeometryKind.Mirror ? "镜像范围" : "方向范围")}";

    /// <summary>
    /// Returns the reason a new runtime range cannot be installed in this
    /// executable. The editor uses this preflight so an unsupported or
    /// externally hooked base explains the problem before the user opens the
    /// add menu; Add still repeats the check for callers that bypass the UI.
    /// </summary>
    public string GetAddError(RangeKind kind)
    {
        if (Game == null) return "尚未打开基底";
        string contractError = WriteContractError(kind);
        if (contractError.Length != 0) return contractError;
        // Build() rewrites every existing managed kind together with the new
        // record. Check those contracts now so a valid Pierce addition cannot
        // be accepted only to fail later because an older managed Attack (or
        // Pierce) record belongs to an unknown ABI.
        foreach (var existingKind in _bindings.Where(b => b.IsManaged)
            .Select(b => b.Key.Kind).Distinct())
        {
            if (existingKind == kind) continue;
            string existingError = WriteContractError(existingKind);
            if (existingError.Length != 0)
                return $"当前 EXE 已含{(existingKind == RangeKind.Attack ? "攻击" : "穿透")}扩展记录，但该类范围契约不匹配：{existingError}";
        }
        // Older development builds could have persisted the 6.5 0A record as
        // a managed range. Its native branch has an independent target
        // predicate and cannot coexist with the generic runtime writer;
        // reject either kind of addition up front instead of waiting for Save.
        if (Game.Version == EngineVersion.V65 &&
            _bindings.Any(b => b.IsManaged && b.Key == new RangeKey(RangeKind.Pierce, 0x0A)))
            return "当前 EXE 扩展包含旧的 6.5 穿透 0A 管理记录；该编号使用独立原生判定，需恢复或清理旧扩展后才能新增范围";
        int first = kind == RangeKind.Attack ? 0x26 : 0x0C;
        return Enumerable.Range(first, 0xFF - first).Any(id =>
            _bindings.All(b => b.Key != new RangeKey(kind, id)))
            ? ""
            : "该类范围的有效单字节编号已经用完";
    }

    public void Delete(RangeKey key)
    {
        EnsureEditable(Get(key));
        using var edit = BeginEdit();
        foreach (var group in Get(key).Definition.Groups) group.Generators.Clear();
        foreach (var alias in Aliases(key))
            foreach (var textKey in _text.Keys.Where(k => k.Key == alias.Key).ToArray()) _text.Remove(textKey);
        RecalculateModified();
        OnChanged();
    }

    public void Restore(RangeKey key)
    {
        var binding = Get(key);
        using var edit = BeginEdit();
        var baseline = _baseline.FirstOrDefault(b => b.Key == key);
        if (baseline == null)
        {
            _bindings.Remove(binding);
            foreach (var textKey in _text.Keys.Where(k => k.Key == key).ToArray()) _text.Remove(textKey);
        }
        else
        {
            var restored = baseline.Definition.Clone();
            foreach (var alias in Aliases(key))
            {
                alias.Definition = restored;
                foreach (var textKey in _text.Keys.Where(k => k.Key == alias.Key).ToArray()) _text.Remove(textKey);
            }
            // Centre and other binding fields are not part of the shared
            // geometry object. Restore them for this binding only; changing a
            // shared definition must not overwrite the centre of its aliases.
            binding.Center = baseline.Center;
            binding.ExcludePrimaryTarget = baseline.ExcludePrimaryTarget;
            binding.Name = baseline.Name;
        }
        RecalculateModified();
        OnChanged();
    }

    public void Undo() { if (_undo.TryPop(out var before)) { _redo.Push(Capture()); Apply(before); } }
    public void Redo() { if (_redo.TryPop(out var after)) { _undo.Push(Capture()); Apply(after); } }
    private Snapshot Capture() => new(RangeModelCopies.Clone(_bindings), new(_text), _extension.Clone());
    private void PushUndo()
    {
        if (CommandSink == null) { _undo.Push(Capture()); _redo.Clear(); }
        else _pendingCommandBefore = Capture();
    }

    /// <summary>Opaque, owned draft snapshot for hosts; it never writes the game directory.</summary>
    public object CaptureDraftSnapshot() => Capture();
    public void RestoreDraftSnapshot(object snapshot)
        => Apply(snapshot as Snapshot ?? throw new ArgumentException("范围草稿快照类型不匹配", nameof(snapshot)));
    private IDisposable BeginEdit()
    {
        PushUndo();
        return new PendingEditScope(this, _pendingCommandBefore);
    }
    private sealed class PendingEditScope(RangeEditSession owner, Snapshot? before) : IDisposable
    {
        public void Dispose()
        {
            // Unexpected failures before OnChanged must not leave a partial
            // global edit or carry its pending snapshot into the next action.
            if (before == null || !ReferenceEquals(owner._pendingCommandBefore, before)) return;
            owner._pendingCommandBefore = null;
            owner.Apply(before);
        }
    }

    private void Apply(Snapshot snapshot)
    {
        _bindings = RangeModelCopies.Clone(snapshot.Bindings);
        _text.Clear();
        foreach (var entry in snapshot.Text) _text.Add(entry.Key, entry.Value);
        _extension = snapshot.Extension.Clone();
        RecalculateModified();
        OnChanged();
    }

    public bool UpdateExtension(int index, int value, out string error)
    {
        error = "";
        if (!_extension.Supported) { error = _extension.Reason; return false; }
        if ((uint)index >= RangeExtensionMapping.MaxRangeValue + 1)
        {
            error = $"范围映射索引必须为 0x00..0x{RangeExtensionMapping.MaxRangeValue:X2}，0xFF 为保留哨兵";
            return false;
        }
        if (_extension.Values[index] == value) return true;
        // Validate on a clone first.  This keeps the current draft untouched
        // when the grid rejects an out-of-range value, and lets the undo entry
        // capture the actual pre-edit mapping.
        var candidate = _extension.Clone();
        if (!candidate.TrySet(index, value, out error)) return false;
        using var edit = BeginEdit();
        if (!_extension.TrySet(index, value, out error)) return false;
        OnChanged();
        return true;
    }

    public void RestoreExtension()
    {
        if (!_extension.Supported) throw new InvalidDataException(_extension.Reason);
        if (!_extension.IsDirty) return;
        using var edit = BeginEdit();
        _extension.Restore();
        OnChanged();
    }

    public void Validate()
    {
        LastValidationFailure = null;
        if (_extension.IsDirty) _extension.ValidateForSave();
        foreach (var ((key, group), draft) in _text)
            if (draft.Error != null)
            {
                var message = $"{key}，第 {group + 1} 组：{draft.Error}";
                LastValidationFailure = new(key, message, group);
                throw new InvalidDataException(message);
            }
        foreach (var binding in _bindings.Where(b => b.IsModified))
        {
            int? validationGroup = null;
            try
            {
                EnsureEditable(binding);
                for (int index = 0; index < binding.Definition.Groups.Count; index++)
                {
                    validationGroup = index;
                    var group = binding.Definition.Groups[index];
                    ParseGenerators(group.Text, group.Geometry);
                }
                validationGroup = null;
                if (binding.Definition.Groups.Sum(g => g.Generators.Count) > 256)
                    throw new InvalidDataException($"{binding.Key} 的所有组合组总计最多允许 256 个生成元");
                var contexts = new List<RangePreviewContext> { RangePreviewContext.Default };
                for (int y = -1; y <= 1; y++)
                    for (int x = -1; x <= 1; x++) contexts.Add(new(new CellOffset(x, y)));
                foreach (var context in contexts)
                {
                    var cells = NativeRangeCatalog.Expand(binding, context);
                    // Reserve one coordinate for the primary target; the old leaf writer has no bounds check.
                    if (binding.Key.Kind == RangeKind.Pierce && cells.Count > 24)
                        throw new InvalidDataException($"{binding.Key} 共 {cells.Count} 个额外坐标；当前原生缓冲仅允许最多 24 个额外坐标（另预留主目标）");
                }
                using var tiles = _previewImages.CreateFixedPixelTiles();
                CellOffset? targetMarker = binding.Key.Kind == RangeKind.Pierce && binding.Center == RangeCenterMode.Target
                    ? RangePreviewContext.Default.Target : null;
                using var icon = RangeBitmapRenderer.RenderIconFromTiles(binding.Key.Kind,
                    NativeRangeCatalog.Expand(binding, RangePreviewContext.Default).ToArray(),
                    targetMarker, tiles);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or ArgumentException)
            {
                LastValidationFailure = new(binding.Key, ex.Message, validationGroup);
                throw;
            }
        }
    }

    public void Save()
    {
        if (!IsDirty) return;
        var candidate = PrepareSaveCandidate();
        string root = Game.RootPath;
        RangeSaveTransaction.Commit(root, candidate.Files, candidate.ExpectedHashes);
        AcceptSaved();
    }

    public void VerifyBaselineFiles()
    {
        foreach (string name in new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" })
            if (!_diskHashes.ContainsKey(ResolveResourcePath(Game.RootPath, name)))
                throw new IOException("范围资源的 E5/根目录读取位置已改变，请重新打开工程：" + name);
        foreach (var (path, hash) in _diskHashes)
            if (RangeSaveTransaction.Hash(path) != hash) throw new IOException("基底文件已被外部修改，请重新打开：" + path);
    }

    public bool MatchesDiskBaselines()
        => new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" }
               .All(name => _diskHashes.ContainsKey(ResolveResourcePath(Game.RootPath, name))) &&
           _diskHashes.All(pair => RangeSaveTransaction.Hash(pair.Key) == pair.Value);

    /// <summary>Builds owned candidates only; the workspace owns the eventual multi-file transaction.</summary>
    public RangeSaveCandidate PrepareSaveCandidate()
    {
        Validate();
        VerifyBaselineFiles();
        var bindings = RangeModelCopies.Clone(_bindings);
        var extension = _extension.IsDirty ? _extension.Clone() : null;
        string root = Game.RootPath;
        byte[] baseline = (byte[])Game.Exe.Bytes.Clone();
        byte[] Build(byte[] input)
        {
            using var game = new GameBase(root, input);
            return ManagedRangeRuntime.Build(game, bindings, extension?.Clone());
        }
        // The runtime builder owns both the range definitions and the
        // CCZRNG03 mapping transaction. Passing the dirty mapping here keeps
        // table bytes, managed tail values, hooks and metadata in one
        // candidate executable; the old Apply path could only write the
        // native 33-byte prefix and would reject/lose 0x21+ mappings.
        byte[] executable = Build(baseline);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [Game.ExePath] = executable };
        using var images = new E5RangeImages(Game.RootPath);
        foreach (var binding in _bindings.Where(b => b.IsModified))
        {
            using var tiles = images.CreateFixedPixelTiles();
            CellOffset? targetMarker = binding.Key.Kind == RangeKind.Pierce && binding.Center == RangeCenterMode.Target
                ? RangePreviewContext.Default.Target : null;
            using var icon = RangeBitmapRenderer.RenderIconFromTiles(binding.Key.Kind,
                NativeRangeCatalog.Expand(binding, RangePreviewContext.Default).ToArray(),
                targetMarker, tiles);
            images.SetIcon(binding.Key.Kind == RangeKind.Attack, binding.Key.Id, icon);
        }
        foreach (var (path, bytes) in images.BuildPendingFiles()) files.Add(path, bytes);
        VerifyBaselineFiles();
        void Verify(byte[] bytes)
        {
            using var game = new GameBase(root, bytes);
            var actual = NativeRangeCatalog.Read(game).ToDictionary(binding => binding.Key);
            foreach (var managed in ManagedRangeRuntime.ReadBindings(game)) actual[managed.Key] = managed;
            foreach (var expected in bindings.Where(binding => binding.IsModified || binding.IsManaged))
                if (!actual.TryGetValue(expected.Key, out var found) || Fingerprint(found) != Fingerprint(expected))
                    throw new InvalidDataException($"范围候选复读不一致：{expected.Key} 的生成元、中心或组合定义与草稿不符。");
            if (extension != null)
            {
                var mapping = RangeExtensionMapping.Read(game);
                if (!mapping.Supported || !mapping.Values.SequenceEqual(extension.Values))
                    throw new InvalidDataException("范围候选复读不一致：拓展范围映射与草稿不符。");
            }
        }
        Verify(executable);
        return new RangeSaveCandidate(root, Game.ExePath, baseline, executable, files,
            new Dictionary<string, string>(_diskHashes, StringComparer.OrdinalIgnoreCase), Build)
            { ExecutableVerifier = Verify };
    }

    public void AcceptSaved() => ReloadFromDisk();
    public void ReloadFromDisk()
    {
        Load(Game.RootPath);
        OnChanged();
    }

    public void Discard()
    {
        _text.Clear();
        _bindings = RangeModelCopies.Clone(_baseline);
        _extension.Restore();
        _undo.Clear(); _redo.Clear(); _pendingCommandBefore = null;
        OnChanged();
    }

    public Action CaptureSaveRecovery()
    {
        string root = Game.RootPath;
        byte[] image = (byte[])Game.Exe.Bytes.Clone();
        var draft = Capture();
        var baseline = RangeModelCopies.Clone(_baseline);
        var values = new Dictionary<RangeKey, string>(_baselineValues);
        var hashes = new Dictionary<string, string>(_diskHashes, StringComparer.OrdinalIgnoreCase);
        var undo = _undo.Reverse().ToArray(); var redo = _redo.Reverse().ToArray();
        int revision = _loadRevision;
        return () =>
        {
            Load(root, image);
            _baseline = RangeModelCopies.Clone(baseline); _baselineValues = new(values);
            _diskHashes.Clear(); foreach (var pair in hashes) _diskHashes.Add(pair.Key, pair.Value);
            foreach (var value in undo) _undo.Push(value); foreach (var value in redo) _redo.Push(value);
            _loadRevision = revision;
            Apply(draft);
        };
    }

    public string Diagnostics
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine($"基底：{Game.RootPath}\n版本：{Game.VersionText}\nEXE SHA256：{Game.Exe.DiskSha256}");
            foreach (var kind in Enum.GetValues<RangeKind>())
                text.AppendLine($"{kind}：{_bindings.Count(b => b.Key.Kind == kind)} 项，其中扩展 {_bindings.Count(b => b.Key.Kind == kind && b.IsManaged)} 项");
            foreach (var kind in Enum.GetValues<RangeKind>())
                if (GetAddError(kind) is { Length: > 0 } addError)
                    text.AppendLine($"新增{(kind == RangeKind.Attack ? "攻击" : "穿透")}范围：不可用；{addError}");
            foreach (var binding in _bindings.Where(b => !b.Editable && b.ReadOnlyReason.Length != 0))
                text.AppendLine($"{binding.Key}：{binding.ReadOnlyReason}");
            text.AppendLine("预览由 EXE 几何生成；E5 仅为同步输出。穿透额外坐标预算为 24，主目标另预留 1。");
            return text.ToString();
        }
    }

    private void ClearAliasDrafts(RangeKey key, int group)
    {
        foreach (var alias in Aliases(key)) _text.Remove((alias.Key, group));
    }
    private void RecalculateModified()
    {
        foreach (var binding in _bindings)
            binding.IsModified = !_baselineValues.TryGetValue(binding.Key, out var before) || before != Fingerprint(binding);
    }
    private static string Fingerprint(RangeBinding binding) => JsonSerializer.Serialize(new
    {
        binding.Definition.Identity, binding.Definition.Groups, binding.Center, binding.ExcludePrimaryTarget, binding.Special
    });
    private static void EnsureEditable(RangeBinding binding)
    {
        if (!binding.Editable) throw new InvalidDataException(binding.ReadOnlyReason.Length == 0 ? "该原生保留编号不可编辑" : binding.ReadOnlyReason);
    }
    private string WriteContractError(RangeKind kind)
    {
        var errors = NativeRangeContract.Validate(Game, kind).ToList();
        if (kind == RangeKind.Pierce && !NativeRangeContract.TryReadPierceCenter(Game, out _, out string wrapperError))
            errors.Add(wrapperError);
        return string.Join("；", errors);
    }

    private static List<CellOffset> ParseGenerators(string text, RangeGeometryKind geometry)
    {
        var cells = RangeShapeTable.ParseText(text);
        if (cells.Count > 256) throw new InvalidDataException("每组最多 256 个生成元");
        var result = new List<CellOffset>();
        int sideSign = Math.Sign(cells.FirstOrDefault(c => c.Dy != 0)?.Dy ?? 1);
        foreach (var cell in cells)
        {
            if (cell.Dx is < -31 or > 31 || cell.Dy is < -31 or > 31)
                throw new InvalidDataException("可编辑生成元偏移须在 -31..31 内");
            if (cell.Dx == -1 && cell.Dy == -1) throw new InvalidDataException("-1,-1 与原生 FFFF 终止符冲突，请使用等价的 1,1 镜像生成元");
            if (cell.Dx == 0 && cell.Dy == 0) continue;
            CellOffset normalized = geometry switch
            {
                RangeGeometryKind.Mirror => ShapeGeometry.ToGenerator(cell)!,
                RangeGeometryKind.SidePair => new(cell.Dx, Math.Abs(cell.Dy) * sideSign),
                _ => cell
            };
            if (!result.Contains(normalized)) result.Add(normalized);
        }
        return result;
    }
    private void OnChanged()
    {
        Interlocked.Increment(ref _draftRevision);
        LastValidationFailure = null;
        if (_pendingCommandBefore is { } before)
        {
            _pendingCommandBefore = null;
            var after = Capture();
            CommandSink?.Invoke(new DelegateEditCommand("范围修改", () => Apply(before), () => Apply(after)));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        CommandSink = null;
        _previewImages?.Dispose();
        Game?.Dispose();
    }
}
