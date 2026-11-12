using System.Drawing.Drawing2D;
using System.Globalization;
using System.Drawing.Imaging;
using System.ComponentModel;

namespace RangeManager.App;

/// <summary>One cached image used by the extension mapping grid and its popup.</summary>
public sealed record ExtensionRangePreview(int Id, Bitmap Image, string Source, string Status);

/// <summary>
/// Owns the small set of extension previews.  E5 images are preferred; when
/// an E5 entry is absent, a finite EXE definition is rendered in memory using
/// the same fixed range tiles as the range editor.  The cache never writes an
/// E5 archive and the combo-box only borrows these bitmaps while it is open.
/// </summary>
public sealed class ExtensionRangePreviewCache : IDisposable
{
    private readonly Dictionary<int, ExtensionRangePreview> _items = [];
    private Bitmap? _unavailable;

    public ExtensionRangePreview? Get(int id)
    {
        if (_items.TryGetValue(id, out var value)) return value;
        return _unavailable == null ? null : new ExtensionRangePreview(
            id, _unavailable, "不可用", $"范围 0x{id:X2} 当前 EXE 没有可识别的攻击定义或 E5 条目");
    }

    public void Rebuild(RangeEditSession session, IEnumerable<RangeBinding> attackBindings)
    {
        Clear();
        var bindings = attackBindings.ToDictionary(binding => binding.Key.Id);
        using var tiles = session.CreatePreviewTiles(new RangeKey(RangeKind.Attack, 0));
        _unavailable = RenderUnavailable(tiles, "无图");
        foreach (var binding in bindings.Values.OrderBy(binding => binding.Key.Id))
        {
            int id = binding.Key.Id;
            Bitmap? image = session.CreateExtensionRangePreview(id);
            string source;
            string status;
            if (image != null)
            {
                source = "Hitarea.e5";
                status = $"Hitarea.e5 第 {id + 1} 项";
            }
            else if (binding.Special.Contains("全场", StringComparison.Ordinal))
            {
                image = RenderSpecial(tiles, "全场");
                source = "EXE 特殊语义";
                status = "原生全场范围，无有限格点图片";
            }
            else if (binding.Special.Contains("忽略", StringComparison.Ordinal) ||
                     binding.Special.Contains("跳过", StringComparison.Ordinal))
            {
                image = RenderSpecial(tiles, "跳过");
                source = "EXE 特殊语义";
                status = "原生忽略范围，无有限格点图片";
            }
            else if (binding.Editable && binding.Definition.Groups.Count > 0)
            {
                try
                {
                    var cells = NativeRangeCatalog.Expand(binding, RangePreviewContext.Default);
                    image = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack, cells, null, tiles);
                    source = "EXE 定义（内存预览）";
                    status = "当前基底没有对应 Hitarea.e5，按 EXE 定义生成内存预览";
                }
                catch (Exception ex)
                {
                    image = RenderUnavailable(tiles, "无法");
                    source = "不可用";
                    status = "无法生成预览：" + ex.Message;
                }
            }
            else
            {
                image = RenderUnavailable(tiles, "无图");
                source = "不可用";
                status = string.IsNullOrWhiteSpace(binding.ReadOnlyReason)
                    ? "当前范围没有可识别的有限定义"
                    : binding.ReadOnlyReason;
            }
            _items[id] = new ExtensionRangePreview(id, image, source, status);
        }

        // The mapping field is a byte, so the editor accepts every finite
        // value 0x00..0xFE.  Missing IDs borrow one shared placeholder from
        // Get() instead of allocating 255 identical bitmaps during every
        // page refresh. The popup still paints an image for every candidate.
    }

    private static Bitmap RenderSpecial(RangePixelTiles tiles, string text)
    {
        var bitmap = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack, [], null, tiles);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.None;
        using var background = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        graphics.FillRectangle(background, 4, 24, 56, 16);
        using var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.White);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, brush, (64 - size.Width) / 2f, (64 - size.Height) / 2f);
        return bitmap;
    }

    private static Bitmap RenderUnavailable(RangePixelTiles tiles, string text)
    {
        var bitmap = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack, [], null, tiles);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.None;
        using var background = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
        graphics.FillRectangle(background, 2, 22, 60, 20);
        using var font = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.Gainsboro);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, brush, (64 - size.Width) / 2f, (64 - size.Height) / 2f);
        return bitmap;
    }

    public void Clear()
    {
        foreach (var item in _items.Values) item.Image.Dispose();
        _items.Clear();
        _unavailable?.Dispose();
        _unavailable = null;
    }

    public void Dispose() => Clear();
}

/// <summary>Owner-drawn DataGridView editor for range IDs and their thumbnails.</summary>
public sealed class ExtensionRangePreviewComboBoxEditingControl : DataGridViewComboBoxEditingControl
{
    public Func<int, ExtensionRangePreview?>? PreviewProvider { get; set; }

    public ExtensionRangePreviewComboBoxEditingControl()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        DropDownStyle = ComboBoxStyle.DropDownList;
        IntegralHeight = false;
        MaxDropDownItems = 8;
        DropDownHeight = 560;
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        e.ItemHeight = Math.Max(70, 64 + 6 * Math.Max(1, DeviceDpi / 96));
        base.OnMeasureItem(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= Items.Count) return;
        string text = Convert.ToString(Items[e.Index], CultureInfo.InvariantCulture) ?? "";
        bool editing = (e.State & DrawItemState.ComboBoxEdit) != 0;
        if (!editing && TryParseId(text, out int id))
        {
            var preview = PreviewProvider?.Invoke(id);
            var imageBounds = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 3, 64, 64);
            if (preview?.Image != null)
            {
                var oldInterpolation = e.Graphics.InterpolationMode;
                var oldPixelOffset = e.Graphics.PixelOffsetMode;
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.DrawImage(preview.Image, imageBounds);
                e.Graphics.InterpolationMode = oldInterpolation;
                e.Graphics.PixelOffsetMode = oldPixelOffset;
            }
            var textBounds = new Rectangle(imageBounds.Right + 8, e.Bounds.Top,
                Math.Max(1, e.Bounds.Width - imageBounds.Width - 14), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, Font, textBounds, e.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -4, 0),
                e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        e.DrawFocusRectangle();
    }

    private static bool TryParseId(string text, out int id)
    {
        text = text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }
}

public sealed class ExtensionRangePreviewComboBoxCell : DataGridViewComboBoxCell
{
    public Func<int, ExtensionRangePreview?>? PreviewProvider { get; set; }

    public override Type EditType => typeof(ExtensionRangePreviewComboBoxEditingControl);

    public override object Clone()
    {
        var clone = (ExtensionRangePreviewComboBoxCell)base.Clone();
        clone.PreviewProvider = PreviewProvider;
        return clone;
    }

    // DataGridViewComboBoxCell normally asks its CurrencyManager to resolve a
    // formatted value.  With a simple string[] source and an owner-drawn
    // editing control that resolution can yield an empty display value while
    // the dropdown itself still contains the item.  The mapping grid stores
    // the already formatted hexadecimal string, so keep that value visible
    // in the non-editing cell and let the standard combo editor validate it
    // against the same string[] source.
    protected override object GetFormattedValue(object? value, int rowIndex,
        ref DataGridViewCellStyle cellStyle, TypeConverter? valueTypeConverter,
        TypeConverter? formattedValueTypeConverter, DataGridViewDataErrorContexts context)
    {
        if (value is string text && text.Length != 0) return text;
        return base.GetFormattedValue(value, rowIndex, ref cellStyle,
            valueTypeConverter, formattedValueTypeConverter, context);
    }

    public override void InitializeEditingControl(int rowIndex, object? initialFormattedValue,
        DataGridViewCellStyle dataGridViewCellStyle)
    {
        base.InitializeEditingControl(rowIndex, initialFormattedValue, dataGridViewCellStyle);
        if (DataGridView?.EditingControl is ExtensionRangePreviewComboBoxEditingControl editor)
        {
            editor.PreviewProvider = PreviewProvider;
            editor.DrawMode = DrawMode.OwnerDrawVariable;
            editor.DropDownStyle = ComboBoxStyle.DropDownList;
            editor.Invalidate();
        }
    }
}
