namespace DataManager.Core.Models;

public enum FieldKind
{
    FixedString,
    UInt8,
    UInt16,
    UInt32,
    RawBytes
}

public enum FieldPolicy
{
    Editable,
    ReadOnly,
    Preserve
}

public enum VerificationLevel
{
    BaselineVerified,
    VariantVerified,
    SourceReviewed,
    InheritedUnverified
}

/// <summary>
/// Describes whether a physical table is safe to write for the active
/// profile/layout.  VerificationLevel records the evidence source; this
/// capability is the runtime write gate exposed to domain and UI layers.
/// </summary>
public enum TableCapability
{
    Editable,
    ReadOnly,
    Unavailable
}

public sealed record TableFieldDefinition(
    string Name,
    int Offset,
    int Size,
    FieldKind Kind,
    FieldPolicy Policy = FieldPolicy.Editable,
    long Minimum = 0,
    long Maximum = 255,
    string? ReferenceTable = null,
    string? CsvName = null,
    DataManager.Core.Binary.TextFieldConstraint? TextConstraint = null)
{
    public string ExportName => CsvName ?? Name;
}

public sealed record TableDefinition(
    string Key,
    string DisplayName,
    string FileName,
    long DataOffset,
    int RowCount,
    int RowSize,
    int BeginId,
    IReadOnlyList<TableFieldDefinition> Fields,
    string Module,
    string? ParentKey = null,
    string RecordCodecKey = "fixed-record-v1",
    VerificationLevel VerificationLevel = VerificationLevel.BaselineVerified,
    string LayoutSource = "ccz-6.5",
    TableCapability Capability = TableCapability.Editable,
    string CapabilityEvidence = "")
{
    public long EndOffsetExclusive => DataOffset + (long)RowCount * RowSize;
    public bool IsVariableLengthText => Fields.Any(field => field.Kind == FieldKind.FixedString);
    public bool IsWriteSupported => Capability == TableCapability.Editable && Fields.Any(field => field.Policy == FieldPolicy.Editable);
}

public sealed class TableValidation
{
    public required TableDefinition Definition { get; init; }
    public required string Path { get; init; }
    public bool FileExists { get; init; }
    public long ActualLength { get; init; }
    public bool HasEnoughBytes { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsUsable => Definition.Capability != TableCapability.Unavailable && FileExists && HasEnoughBytes;
}
