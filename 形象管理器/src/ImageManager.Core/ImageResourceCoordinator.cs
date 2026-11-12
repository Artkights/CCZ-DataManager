using System.Threading.Channels;

using System.Diagnostics;

namespace ImageManager.Core;

public enum DecodeRequestPriority { VisiblePreview, Thumbnail, Background }

/// <summary>One process-wide budget for disposable preview pixels and expensive decode work.
/// Editing bitmaps are deliberately not enrolled here. Every preview keeps its original ownership.</summary>
public sealed class ImageResourceCoordinator : IDisposable
{
    public const long DefaultPixelBudget = 96L * 1024 * 1024;
    public const int DefaultQueueCapacity = 64;
    public const int TimingSampleCapacity = 256;
    public static ImageResourceCoordinator Shared { get; } = new();
    private readonly object _gate = new();
    private readonly LinkedList<PixelLease> _pixels = new();
    private readonly Dictionary<object, CancellationTokenSource> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<DecodeJob> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Task[] _workers;
    private readonly int _queueCapacity;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AsyncLocal<int> _decodeDepth = new();
    private readonly TimingWindow _queueWait = new(TimingSampleCapacity);
    private readonly TimingWindow _decodeExecution = new(TimingSampleCapacity);
    private long _pixelBytes, _peakPixels, _completed, _cancelled;
    private int _active, _queued;
    private bool _disposed;

    public ImageResourceCoordinator(long pixelBudget = DefaultPixelBudget, int concurrency = 2, int queueCapacity = DefaultQueueCapacity)
    {
        if (pixelBudget <= 0 || concurrency <= 0 || queueCapacity <= 0) throw new ArgumentOutOfRangeException();
        PixelBudget = pixelBudget;
        _queueCapacity = queueCapacity;
        _workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(DecodeWorkerAsync)).ToArray();
    }

    public long PixelBudget { get; }
    public ImageResourceStatistics Statistics
    {
        get { lock (_gate) return new(_pixelBytes, _peakPixels, PixelBudget, _pixels.Count,
            _pixels.Where(item => item.ReclaimPending).Sum(item => item.Bytes), Volatile.Read(ref _active),
            Volatile.Read(ref _queued), 0, Interlocked.Read(ref _completed), Interlocked.Read(ref _cancelled))
            {
                CategoryBytes = _pixels.GroupBy(item => item.Category).ToDictionary(group => group.Key, group => group.Sum(item => item.Bytes)),
                QueueWait = _queueWait.Snapshot(), DecodeExecution = _decodeExecution.Snapshot()
            }; }
    }

    /// <summary>Register after assigning the owned pixel set; call TrimPixels after publishing its lease.
    /// Reclaim must detach UI references before disposing, and must dispose this lease on completion.</summary>
    public PixelLease RegisterPixels(long bytes, string category, Action reclaim)
    {
        ArgumentNullException.ThrowIfNull(reclaim);
        var lease = new PixelLease(this, Math.Max(0, bytes), category, reclaim);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lease.Node = _pixels.AddFirst(lease);
            _pixelBytes += lease.Bytes;
            _peakPixels = Math.Max(_peakPixels, _pixelBytes);
        }
        return lease;
    }

    public void TrimPixels()
    {
        List<Action> reclaim = [];
        lock (_gate)
        {
            var scheduledBytes = _pixels.Where(item => item.ReclaimPending).Sum(item => item.Bytes);
            for (var node = _pixels.Last; node != null && _pixelBytes - scheduledBytes > PixelBudget; node = node.Previous)
            {
                var item = node.Value;
                if (item.ReclaimPending) continue;
                item.ReclaimPending = true;
                scheduledBytes += item.Bytes;
                reclaim.Add(item.Reclaim);
            }
        }
        // Never hold the budget lock while an owner takes a cache lock or posts to its UI context.
        foreach (var callback in reclaim) { try { callback(); } catch (ObjectDisposedException) { } }
    }

    private void Release(PixelLease lease)
    {
        lock (_gate)
        {
            if (lease.Node == null) return;
            _pixels.Remove(lease.Node); lease.Node = null; _pixelBytes -= lease.Bytes;
        }
    }
    private void Touch(PixelLease lease)
    {
        lock (_gate) if (lease.Node != null && !lease.ReclaimPending)
        { _pixels.Remove(lease.Node); lease.Node = _pixels.AddFirst(lease); }
    }

    /// <summary>Only CPU/codec leaves belong here. Nested legacy wrappers execute within their existing slot,
    /// preventing the two outer requests / two inner requests deadlock during migration.</summary>
    public async Task<T> DecodeAsync<T>(Func<CancellationToken, T> decode, Action<T> release,
        CancellationToken cancellationToken = default, object? latestOwner = null,
        DecodeRequestPriority priority = DecodeRequestPriority.VisiblePreview)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_decodeDepth.Value != 0)
        {
            var nested = decode(cancellationToken);
            if (!cancellationToken.IsCancellationRequested) return nested;
            release(nested); cancellationToken.ThrowIfCancellationRequested();
        }
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            if (latestOwner != null)
            {
                if (_owners.Remove(latestOwner, out var old)) old.Cancel();
                _owners[latestOwner] = linked;
            }
        }
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new DecodeJob(token => decode(token), value => release((T)value!), linked.Token, completion, priority);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Waiting writers also retain closures and input bytes. Bound
                // all pending work, first removing obsolete/hidden requests.
                for (var node = _queue.First; node != null;)
                {
                    var next = node.Next;
                    if (node.Value.Token.IsCancellationRequested) CancelQueued(node);
                    node = next;
                }
                if (_queue.Count >= _queueCapacity)
                {
                    var victim = _queue.First!;
                    for (var node = victim.Next; node != null; node = node.Next)
                        if (node.Value.Priority > victim.Value.Priority) victim = node;
                    if (victim.Value.Priority < priority)
                    {
                        completion.TrySetCanceled(new CancellationToken(true));
                        Interlocked.Increment(ref _cancelled);
                    }
                    else CancelQueued(victim);
                }
                if (!completion.Task.IsCompleted)
                {
                    job.QueuedAt = Stopwatch.GetTimestamp();
                    _queue.AddLast(job); _queued = _queue.Count;
                    _available.Release();
                }
            }
            return (T)(await completion.Task.ConfigureAwait(false))!;
        }
        finally
        {
            lock (_gate)
                if (latestOwner != null && _owners.TryGetValue(latestOwner, out var current) && ReferenceEquals(current, linked))
                    _owners.Remove(latestOwner);
            linked.Dispose();
        }
    }

    private async Task DecodeWorkerAsync()
    {
        while (true)
        {
            await _available.WaitAsync().ConfigureAwait(false);
            DecodeJob job;
            lock (_gate)
            {
                if (_queue.Count == 0) { if (_disposed) return; continue; }
                var selected = _queue.First!;
                for (var node = selected.Next; node != null; node = node.Next)
                    if (node.Value.Priority < selected.Value.Priority) selected = node;
                job = selected.Value; _queue.Remove(selected); _queued = _queue.Count;
            }
            if (job.Token.IsCancellationRequested)
            { Interlocked.Increment(ref _cancelled); job.Completion.TrySetCanceled(job.Token); continue; }
            Interlocked.Increment(ref _active);
            var acquiredAt = Stopwatch.GetTimestamp();
            lock (_gate) _queueWait.Record(Stopwatch.GetElapsedTime(job.QueuedAt, acquiredAt).TotalMilliseconds);
            try
            {
                _decodeDepth.Value++;
                object? result;
                var executionStarted = Stopwatch.GetTimestamp();
                try { result = job.Decode(job.Token); }
                finally
                {
                    var executionMs = Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds;
                    lock (_gate) _decodeExecution.Record(executionMs);
                }
                if (job.Token.IsCancellationRequested)
                { job.Release(result); job.Completion.TrySetCanceled(job.Token); Interlocked.Increment(ref _cancelled); }
                else { job.Completion.TrySetResult(result); Interlocked.Increment(ref _completed); }
            }
            catch (OperationCanceledException) { job.Completion.TrySetCanceled(); Interlocked.Increment(ref _cancelled); }
            catch (Exception error) { job.Completion.TrySetException(error); }
            finally { _decodeDepth.Value--; Interlocked.Decrement(ref _active); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_queue.First != null) CancelQueued(_queue.First);
        }
        _shutdown.Cancel();
        _available.Release(_workers.Length);
        _ = Task.WhenAll(_workers).ContinueWith(_ => { _available.Dispose(); _shutdown.Dispose(); }, TaskScheduler.Default);
    }

    private void CancelQueued(LinkedListNode<DecodeJob> node)
    {
        _queue.Remove(node); _queued = _queue.Count;
        node.Value.Completion.TrySetCanceled(new CancellationToken(true));
        Interlocked.Increment(ref _cancelled);
    }

    public sealed class PixelLease : IDisposable
    {
        private readonly ImageResourceCoordinator _owner;
        internal PixelLease(ImageResourceCoordinator owner, long bytes, string category, Action reclaim)
            { _owner = owner; Bytes = bytes; Category = category; Reclaim = reclaim; }
        public long Bytes { get; }
        public string Category { get; }
        internal Action Reclaim { get; }
        internal bool ReclaimPending { get; set; }
        internal LinkedListNode<PixelLease>? Node { get; set; }
        public void Touch() => _owner.Touch(this);
        public void Dispose() => _owner.Release(this);
    }
    private sealed record DecodeJob(Func<CancellationToken, object?> Decode, Action<object?> Release,
        CancellationToken Token, TaskCompletionSource<object?> Completion, DecodeRequestPriority Priority)
    { public long QueuedAt { get; set; } }

    // Both access paths hold _gate. Retain only the most recent fixed-size
    // window, plus scalar lifetime count/maximum; no per-request objects remain.
    private sealed class TimingWindow(int capacity)
    {
        private readonly double[] _milliseconds = new double[capacity];
        private int _next, _count;
        private long _total;
        private double _maximum;
        public void Record(double milliseconds)
        {
            _milliseconds[_next] = milliseconds;
            _next = (_next + 1) % _milliseconds.Length;
            _count = Math.Min(_count + 1, _milliseconds.Length);
            _total++;
            _maximum = Math.Max(_maximum, milliseconds);
        }
        public DecodeTimingStatistics Snapshot()
        {
            if (_count == 0) return new(0, 0, capacity, 0, 0);
            var sorted = _milliseconds.AsSpan(0, _count).ToArray();
            Array.Sort(sorted);
            return new(_total, _count, capacity, sorted[(int)Math.Ceiling(_count * .95) - 1], _maximum);
        }
    }
}

public sealed record ImageResourceStatistics(long PixelBytes, long PeakPixelBytes, long PixelBudget,
    int PixelSets, long PendingReclaimBytes, int ActiveDecodes, int QueuedDecodes, int WaitingAdmissions,
    long CompletedDecodes, long CancelledDecodes)
{
    public IReadOnlyDictionary<string, long> CategoryBytes { get; init; } = new Dictionary<string, long>();
    /// <summary>Admission to slot acquisition, only for jobs that begin decoding.
    /// Rejected, evicted and cancelled-before-start requests do not create samples.</summary>
    public DecodeTimingStatistics QueueWait { get; init; } = new(0, 0, ImageResourceCoordinator.TimingSampleCapacity, 0, 0);
    /// <summary>Time inside the decoder delegate, including failed/cancelled execution.
    /// Excludes result release/publication; nested wrappers share their outer sample.</summary>
    public DecodeTimingStatistics DecodeExecution { get; init; } = new(0, 0, ImageResourceCoordinator.TimingSampleCapacity, 0, 0);
}

/// <summary>P95 uses the nearest rank of the most recent RetainedSamples (at
/// most SampleCapacity). TotalSamples and MaximumMs cover the coordinator lifetime.</summary>
public sealed record DecodeTimingStatistics(long TotalSamples, int RetainedSamples, int SampleCapacity,
    double P95Ms, double MaximumMs);
