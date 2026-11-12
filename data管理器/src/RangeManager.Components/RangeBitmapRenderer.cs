using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RangeManager.App;

/// <summary>The coordinate transform used for both painting and pointer input.</summary>
public readonly record struct RangeGridLayout(Rectangle Bounds, int Columns, int Rows, int CellSize)
{
    public Rectangle CellRectangle(CellOffset cell) => new(
        Bounds.Left + (cell.Dx + Columns / 2) * CellSize,
        Bounds.Top + (cell.Dy + Rows / 2) * CellSize, CellSize, CellSize);

    public bool TryHitTest(Point location, out CellOffset cell)
    {
        cell = new(0, 0);
        if (!Bounds.Contains(location)) return false;
        cell = new((location.X - Bounds.Left) / CellSize - Columns / 2,
            (location.Y - Bounds.Top) / CellSize - Rows / 2);
        return true;
    }
}

/// <summary>
/// Fixed pixel blocks copied from Signal's Effarea.e5 entry 10.  The range
/// editor owns this object and disposes it when a new preview is selected.
/// Keeping the four source blocks together prevents the UI and E5 writer from
/// silently falling back to base-dependent colours or generated patterns.
/// </summary>
public sealed class RangePixelTiles : IDisposable
{
    public Bitmap Background { get; }
    public Bitmap Own { get; }
    public Bitmap Attack { get; }
    public Bitmap Pierce { get; }
    /// <summary>Fixed 64×64 source frame with the original separators intact.</summary>
    public Bitmap? Frame { get; }

    public RangePixelTiles(Bitmap background, Bitmap own, Bitmap attack, Bitmap pierce, Bitmap? frame = null)
    {
        Background = background;
        Own = own;
        Attack = attack;
        Pierce = pierce;
        Frame = frame;
    }

    public void Dispose()
    {
        Background.Dispose();
        Own.Dispose();
        Attack.Dispose();
        Pierce.Dispose();
        Frame?.Dispose();
    }
}

public static class RangeBitmapRenderer
{
    private const int GridSize = 64;

    public static Bitmap RenderIconFromTiles(RangeKind kind, IReadOnlyCollection<CellOffset> cells,
        CellOffset? target, RangePixelTiles tiles, int width = 64, int height = 64)
    {
        if (width <= 0 || height <= 0 || width != height)
            throw new InvalidDataException("范围 E5 输出必须为正方形");
        var all = cells.Append(new CellOffset(0, 0)).Concat(target == null ? Array.Empty<CellOffset>() : new[] { target }).ToArray();
        int radius = Math.Max(3, all.Max(c => Math.Max(Math.Abs(c.Dx), Math.Abs(c.Dy))));
        int side = radius * 2 + 1;
        if (side > width)
            throw new InvalidDataException($"真实范围需要 {side}×{side} 格，无法完整拼接到 {width}×{height} 的方形 E5 条目");
        var attackSet = kind == RangeKind.Attack ? cells.ToHashSet() : [];
        var pierceSet = kind == RangeKind.Pierce ? cells.ToHashSet() : [];

        // Original range resources have a 7×7 logical grid.  For a range
        // that fits that grid, start from the source frame and replace only
        // the 8×8 cell interiors.  This preserves the one-pixel separators
        // and outer border at x/y = 0,9,...,63 exactly.
        if (width == GridSize && height == GridSize && side <= 7 &&
            tiles.Frame is { Width: GridSize, Height: GridSize })
        {
            var framed = new Bitmap(GridSize, GridSize, PixelFormat.Format32bppArgb);
            CopyTilePixels(tiles.Frame, framed, 0, 0);
            foreach (int y in Enumerable.Range(-3, 7))
            foreach (int x in Enumerable.Range(-3, 7))
            {
                var world = new CellOffset(x, y);
                Bitmap tile = SelectTile(world, attackSet, pierceSet, target, tiles);
                CopyTilePixels(tile, framed, 1 + (x + 3) * 9, 1 + (y + 3) * 9);
            }
            return framed;
        }

        return ComposeGrid(side, side, width, height, attackSet, pierceSet, target, tiles);
    }

    /// <summary>
    /// Copies source pixels with integer nearest-neighbour sampling. Every
    /// logical cell has at least one output pixel. Separators use source frame
    /// pixels and are omitted only when no room remains for cell interiors.
    /// </summary>
    private static Bitmap ComposeGrid(int columns, int rows, int width, int height,
        IReadOnlySet<CellOffset> attack, IReadOnlySet<CellOffset> pierce,
        CellOffset? target, RangePixelTiles tiles)
    {
        if (columns > width || rows > height)
            throw new InvalidDataException($"{width}×{height} 图片无法完整表达 {columns}×{rows} 格");
        bool lines = width >= columns * 3 + 1 && height >= rows * 3 + 1;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        int interiorWidth = width - (lines ? 1 : 0);
        int interiorHeight = height - (lines ? 1 : 0);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int column = Math.Min(columns - 1, ((x + 1) * columns - 1) / interiorWidth);
            int row = Math.Min(rows - 1, ((y + 1) * rows - 1) / interiorHeight);
            int left = column * interiorWidth / columns;
            int top = row * interiorHeight / rows;
            int right = (column + 1) * interiorWidth / columns;
            int bottom = (row + 1) * interiorHeight / rows;
            bool vertical = lines && (x == left || x == width - 1);
            bool horizontal = lines && (y == top || y == height - 1);
            if ((vertical || horizontal) && tiles.Frame is { } frame)
            {
                int sx = vertical ? (x == 0 ? 0 : x == width - 1 ? 63 : 9) :
                    1 + Math.Clamp((x - left - 1) * 8 / Math.Max(1, right - left - 1), 0, 7);
                int sy = horizontal ? (y == 0 ? 0 : y == height - 1 ? 63 : 9) :
                    1 + Math.Clamp((y - top - 1) * 8 / Math.Max(1, bottom - top - 1), 0, 7);
                bitmap.SetPixel(x, y, frame.GetPixel(sx, sy));
                continue;
            }
            var world = new CellOffset(column - columns / 2, row - rows / 2);
            var tile = SelectTile(world, attack, pierce, target, tiles);
            int inset = lines ? 1 : 0;
            int tx = Math.Clamp((x - left - inset) * tile.Width / Math.Max(1, right - left - inset), 0, tile.Width - 1);
            int ty = Math.Clamp((y - top - inset) * tile.Height / Math.Max(1, bottom - top - inset), 0, tile.Height - 1);
            bitmap.SetPixel(x, y, tile.GetPixel(tx, ty));
        }
        return bitmap;
    }

    public static Bitmap RenderIcon(RangeKind kind, IReadOnlyCollection<CellOffset> cells,
        CellOffset? target = null, int width = 64, int height = 64)
    {
        var all = cells.Append(new CellOffset(0, 0)).Concat(target == null ? [] : new[] { target }).ToArray();
        int columns = Math.Max(7, all.Max(c => Math.Abs(c.Dx)) * 2 + 1);
        int rows = Math.Max(7, all.Max(c => Math.Abs(c.Dy)) * 2 + 1);
        if (width <= 0 || height <= 0 || columns > width || rows > height)
            throw new InvalidDataException($"真实范围需要 {columns}×{rows} 格，无法完整写入 {width}×{height} 图片；请缩小范围后保存。");
        int pitch = Math.Min(width / columns, height / rows);
        var bounds = new Rectangle((width - columns * pitch) / 2, (height - rows * pitch) / 2,
            columns * pitch, rows * pitch);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        Draw(graphics, new(bounds, columns, rows, pitch),
            kind == RangeKind.Attack ? cells : [], kind == RangeKind.Pierce ? cells : [], target, showGrid: false);
        return bitmap;
    }

    public static void Draw(Graphics graphics, RangeGridLayout layout,
        IReadOnlyCollection<CellOffset> attack, IReadOnlyCollection<CellOffset> pierce,
        CellOffset? target = null, bool showGrid = true, RangePixelTiles? tiles = null)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
        var attackSet = attack.ToHashSet();
        var pierceSet = pierce.ToHashSet();
        using var lines = new Pen(ShapeGridControl.GridColor);
        using var blue = new SolidBrush(ShapeGridControl.OriginColor);
        using var red = new SolidBrush(ShapeGridControl.AttackColor);
        using var green = new SolidBrush(ShapeGridControl.PierceColor);
        // Build only the four tiny source cells, then scale those cells for
        // the viewport. This keeps the original line textures, avoids GDI+
        // source selection/alpha conversions during composition, and leaves
        // painting and pointer hit tests on the very same cell rectangles.
        using var fixedCells = tiles == null ? null : new PreviewCells(tiles);
        int inset = layout.CellSize >= 3 ? 1 : 0;
        for (int y = 0; y < layout.Rows; y++)
        for (int x = 0; x < layout.Columns; x++)
        {
            var world = new CellOffset(x - layout.Columns / 2, y - layout.Rows / 2);
            var rect = layout.CellRectangle(world);
            if (tiles != null)
            {
                Bitmap tile = SelectTile(world, attackSet, pierceSet, target, tiles);
                var oldInterpolation = graphics.InterpolationMode;
                var oldPixelOffset = graphics.PixelOffsetMode;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(fixedCells!.Get(tile), rect);
                graphics.InterpolationMode = oldInterpolation;
                graphics.PixelOffsetMode = oldPixelOffset;
            }
            else
            {
                Brush? fill = world is { Dx: 0, Dy: 0 } ? blue : pierceSet.Contains(world) ? green : attackSet.Contains(world) ? red : null;
                if (showGrid && layout.CellSize >= 3) graphics.DrawRectangle(lines, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                if (fill != null) graphics.FillRectangle(fill, Rectangle.Inflate(rect, -inset, -inset));
            }
        }
        if (target != null && target is not { Dx: 0, Dy: 0 } && layout.CellSize >= 4)
        {
            var marker = layout.CellRectangle(target);
            using var markerPen = new Pen(Color.Red, layout.CellSize >= 12 ? 2 : 1);
            marker = Rectangle.Inflate(marker, -Math.Max(1, layout.CellSize / 5), -Math.Max(1, layout.CellSize / 5));
            graphics.DrawRectangle(markerPen, marker);
        }
    }

    private sealed class PreviewCells : IDisposable
    {
        private readonly Dictionary<Bitmap, Bitmap> _cells = [];
        public PreviewCells(RangePixelTiles tiles)
        {
            foreach (var tile in new[] { tiles.Background, tiles.Own, tiles.Attack, tiles.Pierce })
            {
                var cell = new Bitmap(9, 9, PixelFormat.Format32bppArgb);
                for (int y = 0; y < 9; y++)
                for (int x = 0; x < 9; x++)
                    cell.SetPixel(x, y, x == 0 || y == 0
                        ? tiles.Frame?.GetPixel(x == 0 ? 9 : x, y == 0 ? 9 : y) ?? tile.GetPixel(0, 0)
                        : tile.GetPixel(x - 1, y - 1));
                _cells.Add(tile, cell);
            }
        }
        public Bitmap Get(Bitmap tile) => _cells[tile];
        public void Dispose() { foreach (var cell in _cells.Values) cell.Dispose(); }
    }

    private static Bitmap SelectTile(CellOffset world, IReadOnlySet<CellOffset> attack,
        IReadOnlySet<CellOffset> pierce, CellOffset? target, RangePixelTiles tiles)
    {
        if (world is { Dx: 0, Dy: 0 }) return tiles.Own;
        if (target == world && world is not { Dx: 0, Dy: 0 }) return tiles.Attack;
        return pierce.Contains(world) ? tiles.Pierce :
            attack.Contains(world) ? tiles.Attack : tiles.Background;
    }

    private static void CopyTilePixels(Bitmap source, Bitmap destination, int left, int top)
    {
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
            destination.SetPixel(left + x, top + y, source.GetPixel(x, y));
    }
}
