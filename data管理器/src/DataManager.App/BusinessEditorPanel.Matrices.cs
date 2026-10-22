using System.Globalization;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    // Matrix cells share the surrounding forms' editors, validation and draft bindings.
    private Control NumericEntityMatrix(string caption, IEnumerable<FieldBinding> source,
        string nameHeader = "名称", string valueHeader = "数值", int columnGroups = 1)
    {
        var bindings = source.ToArray();
        return EditableMatrix(caption, [nameHeader, valueHeader], bindings.Select(binding =>
            new Control[] { MatrixLabel(binding.Label ?? binding.Field.Name),
                CreateEditor(binding.Document, binding.Row, binding.RowIndex, binding.Field) }).ToArray(), columnGroups);
    }

    private Control TerrainValueMatrix()
    {
        // Keep each terrain's effectiveness and movement cost beside its name.
        // Thirty terrains form two full-height columns of fifteen entries.
        var effectiveness = Fields("job-series-terrain").ToArray();
        var movement = Fields("job-series-move-cost").ToArray();
        var host = Columns(50, 50);
        host.Name = "JobSeriesTerrain";
        for (var group = 0; group < 2; group++)
        {
            var start = group * 15;
            var rows = Enumerable.Range(start, Math.Min(15, Math.Max(0, Math.Min(effectiveness.Length, movement.Length) - start)))
                .Select(index => new Control[]
                {
                    MatrixLabel(TerrainName(index)),
                    CreateEditor(effectiveness[index].Document, effectiveness[index].Row, effectiveness[index].RowIndex, effectiveness[index].Field),
                    CreateEditor(movement[index].Document, movement[index].Row, movement[index].RowIndex, movement[index].Field)
                }).ToArray();
            var matrix = EditableMatrix(string.Empty, ["地形", "发挥", "消耗"], rows, 1);
            matrix.Name = $"JobSeriesTerrain{start}";
            host.Controls.Add(matrix, group, 0);
        }
        return host;
    }

    private static Label MatrixLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = Padding.Empty
    };

    private static int MatrixTextWidth(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty,
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;

    private static int MatrixLabelWidth(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty,
        TextFormatFlags.SingleLine).Width;

    private static int MatrixNumericWidth(NumericUpDown numeric, Font font)
        => MatrixTextWidth(numeric.Maximum.ToString(CultureInfo.InvariantCulture), font) +
            Math.Max(SystemInformation.VerticalScrollBarWidth, 18 * numeric.DeviceDpi / 96) + 8;

    private static int[] MatrixColumnWidths(TableLayoutPanel table, Font font)
    {
        using var headerFont = new Font(font, font.Style | FontStyle.Bold);
        return Enumerable.Range(0, table.ColumnCount).Select(column => table.Controls.Cast<Control>()
            .Where(control => table.GetPositionFromControl(control).Column == column)
            .Select(control => control switch
            {
                Label => MatrixLabelWidth(control.Text, table.GetPositionFromControl(control).Row == 0 ? headerFont : font),
                NumericUpDown numeric => MatrixNumericWidth(numeric, font),
                SearchableIdComboBox combo => combo.Options.Select(option => MatrixTextWidth(option.Display, font))
                    .DefaultIfEmpty(MatrixTextWidth("255", font)).Max() + Math.Max(SystemInformation.VerticalScrollBarWidth, 18 * table.DeviceDpi / 96) + 8,
                ByteTextEditor => TextRenderer.MeasureText("夏侯惇", font).Width +
                    TextRenderer.MeasureText("剩余 999 字节", font).Width + 16,
                _ => 80 * table.DeviceDpi / 96
            }).DefaultIfEmpty(0).Max()).ToArray();
    }

    private static bool MatrixIsReleasing(Control control)
    {
        for (Control? ancestor = control; ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.IsDisposed || ancestor.Disposing || ancestor is BusinessEditorPanel panel &&
                (panel._disposingDetailPages || panel._releaseCompleted)) return true;
        return false;
    }

    private void FitJobSeriesPage(TabPage page, TableLayoutPanel body, Control[] pageColumns)
        => FitMatrixPage(page, body, pageColumns, 20);

    private void FitLearningPage(TabPage page, TableLayoutPanel body)
        => FitMatrixPage(page, body, body.Controls.Cast<Control>().ToArray(), 20);

    private void FitMatrixPage(TabPage page, TableLayoutPanel body, Control[] pageColumns, int maximumRows)
    {
        IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        var number = Descendants(pageColumns[0]).OfType<NumericUpDown>().FirstOrDefault();
        // Inherited FontChanged notifications and native resize messages can
        // still refer to a previous font after the new one has been assigned.
        // Keep every owned font alive for the whole page lifetime.
        var fittedFonts = new Dictionary<(string Family, float Pixels, FontStyle Style), Font>();
        (Size Size, int Dpi, Font Font)? previousLayout = null;
        var arranging = false;
        int MinimumColumnWidth(Control column, Font font)
        {
            var width = column.Padding.Horizontal + column.Margin.Horizontal;
            foreach (var table in Descendants(column).OfType<TableLayoutPanel>()
                .Where(table => table.Name == "EditableMatrix"))
            {
                width += MatrixColumnWidths(table, font).Sum() +
                    (table.ColumnCount - 1) * Math.Max(4, 4 * table.DeviceDpi / 96);
            }
            return width;
        }
        void Arrange()
        {
            if (arranging || MatrixIsReleasing(page) || MatrixIsReleasing(body) || IsDisposed || Disposing ||
                _disposingDetailPages || _releaseCompleted ||
                body.ClientSize.Width < 100 || body.ClientSize.Height < 100) return;
            var baseline = Font;
            if (previousLayout is { } previous && previous.Size == body.ClientSize &&
                previous.Dpi == page.DeviceDpi && previous.Font.Equals(baseline)) return;
            arranging = true;
            try
            {
                // Pixel fonts make the constraint physical: at 144 DPI twenty
                // native numeric editors must still fit a 720-pixel window.
                // Restore the surrounding form's normal font as room increases.
                var normalPixels = baseline.SizeInPoints * page.DeviceDpi / 72f;
                var nativePadding = number == null ? 7 : number.PreferredSize.Height - number.Font.Height;
                var counterHeight = pageColumns.Min(column => body.ClientSize.Height - column.Margin.Vertical - column.Padding.Vertical);
                var pixels = normalPixels;
                Font candidate;
                int[] minimumWidths;
                while (true)
                {
                    var key = (baseline.FontFamily.Name, pixels, baseline.Style);
                    if (!fittedFonts.TryGetValue(key, out candidate!))
                        fittedFonts.Add(key, candidate = new Font(baseline.FontFamily, pixels, baseline.Style, GraphicsUnit.Pixel));
                    minimumWidths = pageColumns.Select(column => MinimumColumnWidth(column, candidate)).ToArray();
                    var headerHeight = candidate.Height + Math.Max(4, 4 * page.DeviceDpi / 96);
                    var fitsHeight = headerHeight + maximumRows * (candidate.Height + nativePadding) + 2 <= counterHeight;
                    if ((fitsHeight && minimumWidths.Sum() <= body.ClientSize.Width) || pixels <= 9) break;
                    pixels = Math.Max(9, pixels - .5f);
                }
                page.Font = candidate;
                var remainder = Math.Max(0, body.ClientSize.Width - minimumWidths.Sum());
                for (var column = 0; column < pageColumns.Length; column++)
                {
                    body.ColumnStyles[column].SizeType = column == pageColumns.Length - 1 ? SizeType.Percent : SizeType.Absolute;
                    body.ColumnStyles[column].Width = column == pageColumns.Length - 1 ? 100
                        : minimumWidths[column] + remainder / pageColumns.Length + (column < remainder % pageColumns.Length ? 1 : 0);
                }
                if (pageColumns.Length == 5 && pageColumns[2].Parent is TableLayoutPanel terrainColumns && ReferenceEquals(terrainColumns, pageColumns[3].Parent))
                {
                    terrainColumns.ColumnStyles[0].SizeType = SizeType.Absolute;
                    terrainColumns.ColumnStyles[0].Width = body.ColumnStyles[2].Width;
                }
                previousLayout = (body.ClientSize, page.DeviceDpi, baseline);
            }
            finally { arranging = false; }
        }
        body.ClientSizeChanged += (_, _) => Arrange();
        page.DpiChangedAfterParent += (_, _) => Arrange();
        page.HandleCreated += (_, _) => Arrange();
        page.VisibleChanged += (_, _) => { if (page.Visible) Arrange(); };
        EventHandler fontChanged = (_, _) => Arrange();
        FontChanged += fontChanged;
        page.Disposed += (_, _) =>
        {
            FontChanged -= fontChanged;
            foreach (var font in fittedFonts.Values) font.Dispose();
            fittedFonts.Clear();
        };
        Arrange();
    }

    private static Control EditableMatrix(string caption, string[] headers, Control[][] rows, int columnGroups)
    {
        var host = SectionContainer(caption);
        var columns = Columns(Enumerable.Repeat(100f / columnGroups, columnGroups).ToArray());
        host.Controls.Add(columns);
        var rowsPerColumn = Math.Max(1, (rows.Length + columnGroups - 1) / columnGroups);
        for (var group = 0; group < columnGroups; group++)
        {
            var entries = rows.Skip(group * rowsPerColumn).Take(rowsPerColumn).ToArray();
            var viewport = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var table = new TableLayoutPanel
            {
                Name = "EditableMatrix", Dock = DockStyle.None, ColumnCount = headers.Length, RowCount = rowsPerColumn + 1,
                Margin = Padding.Empty, Padding = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            for (var column = 0; column < headers.Length; column++)
                table.ColumnStyles.Add(new ColumnStyle(column == headers.Length - 1 ? SizeType.Percent : SizeType.Absolute,
                    column == headers.Length - 1 ? 100 : 0));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            for (var row = 0; row < rowsPerColumn; row++)
                table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rowsPerColumn));
            for (var column = 0; column < headers.Length; column++)
            {
                var label = MatrixLabel(headers[column]);
                table.Controls.Add(label, column, 0);
            }
            for (var row = 0; row < entries.Length; row++)
                for (var column = 0; column < headers.Length; column++)
                {
                    var control = entries[row][column];
                    control.Margin = Padding.Empty;
                    if (column > 0)
                    {
                        control.Dock = DockStyle.None;
                        control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    }
                    table.Controls.Add(control, column, row + 1);
                }
            viewport.Controls.Add(table);
            columns.Controls.Add(viewport, group, 0);
            Font? headerFont = null;
            var headerFonts = new Dictionary<(string Family, float Size, GraphicsUnit Unit, FontStyle Style), Font>();
            var arranging = false;
            var queued = false;
            var handleGeneration = 0;
            void Arrange()
            {
                if (arranging || MatrixIsReleasing(host) || MatrixIsReleasing(table) || MatrixIsReleasing(viewport)) return;
                arranging = true;
                try
                {
                var available = Math.Max(1, viewport.ClientSize.Width);
                if (headerFont == null || headerFont.Size != table.Font.Size || headerFont.Unit != table.Font.Unit ||
                    headerFont.Style != (table.Font.Style | FontStyle.Bold) || !headerFont.FontFamily.Equals(table.Font.FontFamily))
                {
                    var key = (table.Font.FontFamily.Name, table.Font.Size, table.Font.Unit, table.Font.Style | FontStyle.Bold);
                    if (!headerFonts.TryGetValue(key, out headerFont))
                        headerFonts.Add(key, headerFont = new Font(table.Font, table.Font.Style | FontStyle.Bold));
                    foreach (var header in table.Controls.OfType<Label>().Where(label => table.GetPositionFromControl(label).Row == 0))
                        header.Font = headerFont;
                }
                // Column groups absorb the page's spare width. The fields
                // inside a group remain a compact unit with one small gap.
                var gap = Math.Max(4, 4 * table.DeviceDpi / 96);
                var widths = MatrixColumnWidths(table, table.Font);
                var width = Math.Min(available, widths.Sum() + gap * (headers.Length - 1));
                if (widths.Sum() + gap * (headers.Length - 1) > available)
                    widths[0] = Math.Max(1, available - widths.Skip(1).Sum() - gap * (headers.Length - 1));
                for (var column = 0; column < headers.Length - 1; column++)
                    table.ColumnStyles[column].Width = widths[column] + gap;
                var headerHeight = table.Font.Height + Math.Max(4, 4 * table.DeviceDpi / 96);
                table.RowStyles[0].Height = headerHeight;
                var rowsHeight = Math.Max(0, viewport.ClientSize.Height - headerHeight);
                for (var row = 0; row < entries.Length; row++)
                    for (var column = 1; column < headers.Length; column++)
                        entries[row][column].Width = widths[column];
                var minimumHeights = Enumerable.Range(0, rowsPerColumn).Select(row => row < entries.Length
                    ? entries[row].OfType<ByteTextEditor>().Select(editor => editor.MinimumSize.Height).DefaultIfEmpty(0).Max() : 0).ToArray();
                var evenHeight = rowsHeight / rowsPerColumn;
                while (evenHeight > 0 && minimumHeights.Sum(minimum => Math.Max(minimum, evenHeight)) > rowsHeight) evenHeight--;
                var rowHeights = minimumHeights.Select(minimum => Math.Max(minimum, evenHeight)).ToArray();
                var spareHeight = Math.Max(0, rowsHeight - rowHeights.Sum());
                for (var row = 0; row < spareHeight; row++) rowHeights[row % rowsPerColumn]++;
                for (var row = 0; row < rowsPerColumn; row++)
                {
                    // Explicit pixel distribution keeps the last row within
                    // one pixel of the others at every viewport height.
                    table.RowStyles[row + 1].SizeType = SizeType.Absolute;
                    table.RowStyles[row + 1].Height = rowHeights[row];
                }
                table.SetBounds((available - width) / 2, 0, width, viewport.ClientSize.Height);
                for (var row = 0; row < entries.Length; row++)
                    for (var column = 1; column < headers.Length; column++)
                    {
                        var editor = entries[row][column];
                        var rowHeight = rowHeights[row];
                        // Native numeric editors enforce their own height after
                        // inheriting a font. Top anchoring plus an explicit inset
                        // avoids TableLayoutPanel centering from an old specified
                        // height after a small-to-large window transition.
                        editor.Margin = new Padding(0, Math.Max(0, (rowHeight - editor.Height) / 2), 0, 0);
                    }
                }
                finally { arranging = false; }
            }
            void QueueArrange()
            {
                if (queued || !table.IsHandleCreated || MatrixIsReleasing(table) || MatrixIsReleasing(host)) return;
                queued = true;
                var generation = handleGeneration;
                table.BeginInvoke(() =>
                {
                    if (generation != handleGeneration) return;
                    queued = false;
                    if (!table.IsHandleCreated || MatrixIsReleasing(table) || MatrixIsReleasing(host)) return;
                    Arrange();
                    // A parent's FontChanged event precedes the native editors'
                    // inherited font and height updates. Recenter them after
                    // propagation, using their final heights rather than the
                    // previous font's height.
                    table.PerformLayout();
                });
            }
            table.HandleDestroyed += (_, _) => { handleGeneration++; queued = false; };
            viewport.SizeChanged += (_, _) => Arrange();
            table.FontChanged += (_, _) => { Arrange(); QueueArrange(); };
            table.DpiChangedAfterParent += (_, _) => { Arrange(); QueueArrange(); };
            table.HandleCreated += (_, _) => { Arrange(); QueueArrange(); };
            host.Disposed += (_, _) =>
            {
                foreach (var font in headerFonts.Values) font.Dispose();
                headerFonts.Clear();
            };
            Arrange();
        }
        return host;
    }

    // Learning levels use exactly the same editors and draft bindings as the
    // counter values on the job-series page.
    private Control EntityMatrix(string caption, IEnumerable<FieldBinding> source)
        => NumericEntityMatrix(string.Empty, source, "兵种", "等级");
}
