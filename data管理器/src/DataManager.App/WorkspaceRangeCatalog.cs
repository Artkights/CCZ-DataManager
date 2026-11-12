using DataManager.Images;
using RangeManager.App;

namespace DataManager.App;

/// <summary>A range definition exposed to business fields, independent of the E5 archive's entry count.</summary>
public sealed record WorkspaceRangeCatalogEntry(int Id, string Name, bool IsModified, bool IsManaged);

/// <summary>An immutable catalog generation. IDs are the actual, possibly sparse, session bindings.</summary>
public sealed record WorkspaceRangeCatalogSnapshot(
    string RootPath,
    long Revision,
    RangeKind Kind,
    bool Available,
    IReadOnlyList<WorkspaceRangeCatalogEntry> Entries,
    IReadOnlyList<int> Ids);

public sealed class WorkspaceRangeCatalogChangedEventArgs(bool reset, bool directoryChanged,
    IReadOnlyList<RangeKey> affectedKeys) : EventArgs
{
    public bool Reset { get; } = reset;
    public bool DirectoryChanged { get; } = directoryChanged;
    public IReadOnlyList<RangeKey> AffectedKeys { get; } = affectedKeys;
}

/// <summary>
/// Shares the current range draft with business pickers. The host binds and
/// mutates sessions on the UI thread; preview workers read owned snapshots only.
/// This object does not own the session or the resource adapter.
/// </summary>
public sealed class WorkspaceRangeCatalog : IDisposable
{
    private sealed record Generation(
        string RootPath,
        long Revision,
        bool Available,
        IReadOnlyDictionary<RangeKey, RangeBinding> Bindings,
        WorkspaceRangeCatalogSnapshot Attack,
        WorkspaceRangeCatalogSnapshot Pierce,
        CancellationToken CancellationToken);

    private readonly ResourceCatalogAdapter _resources;
    private readonly object _gate = new();
    private RangeEditSession? _session;
    private CancellationTokenSource _generationCancellation = new();
    private Generation _generation;
    private long _revision;
    private bool _disposed;
    private int _loadRevision = -1;
    private RangeEditSession? _publishedSession;

    public WorkspaceRangeCatalog(ResourceCatalogAdapter resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _generation = CreateGeneration(string.Empty, false, [], 0, _generationCancellation.Token);
    }

    public string RootPath { get { lock (_gate) return _generation.RootPath; } }
    public long Revision { get { lock (_gate) return _generation.Revision; } }
    public bool HasSession { get { lock (_gate) return _generation.Available; } }
    public event EventHandler? Changed;
    public event EventHandler<WorkspaceRangeCatalogChangedEventArgs>? DetailedChanged;

    /// <summary>
    /// Binds the host's current project. A missing session preserves disk-image
    /// preview access without presenting unverified image slots as definitions.
    /// </summary>
    public void Bind(string rootPath, RangeEditSession? session)
    {
        var root = string.IsNullOrWhiteSpace(rootPath) ? string.Empty : Path.GetFullPath(rootPath);
        if (session != null && !SameRoot(root, session.Game.RootPath))
            throw new ArgumentException("范围会话与当前项目目录不一致。", nameof(session));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_session, session) && SameRoot(_generation.RootPath, root)) return;
            if (_session != null) _session.Changed -= SessionChanged;
            _session = session;
            if (_session != null) _session.Changed += SessionChanged;
        }
        Publish(root, session);
    }

    public WorkspaceRangeCatalogSnapshot GetSnapshot(bool attack)
        => GetSnapshot(attack ? RangeKind.Attack : RangeKind.Pierce);

    public WorkspaceRangeCatalogSnapshot GetSnapshot(RangeKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return kind == RangeKind.Attack ? _generation.Attack : _generation.Pierce;
        }
    }

    /// <summary>
    /// Renders modified definitions from their frozen geometry. Unchanged and
    /// unknown IDs retain the existing disk-image preview path; an image alone
    /// never adds an ID to the definition catalog.
    /// The caller owns the returned bitmap. A superseded generation is cancelled.
    /// </summary>
    public Task<Bitmap?> DecodePreviewAsync(bool attack, int id, CancellationToken cancellationToken = default)
    {
        Generation generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = _generation;
        }
        return DecodePreviewAsync(generation, attack, id, cancellationToken);
    }

    private async Task<Bitmap?> DecodePreviewAsync(Generation generation, bool attack, int id,
        CancellationToken cancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.CancellationToken);
        var token = request.Token;
        token.ThrowIfCancellationRequested();
        if (generation.RootPath.Length == 0) return null;
        Bitmap? bitmap = null;
        try
        {
            var key = new RangeKey(attack ? RangeKind.Attack : RangeKind.Pierce, id);
            if (generation.Bindings.TryGetValue(key, out var binding) && binding.IsModified)
            {
                bitmap = await ImageManager.Core.ImageResourceCoordinator.Shared.DecodeAsync(workerToken =>
                {
                    workerToken.ThrowIfCancellationRequested();
                    using var tiles = FixedRangePixelTemplate.Create();
                    var context = RangePreviewContext.Default;
                    CellOffset? target = binding.Key.Kind == RangeKind.Pierce && binding.Center == RangeCenterMode.Target
                        ? context.Target : null;
                    return RangeBitmapRenderer.RenderIconFromTiles(binding.Key.Kind,
                        NativeRangeCatalog.Expand(binding, context).ToArray(), target, tiles);
                }, result => result.Dispose(), token).ConfigureAwait(false);
            }
            else
                bitmap = await _resources.DecodeRangeNativeFrameAsync(generation.RootPath, attack, id, token)
                    .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                generation.Bindings.TryGetValue(key, out var originalBinding);
                _generation.Bindings.TryGetValue(key, out var currentBinding);
                if (_disposed || !SameRoot(generation.RootPath, _generation.RootPath) ||
                    !ReferenceEquals(originalBinding, currentBinding))
                    throw new OperationCanceledException("范围目录已更新。", token);
            }
            var result = bitmap;
            bitmap = null;
            return result;
        }
        finally { bitmap?.Dispose(); }
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        RangeEditSession? session;
        string root;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _session)) return;
            session = _session;
            root = _generation.RootPath;
        }
        Publish(root, session);
    }

    private void Publish(string root, RangeEditSession? session)
    {
        // Clone on the owning UI thread before any asynchronous renderer can
        // observe a later edit, undo, save acceptance or session disposal.
        var bindings = session == null ? [] : RangeModelCopies.Clone(session.Bindings);
        CancellationTokenSource? previous = null;
        WorkspaceRangeCatalogChangedEventArgs change;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(session, _session)) return;
            var reset = !ReferenceEquals(session, _publishedSession) || !SameRoot(root, _generation.RootPath) || _generation.Available != (session != null) ||
                _loadRevision != (session?.LoadRevision ?? -1);
            var affected = new List<RangeKey>();
            var directoryChanged = false;
            for (var index = 0; index < bindings.Count; index++)
            {
                var binding = bindings[index];
                if (_generation.Bindings.TryGetValue(binding.Key, out var old))
                {
                    if (!reset && SameBinding(old, binding)) bindings[index] = old;
                    else affected.Add(binding.Key);
                }
                else { affected.Add(binding.Key); directoryChanged = true; }
            }
            var removed = _generation.Bindings.Keys.Except(bindings.Select(binding => binding.Key)).ToArray();
            affected.AddRange(removed);
            directoryChanged |= removed.Length != 0;
            if (reset)
            {
                previous = _generationCancellation;
                _generationCancellation = new CancellationTokenSource();
            }
            _loadRevision = session?.LoadRevision ?? -1;
            _publishedSession = session;
            change = new(reset, directoryChanged, affected.AsReadOnly());
            _generation = CreateGeneration(root, session != null, bindings, ++_revision, _generationCancellation.Token);
        }
        previous?.Cancel();
        previous?.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
        DetailedChanged?.Invoke(this, change);
    }

    private static Generation CreateGeneration(string root, bool available, IReadOnlyList<RangeBinding> bindings,
        long revision, CancellationToken cancellationToken)
    {
        WorkspaceRangeCatalogSnapshot Snapshot(RangeKind kind)
        {
            var entries = bindings.Where(binding => binding.Key.Kind == kind).OrderBy(binding => binding.Key.Id)
                .Select(binding => new WorkspaceRangeCatalogEntry(binding.Key.Id, binding.Name, binding.IsModified, binding.IsManaged))
                .ToArray();
            return new(root, revision, kind, available, Array.AsReadOnly(entries),
                Array.AsReadOnly(entries.Select(entry => entry.Id).ToArray()));
        }
        return new(root, revision, available, bindings.ToDictionary(binding => binding.Key),
            Snapshot(RangeKind.Attack), Snapshot(RangeKind.Pierce), cancellationToken);
    }

    private static bool SameRoot(string left, string right)
        => Path.TrimEndingDirectorySeparator(left).Equals(Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private static bool SameBinding(RangeBinding left, RangeBinding right)
        => left.Key == right.Key && left.Name == right.Name && left.Center == right.Center &&
           left.ExcludePrimaryTarget == right.ExcludePrimaryTarget && left.Editable == right.Editable &&
           left.ReadOnlyReason == right.ReadOnlyReason && left.Special == right.Special &&
           left.IsManaged == right.IsManaged && left.IsModified == right.IsModified &&
           left.NativePointer == right.NativePointer && left.Source == right.Source &&
           left.ContractId == right.ContractId && left.RuntimeCacheId == right.RuntimeCacheId &&
           left.PointerSlots.SequenceEqual(right.PointerSlots) && left.Definition.Identity == right.Definition.Identity &&
           left.Definition.Groups.Count == right.Definition.Groups.Count &&
           left.Definition.Groups.Zip(right.Definition.Groups).All(pair =>
               pair.First.Name == pair.Second.Name && pair.First.Geometry == pair.Second.Geometry &&
               pair.First.WeightMode == pair.Second.WeightMode && pair.First.ConstantWeight == pair.Second.ConstantWeight &&
               pair.First.EmissionOrder == pair.Second.EmissionOrder && pair.First.OrdinalStart == pair.Second.OrdinalStart &&
               pair.First.Generators.SequenceEqual(pair.Second.Generators));

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_session != null) _session.Changed -= SessionChanged;
            _session = null;
            cancellation = _generationCancellation;
            Changed = null;
            DetailedChanged = null;
        }
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
