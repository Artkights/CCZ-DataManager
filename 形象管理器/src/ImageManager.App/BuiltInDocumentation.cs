using System.Text;

namespace ImageManager.App;

/// <summary>The help stays available when the application is distributed as one executable.</summary>
public static class BuiltInDocumentation
{
    private const string ResourceName = "Ccz.ImageEditor.BuiltInHelp.md";
    public static string ReadText()
    {
        using var stream = typeof(BuiltInDocumentation).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("程序内置使用说明缺失，请使用完整发布版本。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static void Show(IWin32Window? owner)
    {
        using var dialog = new Form
        {
            Text = "Data 与形象管理器使用说明", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(920, 720), MinimumSize = new Size(600, 400), MinimizeBox = false
        };
        var content = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, WordWrap = true, DetectUrls = true,
            BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 10.5f), Text = ReadText()
        };
        var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft
        };
        footer.Controls.Add(close);
        dialog.Controls.Add(content); dialog.Controls.Add(footer);
        dialog.AcceptButton = close; dialog.CancelButton = close;
        dialog.Shown += (_, _) => { content.SelectionStart = 0; content.SelectionLength = 0; close.Focus(); };
        dialog.ShowDialog(owner);
    }
}
