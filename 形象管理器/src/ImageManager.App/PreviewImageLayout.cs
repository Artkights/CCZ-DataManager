namespace ImageManager.App;

/// <summary>Fits the whole image and makes its actual picture box follow its proportions.</summary>
public static class PreviewImageLayout
{
    public static Panel CreateViewport(PictureBox picture)
    {
        var viewport = new Panel
        {
            Dock = DockStyle.Fill, Margin = picture.Margin, BackColor = picture.BackColor,
            BorderStyle = picture.BorderStyle
        };
        picture.Dock = DockStyle.None;
        picture.Margin = Padding.Empty;
        picture.BorderStyle = BorderStyle.None;
        viewport.Controls.Add(picture);
        var arranging = false;
        void Arrange()
        {
            if (arranging || picture.IsDisposed || viewport.IsDisposed) return;
            arranging = true;
            try { picture.Bounds = FitImageBounds(picture.Image?.Size ?? Size.Empty, viewport.ClientRectangle); }
            finally { arranging = false; }
        }
        viewport.SizeChanged += (_, _) => Arrange();
        picture.HandleCreated += (_, _) => Arrange();
        // Image assignment invalidates the PictureBox, including animation
        // frames and replacements whose aspect ratio changes.
        picture.Invalidated += (_, _) => Arrange();
        return viewport;
    }

    public static Rectangle FitImageBounds(Size imageSize, Rectangle available)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0 || available.Width <= 0 || available.Height <= 0)
            return new Rectangle(available.Location, Size.Empty);
        var scale = Math.Min((double)available.Width / imageSize.Width, (double)available.Height / imageSize.Height);
        var width = Math.Clamp((int)Math.Round(imageSize.Width * scale), 1, available.Width);
        var height = Math.Clamp((int)Math.Round(imageSize.Height * scale), 1, available.Height);
        return new Rectangle(available.Left + (available.Width - width) / 2,
            available.Top + (available.Height - height) / 2, width, height);
    }
}
