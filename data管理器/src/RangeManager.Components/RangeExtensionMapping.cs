namespace RangeManager.App;

/// <summary>
/// The native extended-range table and the optional managed table used for
/// values outside the 33 bytes reserved by the original executable.
/// </summary>
public sealed class RangeExtensionMapping
{
    public const uint TableVa = 0x0041CEAB;
    public const uint MappingFunctionVa = 0x0041CE8B;
    public const uint PredicateVa = 0x0041CDEE;
    public const int NativeCount = 0x21;
    public const int Count = NativeCount;
    public const int MaxRangeValue = 0xFE;
    public const int MaxManagedValue = MaxRangeValue;
    public const int ValueCount = 0x100;

    private readonly byte[] _original;
    private readonly bool _configuredOriginal;
    private byte[] _values;

    private RangeExtensionMapping(byte[] values, bool supported, string reason,
        byte[]? original = null, bool configured = false)
    {
        if (values.Length != ValueCount) throw new ArgumentException("范围映射必须包含 256 项", nameof(values));
        _values = [.. values];
        _original = [.. original ?? values];
        _configuredOriginal = configured;
        Supported = supported;
        Reason = reason;
        HasManagedMappings = configured || values.Skip(NativeCount).Select((v, i) => v != i + NativeCount).Any(x => x);
    }

    public bool Supported { get; }
    public bool Editable => Supported;
    public string Reason { get; }
    public IReadOnlyList<byte> Values => _values;
    public bool IsDirty => !_values.SequenceEqual(_original);
    public bool HasManagedMappings { get; private set; }
    public bool IsConfigured => HasManagedMappings;
    public string ConfigurationStatus => !Supported ? Reason : HasManagedMappings ? "已配置 CCZRNG03 管理映射" : "仅使用原生 33 项映射";
    public string AddressDescription => $"原生映射表 0x{TableVa:X8}（{NativeCount} 项）·逻辑目录 256 项 · 函数 0x{MappingFunctionVa:X8} · 特效判定 0x{PredicateVa:X8}";

    public static RangeExtensionMapping Read(GameBase game)
    {
        if (!TryValidateContract(game, out string reason)) return CreateUnsupported(reason);
        var persisted = ManagedRangeRuntime.ReadExtensionMappings(game);
        if (persisted != null)
        {
            for (int i = 0; i < NativeCount; i++)
                if (game.Exe.ReadByte(TableVa + (uint)i) != persisted.GetValue(i))
                    throw new InvalidDataException("CCZRNG03 原生 33 项与 EXE 映射表不一致，可能发生外部修改");
            return persisted;
        }
        try
        {
            byte[] values = IdentityValues();
            for (int i = 0; i < NativeCount; i++) values[i] = game.Exe.ReadByte(TableVa + (uint)i);
            return new(values, true, "");
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return CreateUnsupported("拓展范围映射表无法读取：" + ex.Message);
        }
    }

    internal static RangeExtensionMapping FromManaged(byte[] values, string? reason = null)
    {
        if (values.Length != ValueCount) throw new InvalidDataException("CCZRNG03 映射目录必须包含 256 项");
        if (values[0xFF] != 0xFF || values.Take(0xFF).Any(v => v == 0xFF))
            throw new InvalidDataException("CCZRNG03 映射目录只能在 0xFF 槽保留 0xFF 哨兵");
        return new(values, true, reason ?? "", configured: true);
    }

    internal static RangeExtensionMapping CreateUnsupported(string reason) => new(IdentityValues(), false, reason);
    internal static byte[] IdentityValues()
    {
        var values = new byte[ValueCount];
        for (int i = 0; i < values.Length; i++) values[i] = (byte)i;
        return values;
    }

    public RangeExtensionMapping Clone() => new(_values, Supported, Reason, _original, HasManagedMappings);

    public byte GetValue(int index)
    {
        if ((uint)index >= ValueCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _values[index];
    }

    public bool TrySet(int index, int value, out string error)
    {
        error = "";
        if (!Supported) { error = Reason; return false; }
        if ((uint)index >= MaxRangeValue + 1) { error = "范围值必须为 0x00..0xFE，0xFF 为保留哨兵"; return false; }
        if ((uint)value >= MaxRangeValue + 1) { error = "下一级范围值必须为 0x00..0xFE，0xFF 为保留哨兵"; return false; }
        _values[index] = (byte)value;
        HasManagedMappings = HasManagedMappings || _values.Skip(NativeCount).Select((v, i) => v != i + NativeCount).Any(x => x);
        return true;
    }

    public void Restore()
    {
        _values = [.. _original];
        HasManagedMappings = _configuredOriginal || _values.Skip(NativeCount).Select((v, i) => v != i + NativeCount).Any(x => x);
    }

    public void ValidateForSave()
    {
        if (!Supported) throw new InvalidDataException(Reason);
        if (_values.Length != ValueCount || _values[0xFF] != 0xFF || _values.Take(0xFF).Any(value => value == 0xFF))
            throw new InvalidDataException("范围映射表只能在 0xFF 槽保留哨兵值");
    }

    public byte[] Apply(byte[] executable, string path)
    {
        ValidateForSave();
        if (HasManagedMappings)
            throw new InvalidOperationException("包含表外映射；请使用 ManagedRangeRuntime.Build(game, bindings, extension) 生成 CCZRNG03 扩展区");
        var pe = new PeFile(path, executable);
        if (!pe.TryVaToOffset(TableVa, out _)) throw new InvalidDataException($"拓展范围映射表 0x{TableVa:X8} 不在 PE 节映射中");
        pe.WriteBytes(TableVa, _values.AsSpan(0, NativeCount));
        return pe.Bytes;
    }

    private static bool TryValidateContract(GameBase game, out string reason)
    {
        reason = "";
        byte[] native;
        try { native = ManagedRangeRuntime.GetNativeBytes(game.Exe); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        { reason = "无法恢复已安装范围扩展的原生代码：" + ex.Message; return false; }
        var exe = new PeFile(game.Exe.Path, native);
        uint entry = game.Version == EngineVersion.V66 ? MappingFunctionVa + 1 : MappingFunctionVa;
        uint tableRead = game.Version == EngineVersion.V66 ? 0x0041CE9Eu : 0x0041CE9Du;
        if (!exe.TryVaToOffset(TableVa, out _) || !exe.TryVaToOffset(PredicateVa, out _) || !exe.TryVaToOffset(entry, out _) || !exe.TryVaToOffset(tableRead, out _))
        { reason = "当前基底缺少拓展范围映射表/映射函数/特效判定地址"; return false; }
        if (!exe.Match(PredicateVa, 0x55, 0x8B, 0xEC, 0x6A, 0x00, 0x6A, 0x03, 0x6A, 0x76, 0x8B, 0x45, 0x08, 0x83, 0xC0, 0x5B, 0x48, 0x50) || !exe.Match(entry, 0x55, 0x8B, 0xEC) || !exe.Match(tableRead, 0x8A, 0x81, 0xAB, 0xCE, 0x41, 0x00))
        { reason = "拓展范围映射函数、查表指令或特效判定代码身份不匹配，当前基底只读"; return false; }
        bool consumer = game.Version == EngineVersion.V66
            ? exe.Match(0x0043038B, 0x55, 0x8B, 0xEC, 0x50, 0x6A, 0x01) && exe.Match(0x00430391, 0xE8, 0xF6, 0xCA, 0xFE, 0xFF)
            : exe.Match(0x00417A35, 0x55, 0x8B, 0xEC) && exe.Match(0x00417A54, 0x50, 0x6A, 0x01);
        if (!consumer) { reason = "拓展范围实际消费入口不匹配，拒绝写入映射表"; return false; }
        return true;
    }
}
