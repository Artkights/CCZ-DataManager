using System.Drawing;
using System.Windows.Forms;

namespace Ccz.ManagerUi;

/// <summary>The native WinForms shell shared by the Data and Range managers.</summary>
internal static class ManagerUiStyle
{
    public static Color ToolbarColor => Color.FromArgb(245, 247, 250);

    public static FlowLayoutPanel CreateToolbar() => new()
    {
        Dock = DockStyle.Top,
        Height = 38,
        Padding = new Padding(8, 4, 8, 3),
        WrapContents = false,
        AutoScroll = true,
        BackColor = ToolbarColor
    };

    public static Button CreateButton(string text, EventHandler? clicked = null)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        if (clicked != null) button.Click += clicked;
        return button;
    }

    public static void ConfigureTabs(TabControl tabs)
    {
        tabs.Dock = DockStyle.Fill;
        tabs.Multiline = false;
        tabs.Appearance = TabAppearance.FlatButtons;
        tabs.ItemSize = new Size(120, 28);
        tabs.SizeMode = TabSizeMode.FillToRight;
    }

    public static void ConfigureEntityList(ListBox list)
    {
        list.Dock = DockStyle.Fill;
        list.Font = new Font(list.Font.FontFamily, 10.5f);
        list.SelectionMode = SelectionMode.One;
        list.HorizontalScrollbar = true;
        list.IntegralHeight = false;
    }

    public static void FitShellText(Control owner, FlowLayoutPanel toolbar, Control header, TabControl tabs)
    {
        var buttons = toolbar.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
        var preferred = buttons.Select(control => (Control: control, Size: control.PreferredSize)).ToArray();
        var contentWidth = preferred.Sum(item => item.Size.Width + item.Control.Margin.Horizontal);
        var scrollHeight = contentWidth > Math.Max(0, toolbar.Width - toolbar.Padding.Horizontal)
            ? SystemInformation.HorizontalScrollBarHeight : 0;
        var toolbarHeight = Math.Max(38 * owner.DeviceDpi / 96,
            preferred.Select(item => item.Size.Height + item.Control.Margin.Vertical).DefaultIfEmpty(28).Max()
                + toolbar.Padding.Vertical + scrollHeight);
        if (toolbar.Height != toolbarHeight) toolbar.Height = toolbarHeight;
        var itemHeight = Math.Max(28 * owner.DeviceDpi / 96, tabs.Font.Height + 10);
        var headerHeight = Math.Max(34 * owner.DeviceDpi / 96, itemHeight + 8 * owner.DeviceDpi / 96);
        if (header.Height != headerHeight) header.Height = headerHeight;
        if (tabs.ItemSize.Height != itemHeight) tabs.ItemSize = new Size(tabs.ItemSize.Width, itemHeight);
    }
}
