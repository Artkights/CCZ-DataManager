namespace RangeManager.App;

public enum RangeKind { Attack, Pierce }
public enum RangeCenterMode { Self, Target }
public enum RangeGeometryKind { Mirror, Directional, SidePair }
public enum RangeWeightMode { Constant, Distance, Ordinal }
public enum RangeEmissionOrder { Geometric, LegacyAxis, LegacyBand }

public readonly record struct RangeKey(RangeKind Kind, int Id)
{
    public override string ToString() => $"{(Kind == RangeKind.Attack ? "攻击" : "穿透")} {Id:D2} (0x{Id:X2})";
}

public sealed class RangeGroup
{
    public string Name { get; set; } = "生成元";
    public RangeGeometryKind Geometry { get; set; } = RangeGeometryKind.Mirror;
    public List<CellOffset> Generators { get; set; } = [];
    public RangeWeightMode WeightMode { get; set; } = RangeWeightMode.Distance;
    public int ConstantWeight { get; set; } = 90;
    public RangeEmissionOrder EmissionOrder { get; set; }
    public int OrdinalStart { get; set; }
    public RangeGroup Clone() => new() { Name = Name, Geometry = Geometry, Generators = [.. Generators], WeightMode = WeightMode, ConstantWeight = ConstantWeight, EmissionOrder = EmissionOrder, OrdinalStart = OrdinalStart };
    public string Text => string.Join("; ", Generators.Select(c => $"{c.Dx},{c.Dy}"));
}

public sealed class RangeDefinition
{
    public string Identity { get; set; } = "";
    public List<RangeGroup> Groups { get; set; } = [];
    public RangeDefinition Clone() => new() { Identity = Identity, Groups = Groups.Select(g => g.Clone()).ToList() };
}

public sealed class RangeBinding
{
    public RangeKey Key { get; set; }
    public RangeDefinition Definition { get; set; } = new();
    public string Name { get; set; } = "";
    public RangeCenterMode Center { get; set; }
    public bool ExcludePrimaryTarget { get; set; }
    public bool Editable { get; set; } = true;
    public string ReadOnlyReason { get; set; } = "";
    public string Special { get; set; } = "";
    public bool IsManaged { get; set; }
    public bool IsModified { get; set; }
    public uint NativePointer { get; set; }
    public List<uint> PointerSlots { get; set; } = [];
    public string Source { get; set; } = "";
    public string ContractId { get; set; } = "";
    public int RuntimeCacheId { get; set; } = -1;
    public RangeBinding Clone(RangeDefinition definition) => new()
    {
        Key = Key, Definition = definition, Name = Name, Center = Center,
        ExcludePrimaryTarget = ExcludePrimaryTarget, Editable = Editable,
        ReadOnlyReason = ReadOnlyReason, Special = Special, IsManaged = IsManaged,
        IsModified = IsModified, NativePointer = NativePointer, PointerSlots = [.. PointerSlots],
        Source = Source, ContractId = ContractId, RuntimeCacheId = RuntimeCacheId
    };
    public override string ToString() => $"{Key.Id:D2}  (0x{Key.Id:X2})  {Name}{(IsModified ? " *" : "")}";
}

public readonly record struct RangePreviewContext(CellOffset Target)
{
    public static RangePreviewContext Default => new(new CellOffset(1, 0));
    public CellOffset Self => new(0, 0);
}

public static class RangeModelCopies
{
    public static List<RangeBinding> Clone(IEnumerable<RangeBinding> source)
    {
        var definitions = new Dictionary<string, RangeDefinition>(StringComparer.Ordinal);
        return source.Select(b =>
        {
            if (!definitions.TryGetValue(b.Definition.Identity, out var definition))
                definitions.Add(b.Definition.Identity, definition = b.Definition.Clone());
            return b.Clone(definition);
        }).ToList();
    }
}
