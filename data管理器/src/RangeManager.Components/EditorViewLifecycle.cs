using System.Windows.Forms;

namespace Ccz.ManagerUi;

/// <summary>Layout-only activation; it never binds, validates or commits editor input.</summary>
public interface IEditorViewLifecycle
{
    void ActivateView();
    void DeactivateView();
    void RequestViewLayout();
}

/// <summary>Coalesces inherited font, DPI and size notifications after native controls settle.</summary>
public sealed class LayoutScheduler : IDisposable
{
    private static readonly object RegistryGate = new();
    private static readonly List<WeakReference<LayoutScheduler>> Registry = [];
    public sealed record Diagnostic(string ControlPath, bool Active, bool Visible, bool Pending, long Passes);
    private readonly Control _owner;
    private readonly Action _arrange;
    private bool _active = true, _pending, _queued, _running, _disposed;
    private int _generation;
    private int _convergencePasses;

    public LayoutScheduler(Control owner, Action arrange)
    {
        _owner = owner;
        _arrange = arrange;
        lock (RegistryGate) Registry.Add(new(this));
        owner.HandleCreated += OnChanged;
        owner.HandleDestroyed += OnHandleDestroyed;
        owner.ClientSizeChanged += OnChanged;
        owner.FontChanged += OnChanged;
        owner.DpiChangedAfterParent += OnChanged;
        if (owner is Form form) form.DpiChanged += OnChanged;
        owner.VisibleChanged += OnVisibleChanged;
        owner.Disposed += OnDisposed;
        Request();
    }

    public long LayoutPassCount { get; private set; }
    public bool IsPending => _pending || _queued;

    /// <summary>A read-only UI-thread snapshot; weak references never retain closed editors.</summary>
    public static IReadOnlyList<Diagnostic> GetDiagnostics(Control root)
    {
        var result = new List<Diagnostic>();
        lock (RegistryGate)
        {
            for (var index = Registry.Count - 1; index >= 0; index--)
            {
                if (!Registry[index].TryGetTarget(out var scheduler) || scheduler._disposed)
                { Registry.RemoveAt(index); continue; }
                var names = new List<string>();
                for (Control? owner = scheduler._owner; owner != null; owner = owner.Parent)
                {
                    names.Add(owner.GetType().Name + ":" + owner.Name);
                    if (!ReferenceEquals(owner, root)) continue;
                    names.Reverse();
                    result.Add(new(string.Join('/', names), scheduler._active, scheduler._owner.Visible,
                        scheduler.IsPending, scheduler.LayoutPassCount));
                    break;
                }
            }
        }
        return result;
    }

    public void Activate()
    {
        if (_disposed) return;
        _active = true;
        Request();
    }

    public void Deactivate()
    {
        _active = false;
        _generation++;
        _queued = false;
        _pending = true;
    }

    public void Request()
    {
        if (_disposed) return;
        if (!_running && !_queued) _convergencePasses = 0;
        _pending = true;
        Queue();
    }

    private void Queue()
    {
        if (!_active || _running || _queued || !_owner.Visible || !_owner.IsHandleCreated || _owner.IsDisposed || _owner.Disposing) return;
        _queued = true;
        var generation = _generation;
        try
        {
            _owner.BeginInvoke((MethodInvoker)(() => Drain(generation)));
        }
        catch (InvalidOperationException) when (!_owner.IsHandleCreated || _owner.IsDisposed || _owner.Disposing)
        {
            _queued = false;
        }
    }

    private void Drain(int generation)
    {
        if (_disposed || generation != _generation) return;
        _queued = false;
        if (!_active || !_owner.Visible || !_owner.IsHandleCreated || _owner.IsDisposed || _owner.Disposing) return;
        _running = true;
        try
        {
            _pending = false;
            _arrange();
            LayoutPassCount++;
        }
        finally { _running = false; }
        // Font propagation may post native height updates. Run the follow-up
        // on the next message, with a strict bound on convergence work.
        if (_pending && ++_convergencePasses < 3) Queue();
        else _pending = false;
    }

    private void OnChanged(object? sender, EventArgs e) => Request();
    private void OnVisibleChanged(object? sender, EventArgs e) { if (_owner.Visible) Request(); }
    private void OnHandleDestroyed(object? sender, EventArgs e) { _generation++; _queued = false; _pending = true; }
    private void OnDisposed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _owner.HandleCreated -= OnChanged;
        _owner.HandleDestroyed -= OnHandleDestroyed;
        _owner.ClientSizeChanged -= OnChanged;
        _owner.FontChanged -= OnChanged;
        _owner.DpiChangedAfterParent -= OnChanged;
        if (_owner is Form form) form.DpiChanged -= OnChanged;
        _owner.VisibleChanged -= OnVisibleChanged;
        _owner.Disposed -= OnDisposed;
    }
}
