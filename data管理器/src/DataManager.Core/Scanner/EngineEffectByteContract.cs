namespace DataManager.Core.Scanner;

/// <summary>
/// Only the proven equipment/personal selector channels consume the low byte.
/// push imm8 FF sign extends to FFFFFFFF, but denotes logical selector 255.
/// Value and aggregation modes deliberately do not use this contract.
/// </summary>
internal static class EngineEffectByteContract
{
    public static int? Decode(uint? value) => value switch
    {
        <= byte.MaxValue => (int)value.Value,
        >= 0xFFFFFF80 => (int)(value.Value & byte.MaxValue),
        _ => null
    };
}
