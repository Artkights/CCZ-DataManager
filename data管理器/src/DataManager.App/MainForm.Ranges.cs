using DataManager.Core.Binary;
using DataManager.Core.Editing;
using RangeManager.App;

namespace DataManager.App;

public sealed partial class MainForm
{
    private const string RangeTabKey = "__ranges";
    private readonly Panel _rangeHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly RangeManagementPanel _rangeManagementPanel = new() { Dock = DockStyle.Fill };
    private readonly Label _rangeWarning = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Firebrick,
        Padding = new Padding(8), Visible = false
    };
    private RangeEditSession? _rangeSession;
    private RangeWorkspaceParticipant? _rangeParticipant;
    private ProjectWorkspace? _rangeWorkspace;
    private string? _rangeLoadError;
    private int _rangeLoadRevision;
    private readonly WorkspaceRangeCatalog _rangeCatalog;

    private bool IsRangeTab => _moduleTabs.SelectedTab?.Tag as string == RangeTabKey;

    private void InitializeRangeManagement()
    {
        _rangeCatalog.DetailedChanged += RangeCatalogChanged;
        _rangeHost.Controls.Add(_rangeManagementPanel);
        _rangeHost.Controls.Add(_rangeWarning);
        _rangeManagementPanel.Bind(null);
    }

    private void LoadRangeWorkspace()
    {
        ReleaseRangeWorkspace();
        _rangeLoadError = null;
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        _resourceAdapter?.SetActiveRoot(_workspace.RootPath);
        RangeEditSession? next = null;
        try
        {
            // The range PE reader clones its input. Reuse the workspace's
            // immutable saved image so every editor sees the same open-time
            // bytes; the shared save path still checks the live disk baseline.
            var executable = Path.GetFullPath(ProjectFileLocator.Resolve(_workspace.RootPath, "Ekd5.exe"));
            var sources = _workspace.Documents.Values.Where(document =>
                Path.GetFullPath(document.FilePath).Equals(executable, StringComparison.OrdinalIgnoreCase)).ToArray();
            var source = sources.FirstOrDefault();
            var bytes = source?.OriginalFileBytes ?? File.ReadAllBytes(executable);
            var hash = source?.OriginalFileHash ?? HashService.Sha256(bytes);
            if (sources.Any(document => !document.OriginalFileHash.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("工作区的 Ekd5.exe 读取基线不一致，请重新打开项目。");
            next = new RangeEditSession(_workspace.RootPath, bytes, recoverStandaloneTransaction: false);
            _rangeSession = next;
            _rangeWorkspace = _workspace;
            _rangeParticipant = new RangeWorkspaceParticipant(next, _workspace.Commands);
            _workspace.RegisterDraftParticipant(_rangeParticipant);
            _rangeLoadRevision = next.LoadRevision;
            next.Changed += RangeSessionChanged;
            _rangeCatalog.Bind(_workspace.RootPath, next);
            _rangeManagementPanel.Bind(next);
        }
        catch (Exception ex)
        {
            ReleaseRangeWorkspace();
            _rangeLoadError = "范围管理暂不可用：" + ex.Message;
        }
        UpdateRangeWarning();
    }

    private void ReleaseRangeWorkspace()
    {
        _rangeCatalog.Bind(string.Empty, null);
        if (!_rangeManagementPanel.IsDisposed) _rangeManagementPanel.Bind(null);
        if (_rangeParticipant != null)
        {
            _rangeWorkspace?.UnregisterDraftParticipant(_rangeParticipant);
            _rangeParticipant.Dispose();
            _rangeParticipant = null;
        }
        if (_rangeSession != null)
        {
            _rangeSession.Changed -= RangeSessionChanged;
            _rangeSession.Dispose();
            _rangeSession = null;
        }
        _rangeWorkspace = null;
    }

    private void RangeSessionChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing) return;
        if (_rangeSession != null && _rangeLoadRevision != _rangeSession.LoadRevision)
        {
            _rangeLoadRevision = _rangeSession.LoadRevision;
            _resourceAdapter?.Invalidate(_workspace.RootPath);
        }
        RefreshDirtyState();
        if (IsRangeTab) _diagnosticBox.Text = _rangeSession?.Diagnostics ?? _rangeLoadError ?? string.Empty;
    }

    private void RangeCatalogChanged(object? sender, WorkspaceRangeCatalogChangedEventArgs e)
    {
        if (_ownedResourcesDisposed || IsDisposed || Disposing) return;
        DataManager.Core.Domain.WorkspaceReadCache.For(_workspace).InvalidateExternal();
        foreach (var editor in _businessEditors.Values)
            if (!editor.IsDisposed) editor.RefreshRangePickers(e);
    }

    private void FocusRangeValidationFailure(string message)
    {
        if (_rangeSession?.LastValidationFailureKey == null) return;
        var page = _moduleTabs.TabPages.Cast<TabPage>().FirstOrDefault(tab => Equals(tab.Tag, RangeTabKey));
        if (page == null) return;
        _moduleTabs.SelectedTab = page;
        _rangeManagementPanel.TryFocusValidationError(message);
    }

    private void ShowRangeManagementView()
    {
        _businessKind = null;
        _resourceModule = false;
        _currentModule = null;
        _currentDocument = null;
        _businessEditor.Visible = false;
        _imageEditor.Visible = false;
        _grid.Visible = false;
        _gridBudget.Visible = false;
        _historyFilter.Visible = false;
        _quickLookup.Visible = false;
        _entityList.Items.Clear();
        _tableSelector.Items.Clear();
        _tableSelector.Enabled = false;
        if (_navigationSplit != null) _navigationSplit.Panel1Collapsed = true;
        _rangeHost.Visible = true;
        _rangeHost.BringToFront();
        _rangeManagementPanel.ActivateView();
        UpdateRangeWarning();
        _diagnosticBox.Text = _rangeSession?.Diagnostics ?? _rangeLoadError ?? "尚未打开项目。";
        SetStatus(_rangeLoadError ?? "攻击范围、穿透范围和拓展范围共用当前项目；保存时统一写入 EXE 与对应 E5 图片。",
            _rangeLoadError != null);
    }

    private void UpdateRangeWarning()
    {
        _rangeWarning.Text = _rangeLoadError ?? string.Empty;
        _rangeWarning.Visible = _rangeLoadError != null;
    }

    // Clean sessions must observe writes performed elsewhere in this same
    // application. Dirty sessions retain their original SHA and reject overlap.
    private void RefreshRangeAfterInternalWrite()
    {
        if (_rangeSession == null || _rangeSession.IsDirty) return;
        try
        {
            if (_rangeSession.MatchesDiskBaselines()) return;
            _rangeSession.ReloadFromDisk();
            _rangeLoadError = null;
            _rangeManagementPanel.RefreshBindings();
        }
        catch (Exception ex) { _rangeLoadError = "范围管理重新读取失败：" + ex.Message; }
        UpdateRangeWarning();
    }

    private void ReloadRangeAfterHistory()
    {
        if (_rangeSession == null) { LoadRangeWorkspace(); return; }
        RefreshRangeAfterInternalWrite();
        _rangeManagementPanel.RefreshBindings();
        UpdateRangeWarning();
    }

    internal RangeManagementPanel RangeHostForSmokeTest => _rangeManagementPanel;
    internal RangeEditSession? RangeSessionForSmokeTest => _rangeSession;
    internal bool RangeNavigationCollapsedForSmokeTest => _navigationSplit?.Panel1Collapsed == true;
    internal void RedoDraftForSmokeTest() => DraftRedo();
    internal void UndoSavedForSmokeTest() => HistoryUndo();
    internal void RedoSavedForSmokeTest() => HistoryRedo();
}
