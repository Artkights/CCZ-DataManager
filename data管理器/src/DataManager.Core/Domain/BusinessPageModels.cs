using System.Globalization;

namespace DataManager.Core.Domain;

/// <summary>Logical pages exposed by the domain layer.</summary>
public enum BusinessPageKind
{
    Persons,
    JobSeries,
    DetailedJobs,
    Items,
    Effects,
    Shop,
    Strategies,
    ExtendData,
    PhysicalTables
}

public sealed record IdNameOption(int Id, string Name)
{
    public string Display => $"{Id.ToString(CultureInfo.InvariantCulture)}：{Name}";
    public override string ToString() => Display;
}

/// <summary>
/// A record assembled from one or more physical rows.  Field keys are
/// qualified as TableKey.FieldName when multiple tables expose the same name.
/// The physical references make it possible for a UI to create a transaction
/// without guessing offsets or file names.
/// </summary>
public sealed record DomainRecord(
    int Id,
    string Name,
    IReadOnlyDictionary<string, object?> Fields,
    IReadOnlyList<PhysicalFieldRef> PhysicalFields)
{
    public object? Get(string fieldName)
        => Fields.TryGetValue(fieldName, out var value)
            ? value
            : Fields.FirstOrDefault(pair => pair.Key.EndsWith("." + fieldName, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed record PhysicalFieldRef(
    string TableKey,
    int RowIndex,
    string FieldName,
    object? Value);

public sealed record BusinessPage(
    BusinessPageKind Kind,
    IReadOnlyList<string> TableKeys,
    IReadOnlyList<DomainRecord> Records);

public sealed record PageCsvExport(string TableKey, string Path, string DisplayName = "");
