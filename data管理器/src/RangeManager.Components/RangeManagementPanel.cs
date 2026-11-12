using Ccz.ManagerUi;

namespace RangeManager.App;

/// <summary>The three existing range editors, without a separate file or history shell.</summary>
public sealed class RangeManagementPanel : UserControl, IEditorViewLifecycle
{
    private readonly LayoutScheduler _layouts;
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    // LayoutScheduler starts active; keep the parent lifecycle independent
    // from which child currently owns the visible editor.
    private bool _viewActive = true;
    private IEditorViewLifecycle? _activeEditor;
    public void ActivateView()
    {
        if (!_viewActive)
        {
            _viewActive = true;
            _layouts.Activate();
        }
        if (ReferenceEquals(_activeEditor, SelectedEditor)) return;
        _activeEditor = SelectedEditor;
        _activeEditor.ActivateView();
    }
    public void DeactivateView()
    {
        if (!_viewActive) return;
        _viewActive = false;
        _layouts.Deactivate();
        AttackEditor.DeactivateView(); PierceEditor.DeactivateView(); ExtensionEditor.DeactivateView();
        _activeEditor = null;
    }
    public void RequestViewLayout() { _layouts.Request(); SelectedEditor.RequestViewLayout(); }
    private IEditorViewLifecycle SelectedEditor => Tabs.SelectedIndex switch
        { 1 => PierceEditor, 2 => ExtensionEditor, _ => AttackEditor };
    private RangeEditSession? _session;
    public TabControl Tabs { get; } = new() { Name = "RangePages" };
    public RangeEditorPanel AttackEditor { get; } = new(RangeKind.Attack);
    public RangeEditorPanel PierceEditor { get; } = new(RangeKind.Pierce) { Visible = false };
    public RangeExtensionPanel ExtensionEditor { get; } = new() { Visible = false };

    public RangeManagementPanel()
    {
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Inherit;
        var header = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 1, 8, 1) };
        ManagerUiStyle.ConfigureTabs(Tabs);
        Tabs.TabPages.Add("攻击范围"); Tabs.TabPages.Add("穿透范围"); Tabs.TabPages.Add("拓展范围");
        Tabs.SelectedIndexChanged += (_, _) => ShowSelectedPage();
        header.Controls.Add(Tabs);
        _body.Controls.AddRange([AttackEditor, PierceEditor, ExtensionEditor]);
        Controls.Add(_body); Controls.Add(header);
        void FitHeader()
        {
            var changed = false;
            var itemHeight = Math.Max(28 * DeviceDpi / 96, Font.Height + 10);
            if (Tabs.ItemSize.Height != itemHeight)
            { Tabs.ItemSize = new Size(Tabs.ItemSize.Width, itemHeight); changed = true; }
            var height = Math.Max(34 * DeviceDpi / 96, itemHeight + 8 * DeviceDpi / 96);
            if (header.Height != height) { header.Height = height; changed = true; }
            if (changed) SelectedEditor.RequestViewLayout();
        }
        _layouts = new LayoutScheduler(this, FitHeader);
        VisibleChanged += (_, _) => { if (Visible) ActivateView(); else DeactivateView(); };
    }

    private void ShowSelectedPage()
    {
        _body.SuspendLayout();
        try
        {
            _activeEditor?.DeactivateView();
            _activeEditor = null;
            AttackEditor.Visible = Tabs.SelectedIndex == 0;
            PierceEditor.Visible = Tabs.SelectedIndex == 1;
            ExtensionEditor.Visible = Tabs.SelectedIndex == 2;
            (Tabs.SelectedIndex switch { 1 => (Control)PierceEditor, 2 => ExtensionEditor, _ => AttackEditor }).BringToFront();
        }
        finally { _body.ResumeLayout(true); }
        if (Visible) ActivateView();
    }

    public void Bind(RangeEditSession? session)
    {
        _session = session;
        AttackEditor.Bind(session); PierceEditor.Bind(session); ExtensionEditor.Bind(session);
    }

    /// <summary>Shows the range that blocked the latest save without changing its draft.</summary>
    public bool TryFocusValidationError(string? message = null)
    {
        if (_session?.LastValidationFailure is not { } failure ||
            !_session.Bindings.Any(binding => binding.Key == failure.Key)) return false;
        Tabs.SelectedIndex = failure.Key.Kind == RangeKind.Pierce ? 1 : 0;
        var editor = failure.Key.Kind == RangeKind.Pierce ? PierceEditor : AttackEditor;
        return editor.TryFocusValidationError(failure.Key,
            string.IsNullOrWhiteSpace(failure.Message) ? message : failure.Message, failure.GroupIndex);
    }

    public bool TryCommitPendingEdits(out string error)
    {
        // Generator text is already a session draft, including invalid text.
        // Its validation belongs to save so hosts can still discard or undo it.
        return ExtensionEditor.TryCommitPendingEdits(out error);
    }

    public void RefreshBindings()
    {
        AttackEditor.RefreshBindings(); PierceEditor.RefreshBindings(); ExtensionEditor.RefreshMapping();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Bind(null);
        base.Dispose(disposing);
    }
}
