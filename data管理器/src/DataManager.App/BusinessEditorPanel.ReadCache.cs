using System.Data;
using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class BusinessEditorPanel
{
    private sealed record BusinessOptionDirectory(IReadOnlyList<IdNameOption>? Options,
        IReadOnlyDictionary<int, string> Names);
    // Definitions are immutable for a document instance. Weak keys let document
    // replacement release this small schema index independently of UI reuse.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TableDocument,
        IReadOnlyDictionary<string, TableFieldDefinition>> _businessFieldDefinitions = new();
    private static readonly RecordQueryDependency[] QuoteDependencies =
    [
        new("persons", "ID"), new("persons", "名称"), new("persons", "暴击台词"),
        new("person-critical-special-map", "ID"), new("person-critical-special-map", "人物ID"),
        new("person-critical-quotes", "ID"), new("person-critical-quotes", "台词"),
        new("person-retreat-quotes", "ID"), new("person-retreat-quotes", "台词")
    ];
    private static readonly RecordQueryDependency[] EquipmentNameDependencies =
        [new(EquipmentTypeCatalogService.NamesTableKey, "名称")];

    private WorkspaceReadCache ReadCache => WorkspaceReadCache.For(_workspace);

    private TableFieldDefinition? BusinessFieldDefinition(TableDocument document, string name)
        => _businessFieldDefinitions.GetValue(document, source => source.Definition.Fields
            .GroupBy(field => field.Name).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal))
            .GetValueOrDefault(name);

    private IReadOnlyList<IdNameOption>? GetOptions(TableDocument document, int rowIndex, TableFieldDefinition field)
        => OptionDirectory(document, rowIndex, field).Options;

    private BusinessOptionDirectory OptionDirectory(TableDocument document, int rowIndex, TableFieldDefinition field)
    {
        var key = document.Definition.Key;
        var discriminator = string.Empty;
        if (key is "items-low" or "items-high" or "items-extended")
        {
            var semantics = ItemTypeService.Resolve(document, rowIndex);
            discriminator = $"{semantics.Category}:{semantics.IsCategoryMarker}";
        }
        else if (key is "job-series-attributes" || key == StrategyEffectClassService.TableKey)
            discriminator = rowIndex.ToString(CultureInfo.InvariantCulture);
        return ReadCache.GetOrCreateData($"business-options:{key}:{field.Name}:{field.Minimum}:{field.Maximum}:{discriminator}",
            BusinessFieldDependencies([key], field.Name), () =>
            {
                var options = GetOptionsCore(document, rowIndex, field);
                var names = options?.GroupBy(option => option.Id).ToDictionary(group => group.Key, group => group.First().Name)
                    ?? new Dictionary<int, string>();
                return new BusinessOptionDirectory(options, names);
            }, directory => 192L + directory.Names.Sum(entry => 80L + entry.Value.Length * 2L));
    }

    private string? QueryOptionName(TableDocument document, int rowIndex, TableFieldDefinition field, int number)
        => OptionDirectory(document, rowIndex, field).Names.GetValueOrDefault(number);

    private IReadOnlyList<RecordQueryDependency> BusinessFieldDependencies(IEnumerable<string> tableKeys, string fieldName)
    {
        var dependencies = new HashSet<RecordQueryDependency>();
        foreach (var key in tableKeys)
        {
            dependencies.Add(new(key, fieldName));
            if (!_workspace.Documents.TryGetValue(key, out var source)) continue;
            var field = BusinessFieldDefinition(source, fieldName);
            if (field?.ReferenceTable is { Length: > 0 } reference)
                dependencies.UnionWith(ReadCache.GetNavigationDependencies(reference == "items"
                    ? QueryTableKeys("items-low") : [reference]));
            if (key == "persons" && PersonEquipmentService.CategoryForField(fieldName).HasValue)
                foreach (var itemKey in QueryTableKeys("items-low"))
                {
                    dependencies.Add(new(itemKey, "ID")); dependencies.Add(new(itemKey, "名称"));
                    dependencies.Add(new(itemKey, "宝物类型")); dependencies.Add(new(itemKey, "装备特效号"));
                }
            if (key is "items-low" or "items-high" or "items-extended")
            {
                dependencies.Add(new(key, "宝物类型")); dependencies.Add(new(key, "装备特效号"));
                dependencies.UnionWith(EquipmentNameDependencies);
                if (fieldName == "能力成长") dependencies.UnionWith(ReadCache.GetNavigationDependencies(["job-series"]));
            }
            if (fieldName is "装备特效号" or "宝物类型")
            {
                dependencies.Add(new("item-effect-names-low")); dependencies.Add(new("item-effect-names-high"));
                dependencies.Add(new("item-effect-names-66")); dependencies.Add(new("$external"));
            }
            if (key == "strategies" && IsLearningField(fieldName))
                dependencies.UnionWith(ReadCache.GetNavigationDependencies(["detailed-jobs"]));
            if (IsE5RangeField(key, fieldName) || IsNativeRangeField(key, fieldName) || key == StrategyEffectClassService.TableKey)
                dependencies.Add(new("$external"));
        }
        return dependencies.ToArray();
    }

    private PersonQuoteMapping ReadPersonQuoteMapping(int personId)
        => ReadCache.GetOrCreateData($"business-person-quote:{personId}", QuoteDependencies,
            () => new PersonQuoteService(_workspace).Resolve(personId), mapping => 256L + mapping.Evidence.Length * 2L
                + mapping.Critical.Sum(quote => 96L + quote.Text.Length * 2L) + (mapping.Retreat?.Text.Length ?? 0) * 2L);

    private IReadOnlyList<PersonSpecialQuoteSlot> ReadPersonQuoteSlots()
        => ReadCache.GetOrCreateData<IReadOnlyList<PersonSpecialQuoteSlot>>("business-person-quote-slots", QuoteDependencies,
            () => new PersonQuoteService(_workspace).GetSpecialSlots(),
            slots => 128L + slots.Sum(slot => 96L + slot.PersonName.Length * 2L));

    private IReadOnlyList<RecordQueryDependency> LearningDependencies()
    {
        var result = new List<RecordQueryDependency> { new("strategies", "ID"), new("strategies", "名称") };
        if (_workspace.Documents.TryGetValue("strategies", out var source))
            result.AddRange(source.Definition.Fields.Where(field => IsLearningField(field.Name))
                .Select(field => new RecordQueryDependency("strategies", field.Name)));
        return result;
    }

    private IReadOnlyDictionary<int, IReadOnlyList<string>> ReadLearningDirectory()
        => ReadCache.GetOrCreateData<IReadOnlyDictionary<int, IReadOnlyList<string>>>("business-job-learning", LearningDependencies(), () =>
        {
            var result = new Dictionary<int, IReadOnlyList<string>>();
            if (!_workspace.Documents.TryGetValue("strategies", out var source)) return result;
            var fields = source.Definition.Fields.Where(field => IsLearningField(field.Name) && source.Data.Columns.Contains(field.Name))
                .Select(field => (Field: field.Name, Job: int.TryParse(new string(field.Name.Where(char.IsDigit).ToArray()), out var id) ? id : -1))
                .Where(field => field.Job >= 0).ToArray();
            var builders = fields.ToDictionary(field => field.Job, _ => new List<string>());
            foreach (DataRow row in source.Data.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                foreach (var field in fields)
                    if (TryConvertInt(row[field.Field], out var level) && level > 0)
                    {
                        builders[field.Job].Add($"{row["ID"]}：{row["名称"]}");
                        builders[field.Job].Add(level.ToString(CultureInfo.InvariantCulture));
                    }
            }
            foreach (var entry in builders) result.Add(entry.Key, entry.Value.ToArray());
            return result;
        }, directory => 128L + directory.Sum(entry => 64L + entry.Value.Sum(value => 32L + value.Length * 2L)));

    private string EquipmentTypeQueryName(int typeId)
    {
        var names = ReadCache.GetOrCreateData<IReadOnlyDictionary<int, string>>("business-equipment-type-names", EquipmentNameDependencies,
            () => Enumerable.Range(0, 26).ToDictionary(id => id, id => new EquipmentTypeCatalogService().GetTypeDisplayName(_workspace, id)),
            directory => 128L + directory.Sum(entry => 64L + entry.Value.Length * 2L));
        return names.GetValueOrDefault(typeId, $"类型{typeId}");
    }
}
