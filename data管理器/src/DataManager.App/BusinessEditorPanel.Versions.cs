using System.Runtime.CompilerServices;
using DataManager.Core.Domain;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private string? _stableBindingVersion;
    private long _resourceBindingRevision = -1;
    private bool _resourceRefreshPending;
    private readonly List<(RangeManager.App.RangeKind Kind, Action<WorkspaceRangeCatalogChangedEventArgs> Refresh)> _rangePickerRefreshers = [];

    public void RefreshRangePickers(WorkspaceRangeCatalogChangedEventArgs change)
    {
        if (_releaseCompleted || IsDisposed || Disposing) return;
        if (!Visible) { _resourceRefreshPending = true; return; }
        foreach (var entry in _rangePickerRefreshers.ToArray())
            if (change.Reset || change.AffectedKeys.Any(key => key.Kind == entry.Kind))
                entry.Refresh(change);
    }

    private string BindingVersion() => string.Join('|', _workspace.Documents.Values.Select(document =>
        $"{document.Definition.Key}:{RuntimeHelpers.GetHashCode(document)}:{document.Revision}"));

    private bool CanReuseStableBinding(BusinessPageKind kind, int id)
        => !_binding && !_releaseCompleted && kind != BusinessPageKind.ExtendData &&
           _compactLayoutSignature != null && _kind == kind && _id == id &&
           _stableBindingVersion == BindingVersion();

    private void RecordStableBinding()
    {
        _stableBindingVersion = BindingVersion();
        _resourceBindingRevision = _resources.Revision;
    }

    private void RefreshChangedResourceBinding()
    {
        // Reusing the entity controls must not hide an explicit resource
        // invalidation. Refresh candidates without rebinding field text, then
        // let the HWND generation-aware queue resume any completed read.
        if (_resourceBindingRevision != _resources.Revision)
        {
            RefreshResourcePickers();
            _resourceBindingRevision = _resources.Revision;
        }
        QueueResourceOptionUpdates();
    }

    private void ApplyDeferredResourceRefresh()
    {
        if (_resourceRefreshPending && Visible) RefreshResourcePickers();
    }
}
