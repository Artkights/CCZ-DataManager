using System.Runtime.CompilerServices;

namespace DataManager.Core.Models;

/// <summary>Definitions are immutable; index by their actual identity so a resolved layout is never reused accidentally.</summary>
internal static class TableFieldIndex
{
    private static readonly ConditionalWeakTable<TableDefinition, Dictionary<string, TableFieldDefinition>> Index = new();

    public static TableFieldDefinition? Find(TableDefinition definition, string name)
        => Index.GetValue(definition, table => table.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal))
            .GetValueOrDefault(name);
}
