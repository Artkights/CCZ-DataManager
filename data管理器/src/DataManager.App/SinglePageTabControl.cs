namespace DataManager.App;

/// <summary>Preserves the selected TabPage API while a single detail page fills the whole surface.</summary>
internal sealed class SinglePageTabControl : TabControl
{
    private bool _hideHeader;

    internal void RefreshHeader()
    {
        var hide = TabCount == 1;
        if (hide == _hideHeader) return;
        _hideHeader = hide;
        PerformLayout();
    }

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
