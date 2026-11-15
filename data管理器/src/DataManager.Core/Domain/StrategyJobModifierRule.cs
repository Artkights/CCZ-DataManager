namespace DataManager.Core.Domain;

public enum StrategyModifierSide : byte
{
    Receiver = 0,
    Caster = 1
}

public enum StrategyModifierMatchKind : byte
{
    StrategyType = 0,
    StrategyId = 1
}

/// <summary>
/// A user-defined strategy damage/hit modifier. The rule is compiled into the
/// .strh section and evaluated by the native hook after the built-in engine
/// modifiers have run.
/// </summary>
public sealed record StrategyJobModifierRule(
    StrategyModifierSide Side,
    StrategyModifierMatchKind MatchKind,
    byte MatchValue,
    byte Series,
    sbyte DamagePercent,
    sbyte HitPercent,
    bool Enabled,
    string Note)
{
    public const int Capacity = 64;
    public const int HeaderSize = 16;
    public const int RuleSize = 8;
    public const int SectionSize = 0x2000;
    public const int StateJsonSize = 0x0300;
    public const int MaxNoteLength = 40;

    public static StrategyJobModifierRule CreateDefault(byte fireSeries) => new(
        StrategyModifierSide.Receiver,
        StrategyModifierMatchKind.StrategyType,
        0,
        fireSeries,
        40,
        0,
        true,
        string.Empty);

    public string Key => $"{(byte)Side}:{(byte)MatchKind}:{MatchValue}:{Series}";
}

public enum StrategyJobModifierIssueSeverity
{
    Warning,
    Error
}

public sealed record StrategyJobModifierIssue(
    StrategyJobModifierIssueSeverity Severity,
    string Message,
    int RuleIndex = -1);

public sealed record StrategyJobModifierValidation(IReadOnlyList<StrategyJobModifierIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == StrategyJobModifierIssueSeverity.Error);
}

public sealed record StrategyJobModifierSnapshot(
    IReadOnlyList<StrategyJobModifierRule> Rules,
    bool HookInstalled,
    string Version,
    string ProfileId,
    string VariantId,
    uint DamageHookVa,
    uint HitHookVa,
    uint SectionVa,
    string Diagnostic,
    StrategyBuiltInRuleInfo BuiltIn);

public sealed record StrategyJobModifierLayout(
    string Version,
    uint DamageHookVa,
    uint HitHookVa,
    byte[] DamageOriginal,
    byte[] HitOriginal,
    uint DamageReturnVa,
    uint HitReturnVa,
    string SectionName,
    uint SectionVa,
    uint SectionRawPointer,
    uint SectionRawSize,
    uint ImageBase,
    uint FileAlignment,
    uint SectionAlignment,
    byte[] OriginalSectionHeader,
    int OriginalSectionHeaderOffset,
    uint OriginalSizeOfImage,
    uint OriginalSizeOfCode,
    uint OriginalSizeOfInitializedData);
