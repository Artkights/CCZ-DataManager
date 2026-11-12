namespace RangeManager.App;

/// <summary>
/// Reads range definitions from the selected executable. E5 artwork and Data.e5
/// never participate in definition discovery. Shared native pointers share one
/// definition object; a binding retains its own center and exclusion semantics.
/// </summary>
public static class NativeRangeCatalog
{
    private const uint PiercePointers = 0x004D13A0;
    private const uint PierceClasses = 0x00406236;
    private static readonly CellOffset[] NativeDirections =
    [
        new(0, -1), new(1, 0), new(0, 1), new(-1, 0),
        new(1, -1), new(1, 1), new(-1, 1), new(-1, -1)
    ];

    public static IReadOnlyList<RangeBinding> Read(GameBase game)
    {
        var bindings = new List<RangeBinding>();
        var definitions = new Dictionary<string, RangeDefinition>(StringComparer.Ordinal);
        string attackError = string.Join("；", NativeRangeContract.Validate(game, RangeKind.Attack));
        foreach (int id in Enumerable.Range(0, 0x26))
        {
            var binding = New(game, RangeKind.Attack, id, RangeCenterMode.Self);
            if (id is 0x0A or 0x0B)
            {
                binding.Special = id == 0x0A ? "全场特殊分支" : "原生忽略编号";
                binding.Name = binding.Special;
                binding.Editable = false;
                binding.ReadOnlyReason = id == 0x0A
                    ? "该编号调用原生全场枚举器，不能表示为有限的相对格点"
                    : "该编号在原生攻击分派中被跳过";
                binding.Source = id == 0x0A ? "Ekd5.exe 0x00436582 → 0x00437D10" : "Ekd5.exe 原生攻击分派";
            }
            else if (attackError.Length > 0) Unsupported(binding, attackError);
            else
            {
                uint slot = GameBase.RangeShapeTableVa + (uint)((id < 0x0A ? id : id - 2) * 4);
                ReadSharedSegment(game, binding, slot, definitions);
            }
            bindings.Add(binding);
        }

        string pierceError = string.Join("；", NativeRangeContract.Validate(game, RangeKind.Pierce));
        bool wrapperKnown = NativeRangeContract.TryReadPierceCenter(game, out var wrapperCenter, out string wrapperError);
        if (!wrapperKnown) pierceError += (pierceError.Length == 0 ? "" : "；") + wrapperError;
        int count = game.Version == EngineVersion.V64 ? 10 : game.Version == EngineVersion.V65 ? 12 : 11;
        if (game.Version != EngineVersion.V66 && pierceError.Length == 0)
        {
            // The legacy neighbor helper reads signed byte offsets out of this
            // live table. A modified table must not be replaced by stock names.
            for (int i = 0; i < NativeDirections.Length; i++)
            {
                var actual = new CellOffset((sbyte)game.Exe.ReadByte(0x0048DF68 + (uint)(i * 8)),
                    (sbyte)game.Exe.ReadByte(0x0048DF6C + (uint)(i * 8)));
                if (actual != NativeDirections[i])
                {
                    pierceError = "原生邻格方向表 0x0048DF68 已改变，无法以已验证镜像生成元表达；须重新核实该基底";
                    break;
                }
            }
        }
        for (int id = 0; id < count; id++)
        {
            var binding = New(game, RangeKind.Pierce, id, RangeCenterMode.Target);
            if (pierceError.Length > 0) Unsupported(binding, pierceError);
            else if (game.Version == EngineVersion.V66) Read66(game, binding, definitions);
            else ReadLegacy(game, binding);
            if (wrapperKnown && wrapperCenter == RangeCenterMode.Self) binding.Center = RangeCenterMode.Self;
            bindings.Add(binding);
        }
        return bindings;
    }

    private static RangeBinding New(GameBase game, RangeKind kind, int id, RangeCenterMode center) => new()
    {
        Key = new(kind, id), Name = $"{(kind == RangeKind.Attack ? "攻击" : "穿透")}范围 {id:D2}",
        Center = center, ContractId = NativeRangeContract.Id(game, kind),
        Definition = new() { Identity = $"native-{game.VersionText}-{kind}-{id:X2}" }
    };

    private static void Unsupported(RangeBinding binding, string reason)
    {
        binding.Editable = false;
        binding.ReadOnlyReason = reason;
        binding.Special = "原生契约未匹配";
        binding.Source = "Ekd5.exe（待重新核实）";
    }

    private static void ReadSharedSegment(GameBase game, RangeBinding binding, uint slot,
        Dictionary<string, RangeDefinition> definitions)
    {
        try
        {
            uint pointer = game.Exe.ReadU32(slot);
            string identity = $"native-segment-pair-v1-{pointer:X8}";
            if (!definitions.TryGetValue(identity, out var definition))
            {
                definition = new RangeDefinition
                {
                    Identity = identity,
                    Groups = [new RangeGroup { Generators = ReadSegment(game.Exe, pointer) }]
                };
                definitions.Add(identity, definition);
            }
            binding.Definition = definition;
            binding.NativePointer = pointer;
            binding.PointerSlots = [slot];
            binding.Source = $"Ekd5.exe 表槽 0x{slot:X8} → 0x{pointer:X8}，FFFF 终止生成元段";
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            Unsupported(binding, "范围段无法完整读取：" + ex.Message);
        }
    }

    private static List<CellOffset> ReadSegment(PeFile exe, uint pointer)
    {
        var cells = new List<CellOffset>();
        for (int i = 0; i < 512; i++)
        {
            uint at = checked(pointer + (uint)(i * 2));
            if (!exe.TryVaToOffset(at, out int first) || !exe.TryVaToOffset(at + 1, out int second) || second != first + 1)
                throw new InvalidDataException($"0x{at:X8} 越过文件节边界");
            byte x = exe.Bytes[first], y = exe.Bytes[second];
            if (x == 0xFF && y == 0xFF) return cells;
            // Arithmetic in the engine is byte arithmetic. Signed canonical
            // offsets preserve the same mirrored locations without 255-cell views.
            cells.Add(new((sbyte)x, (sbyte)y));
        }
        throw new InvalidDataException($"0x{pointer:X8} 在 512 个生成元内没有 FFFF 终止符");
    }

    private static void Read66(GameBase game, RangeBinding binding, Dictionary<string, RangeDefinition> definitions)
    {
        int id = binding.Key.Id;
        binding.Source = $"Ekd5.exe 穿透分类表 0x{PierceClasses:X8}";
        if (id == 0) { SetNoPierce(binding); return; }
        byte code = game.Exe.ReadByte(PierceClasses + (uint)(id - 1));
        if (code == 0x60)
        {
            binding.Name = "方向双侧";
            binding.Definition.Groups = [SideGroup(1, RangeWeightMode.Constant)];
        }
        else if (code == 0x61)
        {
            int length = id == 5 ? id : id == 4 ? 1 : 2;
            binding.Name = $"方向直线 {length} 格";
            binding.Definition.Groups = [LineGroup(length)];
            binding.RuntimeCacheId = length;
        }
        else if (code <= 4)
        {
            ReadSharedSegment(game, binding, PiercePointers + (uint)(code * 4), definitions);
            if (id == 0x0A)
            {
                binding.Center = RangeCenterMode.Self;
                binding.Name = "自身中心邻格";
            }
        }
        else Unsupported(binding, $"穿透 ID {id:X2} 的分类码 0x{code:X2} 不在已验证的 00..04/60/61 家族内");
    }

    private static void ReadLegacy(GameBase game, RangeBinding binding)
    {
        int id = binding.Key.Id;
        uint table = game.Version == EngineVersion.V64 ? 0x00435F29u : 0x00435EF8u;
        uint target = game.Exe.ReadU32(table + (uint)(id * 4));
        binding.Source = $"Ekd5.exe 跳表 0x{table + id * 4:X8} → 0x{target:X8}";
        switch (target)
        {
            case 0x00435ECA:
                SetNoPierce(binding);
                break;
            case 0x00435D10:
                int directions = id * game.Exe.ReadByte(0x00435D12);
                if (directions is not (4 or 8))
                {
                    Unsupported(binding, $"原生邻格分支当前请求 {directions} 个方向，超出已验证的四向/八向家族");
                    break;
                }
                binding.Definition.Groups = [RingGroup(directions)];
                break;
            case 0x00435D27:
                binding.Definition.Groups = [RingGroup(game.Exe.ReadByte(0x00435D2E))];
                int radius = game.Exe.ReadByte(0x00435D48);
                if (id == 3)
                {
                    var axis = MirrorGroup("轴向附加生成元", [new(radius, 0)]);
                    axis.EmissionOrder = RangeEmissionOrder.LegacyAxis;
                    binding.Definition.Groups.Add(axis);
                }
                else
                {
                    int bandRadius = game.Exe.ReadByte(0x00435D90);
                    int count = game.Exe.ReadByte(0x00435D77);
                    int bias = (sbyte)game.Exe.ReadByte(0x00435D8D);
                    var band = new List<CellOffset>();
                    for (int i = 0; i < count; i++)
                    {
                        int delta = Math.Abs(i + bias);
                        if (delta == 0) band.Add(new(bandRadius, 0));
                        else { band.Add(new(delta, bandRadius)); band.Add(new(bandRadius, delta)); }
                    }
                    var edges = MirrorGroup("边带生成元", band.Distinct().ToList());
                    edges.EmissionOrder = RangeEmissionOrder.LegacyBand;
                    binding.Definition.Groups.Add(edges);
                    if (id != 8)
                    {
                        var axis = MirrorGroup("远轴生成元", [new(game.Exe.ReadByte(0x00435DDA), 0)]);
                        axis.EmissionOrder = RangeEmissionOrder.LegacyAxis;
                        binding.Definition.Groups.Add(axis);
                    }
                }
                break;
            case 0x00435E01:
                int length = id == 5 ? id : id == 4 ? game.Exe.ReadByte(0x00435E3B) : game.Exe.ReadByte(0x00435E41);
                binding.Name = $"方向直线 {length} 格";
                binding.Definition.Groups = [LineGroup(length)];
                binding.RuntimeCacheId = length;
                break;
            case 0x00435E80:
                binding.Name = "方向双侧";
                binding.Definition.Groups = [SideGroup(-1, RangeWeightMode.Ordinal)];
                break;
            case 0x0040CE50 when game.Version == EngineVersion.V65:
                binding.Center = RangeCenterMode.Self;
                binding.ExcludePrimaryTarget = true;
                binding.Name = "自身邻格（排除主目标）";
                binding.Definition.Groups = [RingGroup(game.Exe.ReadByte(0x0040CE6B))];
                binding.Editable = false;
                binding.Special = "原生独立穿透谓词";
                binding.ReadOnlyReason = "6.5 的 0A 调用独立的目标判定分支，不写入通用穿透坐标与衰减缓冲；目前仅按真实邻格展示，保留原生执行语义";
                break;
            case 0x0040D000 when game.Version == EngineVersion.V65:
                int sideCount = 2;
                int lineLength = Math.Max(0, (int)game.Exe.ReadU32(0x0040D04D) - sideCount);
                var line = LineGroup(lineLength);
                line.OrdinalStart = sideCount;
                binding.Name = $"方向双侧 + 直线 {lineLength} 格";
                binding.Definition.Groups = [SideGroup(-1, RangeWeightMode.Ordinal), line];
                // 40D000 enters 435E01 with the two-side loop counter still
                // at 2. The straight loop therefore emits 3 cells, uses
                // ordinal weights 3..5, and leaves the native cache at ID 5.
                binding.RuntimeCacheId = 5;
                break;
            default:
                Unsupported(binding, $"穿透跳表目标 0x{target:X8} 未匹配已验证家族，不能按编号猜测图形");
                break;
        }
    }

    private static void SetNoPierce(RangeBinding binding)
    {
        binding.Name = "无额外穿透";
        binding.Editable = false;
        binding.Special = "无穿透保留编号";
        binding.ReadOnlyReason = "原生编号 0 表示不追加穿透格；如需新范围，请新增独立编号";
    }

    private static RangeGroup MirrorGroup(string name, List<CellOffset> generators) =>
        new() { Name = name, Geometry = RangeGeometryKind.Mirror, Generators = generators, WeightMode = RangeWeightMode.Distance };

    private static RangeGroup RingGroup(int count)
    {
        if (count is not (4 or 8)) throw new InvalidDataException($"原生邻格数量 {count} 不属于已验证家族");
        return new() { Name = "邻格生成元", Generators = count == 4 ? [new(1, 0)] : [new(1, 0), new(1, 1)], WeightMode = RangeWeightMode.Constant, ConstantWeight = 90 };
    }

    private static RangeGroup SideGroup(int firstSide, RangeWeightMode weight) => new()
    {
        Name = "双侧生成元", Geometry = RangeGeometryKind.SidePair, Generators = [new(0, firstSide)],
        WeightMode = weight, ConstantWeight = 90
    };

    private static RangeGroup LineGroup(int length) => new()
    {
        Name = "方向生成元", Geometry = RangeGeometryKind.Directional,
        Generators = Enumerable.Range(1, length).Select(i => new CellOffset(i, 0)).ToList(), WeightMode = RangeWeightMode.Ordinal
    };

    public static IReadOnlyList<CellOffset> Expand(RangeBinding binding, RangePreviewContext context) =>
        Enumerable.Range(0, binding.Definition.Groups.Count).SelectMany(i => ExpandGroup(binding, i, context)).Distinct().ToList();

    public static IReadOnlyList<CellOffset> ExpandGroup(RangeBinding binding, int groupIndex, RangePreviewContext context)
    {
        if (groupIndex < 0 || groupIndex >= binding.Definition.Groups.Count) return [];
        var group = binding.Definition.Groups[groupIndex];
        var placementCenter = binding.Center == RangeCenterMode.Self ? context.Self : context.Target;
        // Placement center and facing reference are independent. A self
        // centered Pierce still points from attacker to target for directional
        // and side-pair groups; using placementCenter here would collapse its
        // heading to (0,0).
        var orientation = new RangePreviewContext(new(
            context.Target.Dx - context.Self.Dx, context.Target.Dy - context.Self.Dy));
        var result = new List<CellOffset>();
        IEnumerable<CellOffset> locals = binding.Key.Kind == RangeKind.Attack &&
            group.Geometry == RangeGeometryKind.Mirror && group.EmissionOrder == RangeEmissionOrder.Geometric
            ? group.Generators.SelectMany(AttackMirror).Distinct()
            : OrderedOffsets(group);
        foreach (var local in locals)
        {
            var offset = group.Geometry switch
            {
                RangeGeometryKind.Mirror => local,
                RangeGeometryKind.Directional => Rotate(local, orientation),
                _ => RotateSide(local, orientation)
            };
            var world = new CellOffset(placementCenter.Dx + offset.Dx, placementCenter.Dy + offset.Dy);
            if (binding.ExcludePrimaryTarget && world == context.Target) continue;
            if (!result.Contains(world)) result.Add(world);
        }
        return result;
    }

    /// <summary>
    /// Ordered local coordinates used by the native pierce families and runtime
    /// compiler. Legacy helper loops are modeled explicitly, rather than relying
    /// on a set whose insertion order happens to look like a stock pattern.
    /// </summary>
    public static IReadOnlyList<CellOffset> OrderedOffsets(RangeGroup group)
    {
        if (group.Geometry == RangeGeometryKind.Directional) return group.Generators.Distinct().ToList();
        if (group.Geometry == RangeGeometryKind.SidePair)
            return group.Generators.SelectMany(g => new[] { g, new CellOffset(g.Dx, -g.Dy) }).Distinct().ToList();
        if (group.EmissionOrder == RangeEmissionOrder.LegacyAxis)
            return group.Generators.SelectMany(LegacyAxis).Distinct().ToList();
        var points = group.Generators.SelectMany(Mirror).Distinct();
        if (group.EmissionOrder == RangeEmissionOrder.LegacyBand)
        {
            var all = points.ToArray();
            // 437968: scan X, emit top then bottom. 4379AF: scan Y,
            // emit left then right. Axis points belong to the matching side.
            return all.Where(c => Math.Abs(c.Dy) > Math.Abs(c.Dx)).OrderBy(c => c.Dx).ThenBy(c => c.Dy)
                .Concat(all.Where(c => Math.Abs(c.Dx) >= Math.Abs(c.Dy)).OrderBy(c => c.Dy).ThenBy(c => c.Dx)).ToList();
        }
        return points.ToList();
    }

    private static IEnumerable<CellOffset> LegacyAxis(CellOffset generator)
    {
        if (generator.Dx != 0 && generator.Dy != 0)
        {
            foreach (var point in Mirror(generator)) yield return point;
            yield break;
        }
        int distance = Math.Max(Math.Abs(generator.Dx), Math.Abs(generator.Dy));
        if (distance == 0) yield break;
        yield return new(distance, 0); yield return new(-distance, 0);
        yield return new(0, -distance); yield return new(0, distance);
    }

    private static IEnumerable<CellOffset> AttackMirror(CellOffset generator)
    {
        int x = generator.Dx, y = generator.Dy;
        if (x != 0 && y != 0)
        {
            yield return new(x, -y); yield return new(-x, -y);
            yield return new(-x, y); yield return new(x, y);
        }
        else
            foreach (var point in LegacyAxis(generator)) yield return point;
    }

    private static IEnumerable<CellOffset> Mirror(CellOffset generator)
    {
        int x = generator.Dx, y = generator.Dy;
        if (x == 0 && y == 0) yield break;
        if (x != 0 && y != 0)
        {
            yield return new(x, -y); yield return new(x, y);
            yield return new(-x, y); yield return new(-x, -y);
        }
        else
        {
            int distance = Math.Max(Math.Abs(x), Math.Abs(y));
            yield return new(0, -distance); yield return new(distance, 0);
            yield return new(0, distance); yield return new(-distance, 0);
        }
    }

    private static CellOffset Heading(RangePreviewContext context)
    {
        int x = Math.Sign(context.Target.Dx), y = Math.Sign(context.Target.Dy);
        return new(x, y);
    }

    private static CellOffset Rotate(CellOffset local, RangePreviewContext context)
    {
        var heading = Heading(context);
        return new(heading.Dx * local.Dx - heading.Dy * local.Dy,
            heading.Dy * local.Dx + heading.Dx * local.Dy);
    }

    private static CellOffset RotateSide(CellOffset local, RangePreviewContext context)
    {
        // 4254FA chooses a side basis by coordinate comparisons, rather than
        // rotating one vector through all quadrants. X equality is tested
        // first, so coincident centers use the same horizontal side pair.
        var basis = SideHeading(context);
        return new(basis.Dx * local.Dx - basis.Dy * local.Dy,
            basis.Dy * local.Dx + basis.Dx * local.Dy);
    }

    private static CellOffset SideHeading(RangePreviewContext context)
    {
        var heading = Heading(context);
        return heading.Dx == 0 ? new(0, -1) : new(1, heading.Dx * heading.Dy);
    }

    public static CellOffset? ToGenerator(RangeBinding binding, int groupIndex, CellOffset worldCell, RangePreviewContext context)
    {
        if (groupIndex < 0 || groupIndex >= binding.Definition.Groups.Count) return null;
        if (binding.ExcludePrimaryTarget && worldCell == context.Target) return null;
        var group = binding.Definition.Groups[groupIndex];
        var placementCenter = binding.Center == RangeCenterMode.Self ? context.Self : context.Target;
        int x = worldCell.Dx - placementCenter.Dx, y = worldCell.Dy - placementCenter.Dy;
        if (x == 0 && y == 0) return null;
        if (group.Geometry == RangeGeometryKind.Mirror)
            return x == 0 || y == 0 ? new(0, Math.Max(Math.Abs(x), Math.Abs(y))) : new(Math.Abs(x), Math.Abs(y));
        var orientation = new RangePreviewContext(new(
            context.Target.Dx - context.Self.Dx, context.Target.Dy - context.Self.Dy));
        var heading = group.Geometry == RangeGeometryKind.SidePair ? SideHeading(orientation) : Heading(orientation);
        int denominator = heading.Dx * heading.Dx + heading.Dy * heading.Dy;
        if (denominator == 0) return null;
        int forward = heading.Dx * x + heading.Dy * y;
        int side = -heading.Dy * x + heading.Dx * y;
        // Diagonal directions span only points whose transformed coordinates
        // are integral; accepting half steps would preview unencodable cells.
        if (forward % denominator != 0 || side % denominator != 0) return null;
        forward /= denominator; side /= denominator;
        // SideHeading is the native basis; normalize the inverse to the
        // stored canonical side sign so either rendered side edits the same
        // generator and the first native candidate remains stable.
        if (group.Geometry == RangeGeometryKind.SidePair)
        {
            int sign = Math.Sign(group.Generators.FirstOrDefault(g => g.Dy != 0)?.Dy ?? 1);
            side = Math.Abs(side) * sign;
        }
        return new(forward, side);
    }
}
