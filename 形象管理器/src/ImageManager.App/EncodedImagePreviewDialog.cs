using ImageManager.Core;

namespace ImageManager.App;

/// <summary>Shows the decoded candidate rather than the pre-encoding editor canvas.</summary>
public sealed class EncodedImagePreviewDialog : Form
{
    private readonly PictureBox _picture;

    public EncodedImagePreviewDialog(Bitmap encodedImage, string title)
    {
        Text = "确认编码后图片 - " + title;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1100, 800);
        ClientSize = new Size(Math.Min(1000, (int)(work.Width * .9)), Math.Min(760, (int)(work.Height * .9)));
        MinimumSize = new Size(580, 420);
        var note = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12), MaximumSize = new Size(900, 0),
            Text = $"下面显示的是 JPEG 编码后、实际将保存的画面（{encodedImage.Width}×{encodedImage.Height}）。请检查压缩后的颜色和细节，再确认写回。"
        };
        _picture = new PictureBox
        {
            Name = "EncodedImagePreview", Image = BmpPayloadService.CopyArgb(encodedImage), SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(37, 39, 42)
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10), FlowDirection = FlowDirection.RightToLeft };
        var accept = new Button { Name = "ConfirmEncodedImage", Text = "确认保存此画面", AutoSize = true, MinimumSize = new Size(130, 34), DialogResult = DialogResult.OK };
        var cancel = new Button { Name = "CancelEncodedImage", Text = "取消写回", AutoSize = true, MinimumSize = new Size(100, 34), DialogResult = DialogResult.Cancel };
        bar.Controls.Add(accept); bar.Controls.Add(cancel);
        Controls.Add(PreviewImageLayout.CreateViewport(_picture)); Controls.Add(note); Controls.Add(bar);
        AcceptButton = accept; CancelButton = cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { var image = _picture.Image; _picture.Image = null; image?.Dispose(); }
        base.Dispose(disposing);
    }
}
