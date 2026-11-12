using DataManager.Core.Domain;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private string? _stableBindingVersion;
    private long _resourceBindingRevision = -1;
    private bool _resourceRefreshPending;
    private string? _bindingDependencySchema;
    private IReadOnlyList<RecordQueryDependency> _bindingDependencies = [];
    private readonly List<(RangeManager.App.RangeKind Kind, Action<WorkspaceRangeCatalogChangedEventArgs> Refresh)> _rangePickerRefreshers = [];

    public void RefreshRangePickers(WorkspaceRangeCatalogChangedEventArgs change)
    {
        if (_releaseCompleted || IsDisposed || Disposing) return;
        if (!Visible) { _resourceRefreshPending = true; return; }
        foreach (var entry in _rangePickerRefreshers.ToArray())
            if (change.Reset || change.AffectedKeys.Any(key => key.Kind == entry.Kind))
                entry.Refresh(change);
    }

    private string BindingVersion()
    {
        var schema = $"{_kind}:{_workspace.Documents.Count}:" + ReadCache.GetSchemaVersion(
            _compactTableKeys.Select(key => new RecordQueryDependency(key)));
        if (_bindingDependencySchema != schema)
        {
            var dependencies = new HashSet<RecordQueryDependency>();
            foreach (var key in _compactTableKeys)
            {
                dependencies.Add(new(key));
                if (!_workspace.Documents.TryGetValue(key, out var document)) continue;
                foreach (var field in document.Definition.Fields)
                    dependencies.UnionWith(BusinessFieldDependencies([key], field.Name));
            }
            if (_kind is BusinessPageKind.Items or BusinessPageKind.Strategies)
                dependencies.UnionWith(ReadCache.GetNavigationDependencies(["detailed-jobs"]));
            dependencies.Add(new("$external"));
            _bindingDependencies = dependencies.ToArray();
            _bindingDependencySchema = schema;
        }
        return schema + ReadCache.GetVersion(_bindingDependencies);
    }

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
