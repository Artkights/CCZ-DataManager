namespace RangeManager.App;

public sealed record CellOffset(int Dx, int Dy);

public sealed class RangeShapeRow
{
    public required int Id { get; init; }
    public required int TableIndex { get; init; }
    public uint PointerVa { get; set; }
    public List<CellOffset> Cells { get; set; } = [];
    public List<CellOffset> OriginalCells { get; init; } = [];
    public uint OriginalPointer { get; init; }
    public string Status { get; set; } = "";
    public string CellsText => string.Join("; ", Cells.Select(c => $"{c.Dx},{c.Dy}"));
    public int ExpandedCount => ShapeGeometry.ExpandMirror(Cells).Count;
}

/// <summary>
/// The data-driven range library at Ekd5 0x4D1400. Each of the 36 pointers
/// names a segment of 2-byte relative cells terminated by 0xFFFF. The engine's
/// range dispatcher (0x4365A1/0x43659C) reads this table; ids 0xA and 0xB are
/// special-cased in code and therefore not editable here.
/// </summary>
public sealed class RangeShapeTable
{
    private const uint ScanLimit = 0x4000;
    private const uint FallbackArenaStart = 0x004D1490;

    private readonly GameBase _base;
    private uint _freeVa;
    private uint _arenaBase;
    private uint _arenaEnd;
    public List<RangeShapeRow> Rows { get; } = [];

    public RangeShapeTable(GameBase gameBase)
    {
        _base = gameBase;
        Load();
    }

    public static IEnumerable<int> EditableIds
    {
        get
        {
            for (int id = 0; id <= 9; id++) yield return id;
            for (int id = 0x0C; id <= 0x25; id++) yield return id;
        }
    }

    public uint ArenaBaseVa => _arenaBase;
    public uint ArenaEndVa => _arenaEnd;
    public int ArenaTotalBytes => (int)(_arenaEnd - _arenaBase);
    public int ArenaUsedBytes => (int)(_freeVa - _arenaBase);
    public int ArenaRemainingBytes => (int)(_arenaEnd - _freeVa);
    public static int RequiredBytes(int cellCount) => cellCount * 2 + 2;

    public int InPlaceCapacity(int id)
    {
        var row = Find(id);
        return row == null ? 0 : SegmentCapacity(row.PointerVa);
    }

    private void Load()
    {
        Rows.Clear();
        uint maxEnd = 0;
        foreach (int id in EditableIds)
        {
            int index = id <= 9 ? id : id - 2;
            uint pointer = _base.Exe.ReadU32(GameBase.RangeShapeTableVa + (uint)(index * 4));
            var cells = ReadSegment(pointer, out uint end);
            Rows.Add(new RangeShapeRow
            {
                Id = id,
                TableIndex = index,
                PointerVa = pointer,
                OriginalPointer = pointer,
                Cells = cells,
                OriginalCells = [.. cells],
                Status = cells.Count == 0 ? "空" : $"{cells.Count} 段"
            });
            if (end > maxEnd) maxEnd = end;
        }

        // New/longer shapes must not overwrite the custom code that follows the
        // shape block in 6.5/6.6. The safe arena is the run of NOP/zero padding
        // that starts right after the last segment. It differs per build, so it
        // is detected instead of hard-coded (0x4D2511 in 6.5, 0x4D2550 in 6.6).
        uint candidate = (maxEnd + 3) & ~3u;
        uint arenaEnd = ScanPaddingEnd(candidate);
        if (arenaEnd - candidate < 0x80)
        {
            candidate = FallbackArenaStart;
            arenaEnd = ScanPaddingEnd(candidate);
        }
        uint hardEnd = HardArenaEnd();
        if (hardEnd > candidate && hardEnd < arenaEnd) arenaEnd = hardEnd;
        _arenaBase = candidate;
        _arenaEnd = arenaEnd;
        _freeVa = candidate;
    }

    /// <summary>
    /// Conservative upper bound of the padding area. The scan alone stops at a
    /// fixed 0x4000 window in builds whose padding is longer than the window
    /// (6.4), so the known data/code boundary is used as a ceiling.
    /// </summary>
    private uint HardArenaEnd() => _base.Version switch
    {
        EngineVersion.V64 => 0x004D63F8,
        EngineVersion.V65 => 0x004D2511,
        EngineVersion.V66 => 0x004D2550,
        _ => 0x004D2511
    };

    private uint ScanPaddingEnd(uint start)
    {
        uint limit = start + ScanLimit;
        for (uint va = start; va < limit; va++)
        {
            if (!_base.Exe.TryVaToOffset(va, out _)) return va;
            byte b = _base.Exe.ReadByte(va);
            if (b is not (0x00 or 0x90)) return va;
        }
        return limit;
    }

    public string ArenaText => $"0x{_arenaBase:X8}–0x{_arenaEnd:X8}（总 {ArenaTotalBytes} 字节，已用 {ArenaUsedBytes}，剩余 {ArenaRemainingBytes}）";

    private List<CellOffset> ReadSegment(uint va, out uint endVa)
    {
        var cells = new List<CellOffset>();
        if (!_base.Exe.TryVaToOffset(va, out _))
        {
            endVa = va;
            return cells;
        }
        for (int i = 0; i < 512; i++)
        {
            uint at = va + (uint)(i * 2);
            if (!_base.Exe.TryVaToOffset(at, out _)) break;
            ushort word = _base.Exe.ReadU16(at);
            if (word == 0xFFFF)
            {
                endVa = va + (uint)((i + 1) * 2);
                return cells;
            }
            cells.Add(new CellOffset((sbyte)(word & 0xFF), (sbyte)(word >> 8)));
        }
        endVa = va;
        return [];
    }

    public RangeShapeRow? Find(int id) => Rows.FirstOrDefault(r => r.Id == id);

    private int SegmentCapacity(uint pointer)
    {
        // In-place updates are bounded by the next *current* segment (or the
        // start of the padding arena). Segments relocated by this tool are
        // included because Rows is reloaded from the pointer table.
        uint next = _arenaEnd;
        foreach (var row in Rows)
        {
            uint p = row.PointerVa;
            if (p > pointer && p < next) next = p;
        }
        return next > pointer ? (int)(next - pointer) : 0;
    }

    public void SetCells(int id, IReadOnlyList<CellOffset> cells)
    {
        var row = Find(id) ?? throw new ArgumentOutOfRangeException(nameof(id), "该形状 ID 由引擎代码特殊处理，不能编辑");
        if (cells.Count > 256) throw new InvalidDataException("一个形状最多 256 个生成元");
        foreach (var cell in cells)
        {
            if (cell.Dx is < -32 or > 31 || cell.Dy is < -32 or > 31)
                throw new InvalidDataException("生成元偏移必须在 -32..31 之间");
            if (cell.Dx == -1 && cell.Dy == -1)
                throw new InvalidDataException("-1,-1 与原生 FFFF 终止符冲突");
        }

        var data = new byte[cells.Count * 2 + 2];
        for (int i = 0; i < cells.Count; i++)
        {
            data[i * 2] = (byte)(sbyte)cells[i].Dx;
            data[i * 2 + 1] = (byte)(sbyte)cells[i].Dy;
        }
        data[^2] = 0xFF;
        data[^1] = 0xFF;

        uint pointer = row.PointerVa;
        int capacity = SegmentCapacity(pointer);
        if (data.Length > capacity || !_base.Exe.TryVaToOffset(pointer, out _))
        {
            pointer = Allocate(data.Length);
            row.PointerVa = pointer;
        }
        _base.Exe.WriteBytes(pointer, data);
        _base.Exe.WriteU32(GameBase.RangeShapeTableVa + (uint)(row.TableIndex * 4), pointer);
        row.Cells = [.. cells];
        row.Status = cells.Count == 0 ? "空" : $"{cells.Count} 段";
    }

    private uint Allocate(int length)
    {
        uint va = (_freeVa + 3) & ~3u;
        if (va + (uint)length > _arenaEnd)
            throw new InvalidDataException($"形状数据空闲区不足：需要 {length} 字节，安全区仅剩 {_arenaEnd - va} 字节（{ArenaText}）");
        _freeVa = (va + (uint)length + 3) & ~3u;
        return va;
    }

    public void Reset(int id)
    {
        var row = Find(id) ?? throw new ArgumentOutOfRangeException(nameof(id));
        var bytes = Serialize(row.OriginalCells);
        _base.Exe.WriteBytes(row.OriginalPointer, bytes);
        _base.Exe.WriteU32(GameBase.RangeShapeTableVa + (uint)(row.TableIndex * 4), row.OriginalPointer);
        row.PointerVa = row.OriginalPointer;
        row.Cells = [.. row.OriginalCells];
        row.Status = row.Cells.Count == 0 ? "空" : $"{row.Cells.Count} 段";
    }

    public static byte[] Serialize(IReadOnlyList<CellOffset> cells)
    {
        var data = new byte[cells.Count * 2 + 2];
        for (int i = 0; i < cells.Count; i++)
        {
            data[i * 2] = (byte)(sbyte)cells[i].Dx;
            data[i * 2 + 1] = (byte)(sbyte)cells[i].Dy;
        }
        data[^2] = 0xFF;
        data[^1] = 0xFF;
        return data;
    }

    public static List<CellOffset> ParseText(string text)
    {
        var cells = new List<CellOffset>();
        foreach (var part in text.Split([';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split([',', '，'], StringSplitOptions.TrimEntries);
            if (xy.Length != 2 || !int.TryParse(xy[0], out int dx) || !int.TryParse(xy[1], out int dy))
                throw new FormatException($"无法解析生成元“{part}”，格式应为 dx,dy");
            cells.Add(new CellOffset(dx, dy));
        }
        return cells;
    }
}

/// <summary>
/// Mirror expansion used by the engine geometry routine (0x436611 family).
/// A two-axis generator (a,b) produces the four quadrant points; a single-axis
/// generator produces the four orthogonal neighbours at distance max(|a|,|b|).
/// </summary>
public static class ShapeGeometry
{
    public static List<CellOffset> ExpandMirror(IEnumerable<CellOffset> generators)
    {
        var set = new HashSet<(int X, int Y)>();
        foreach (var g in generators)
        {
            int a = g.Dx;
            int b = g.Dy;
            if (a != 0 && b != 0)
            {
                set.Add((a, b));
                set.Add((-a, b));
                set.Add((a, -b));
                set.Add((-a, -b));
            }
            else
            {
                int c = Math.Max(Math.Abs(a), Math.Abs(b));
                if (c == 0) continue;
                set.Add((0, -c));
                set.Add((c, 0));
                set.Add((0, c));
                set.Add((-c, 0));
            }
        }
        return set
            .OrderBy(p => p.Y)
            .ThenBy(p => p.X)
            .Select(p => new CellOffset(p.X, p.Y))
            .ToList();
    }

    /// <summary>Normalizes an expanded cell back to a first-quadrant/axis generator.</summary>
    public static CellOffset? ToGenerator(CellOffset cell)
    {
        if (cell.Dx == 0 && cell.Dy == 0) return null;
        if (cell.Dx == 0) return new CellOffset(0, Math.Abs(cell.Dy));
        if (cell.Dy == 0) return new CellOffset(0, Math.Abs(cell.Dx));
        return new CellOffset(Math.Abs(cell.Dx), Math.Abs(cell.Dy));
    }

    /// <summary>
    /// Toggles the symmetric group that contains <paramref name="cell"/>.
    /// Existing generators keep their original sign; new ones are normalized.
    /// </summary>
    public static List<CellOffset> Toggle(List<CellOffset> generators, CellOffset cell)
    {
        foreach (var g in generators)
        {
            if (ExpandMirror([g]).Any(c => c.Dx == cell.Dx && c.Dy == cell.Dy))
            {
                var copy = generators.ToList();
                copy.Remove(g);
                return copy;
            }
        }
        var generator = ToGenerator(cell);
        if (generator == null) return generators.ToList();
        var result = generators.ToList();
        result.Add(generator);
        return result;
    }

    public static int RadiusOf(IEnumerable<CellOffset> cells)
        => cells.Select(c => Math.Max(Math.Abs(c.Dx), Math.Abs(c.Dy))).DefaultIfEmpty(0).Max();
}

/// <summary>Per-job shape id consumed by 0x40782D (physical attack targeting and damage area).</summary>
public sealed class JobPenetrationTable
{
    private readonly GameBase _base;
    public List<(int Id, string Name, byte ShapeId)> Rows { get; } = [];

    public JobPenetrationTable(GameBase gameBase)
    {
        _base = gameBase;
        for (int i = 0; i < GameBase.JobCount; i++)
            Rows.Add((i, gameBase.ReadJobName(i), gameBase.Exe.ReadByte(GameBase.PenetrationTableVa + (uint)i)));
    }

    public void Set(int id, byte value) => _base.Exe.WriteByte(GameBase.PenetrationTableVa + (uint)id, value);

    public static string ShapeName(int id) => id switch
    {
        0 => "不穿透（仅目标）",
        1 => "十字穿透",
        2 => "九宫穿透",
        3 => "大没羽箭（九宫+对角2）",
        4 => "蛇矛（1格）",
        5 => "长蛇矛/穿六（5格）",
        6 => "大大没羽箭（九宫+对角3）",
        7 => "延伸2格（原版无名）",
        8 => "九宫（扩展）",
        9 => "双向两格（扩展）",
        10 => "八邻域（扩展）",
        11 => "双向两格（扩展）",
        _ => $"未知形状 {id}"
    };
}

/// <summary>Compatibility facade. Production previews use the verified live EXE catalog.</summary>
public static class PhysicalShapes
{
    public static List<CellOffset> Expand(GameBase gameBase, int shapeId)
    {
        var binding = NativeRangeCatalog.Read(gameBase).FirstOrDefault(b => b.Key == new RangeKey(RangeKind.Pierce, shapeId));
        if (binding == null) return [];
        if (!binding.Editable && binding.Special == "原生契约未匹配")
            throw new InvalidDataException(binding.ReadOnlyReason);
        return NativeRangeCatalog.Expand(binding, RangePreviewContext.Default).ToList();
    }
    public static string Describe(int shapeId) => JobPenetrationTable.ShapeName(shapeId);
}
