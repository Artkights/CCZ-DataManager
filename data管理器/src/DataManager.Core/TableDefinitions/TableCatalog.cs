using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Core.TableDefinitions;

// Kept as a small compatibility facade for callers that explicitly request
// the baseline. ProfileRegistry remains the sole runtime definition source.
public static class TableCatalog
{
    public static IReadOnlyList<TableDefinition> CreateDefault()
        => new ProfileRegistry().Resolve("ccz-6.5", "65-standard-v1").Tables;

    public static IReadOnlyList<ModuleDefinition> CreateModules()
        => new ProfileRegistry().Resolve("ccz-6.5", "65-standard-v1").Modules;
}
