using Ccz.ManagerUi;

namespace DataManager.App;

/// <summary>Preserves the selected TabPage API while a single detail page fills the whole surface.</summary>
internal sealed class SinglePageTabControl : TabControl
{
    private bool _hideHeader;
    private readonly LayoutScheduler _layout;

    public SinglePageTabControl() => _layout = new LayoutScheduler(this, ArrangePage);

    internal void RefreshHeader()
    {
        var hide = TabCount == 1;
        _hideHeader = hide;
        _layout.Request();
    }

    private void ArrangePage()
    {
        _hideHeader = TabCount == 1;
        PerformLayout();
        if (SelectedTab is { } page && page.Bounds != DisplayRectangle)
            page.Bounds = DisplayRectangle;
    }

    protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); RefreshHeader(); }
    protected override void OnControlRemoved(ControlEventArgs e) { base.OnControlRemoved(e); RefreshHeader(); }

    public override Rectangle DisplayRectangle => _hideHeader ? ClientRectangle : base.DisplayRectangle;

    protected override void WndProc(ref Message message)
    {
        const int adjustRectangle = 0x1328;
        if (_hideHeader && message.Msg == adjustRectangle)
        {
            message.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref message);
    }
}
