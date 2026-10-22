using DataManager.Core.Models;

namespace DataManager.Core.Csv;

/// <summary>Chinese exchange labels are aliases of the physical schema, never new persisted fields.</summary>
public static class CsvDisplayNameRegistry
{
    public const string IdColumnName = "编号";

    private static readonly IReadOnlyDictionary<string, string> TableNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["persons"] = "人物",
        ["person-biography"] = "人物列传",
        ["person-critical-quotes"] = "人物暴击台词",
        ["person-retreat-quotes"] = "人物撤退台词",
        ["person-critical-special-map"] = "特殊暴击人物分配",
        ["person-r-assign"] = "人物R形象指定",
        ["person-s-assign"] = "人物S形象指定",
        ["person-r-images"] = "人物R形象数据",
        ["person-s-images"] = "人物S形象数据",
        ["job-series"] = "兵种系",
        ["job-series-terrain"] = "兵种系-地形发挥",
        ["job-series-move-cost"] = "兵种系-移动消耗",
        ["job-series-counter"] = "兵种系-兵种克制",
        ["job-series-attributes"] = "兵种系-兵种属性",
        ["job-series-accessory-groups"] = "兵种系-辅助装备分组",
        ["detailed-jobs"] = "兵种",
        ["detailed-job-description"] = "兵种说明",
        ["detailed-job-growth"] = "兵种成长与装备许可",
        ["detailed-job-penetration"] = "兵种穿透范围",
        ["items-low"] = "宝物（0-103）",
        ["items-high"] = "宝物（104-254）",
        ["items-extended"] = "宝物扩展（256-511）",
        ["item-description-low"] = "宝物说明（0-103）",
        ["item-description-high"] = "宝物说明（104-254）",
        ["item-description-extended"] = "宝物扩展说明（256-511）",
        ["item-effect-names-low"] = "装备特效名称池（低段）",
        ["item-effect-names-high"] = "装备特效名称池（高段）",
        ["item-effect-names-66"] = "装备特效名称池（六点六）",
        ["equipment-type-names"] = "装备类型名称",
        ["equipment-type-display"] = "装备类型显示",
        ["effect-names"] = "兵种特效名称",
        ["scenario-names"] = "战役名称",
        ["shops"] = "商店数据",
        ["strategies"] = "策略",
        ["strategy-description"] = "策略说明",
        ["strategy-extension"] = "策略扩展标记",
        ["strategy-damage-type"] = "策略伤血类型",
        ["strategy-damage-ratio"] = "策略伤害系数",
        ["strategy-hit-rate"] = "策略命中率",
        ["strategy-learning"] = "策略效果索引",
        ["strategy-battle-ai"] = "策略战场智能限制",
        ["strategy-training-ai"] = "策略练武场智能限制",
        ["strategy-terrain-availability"] = "策略施展地形",
        ["strategy-big-animation"] = "策略大动画",
        ["strategy-small-animation"] = "策略小动画",
        ["effect-archive"] = "拓展特效",
        ["effect-messages"] = "拓展特效成长",
        ["strategy-study-extended"] = "拓展策略学习",
        ["forge"] = "锻造"
    };

    private static readonly IReadOnlyDictionary<string, string> FieldNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Army"] = "部队原始值",
        ["Enabled"] = "启用",
        ["Raw"] = "原始字节",
        ["RawHex"] = "原始十六进制",
        ["GroupID"] = "分组编号",
        ["JobSeriesIDs"] = "兵种系编号列表",
        ["消耗SP"] = "消耗点数"
    };

    public static string GetTableDisplayName(TableDefinition definition)
        => TableNames.TryGetValue(definition.Key, out var name) ? name : Localize(definition.DisplayName);

    public static string GetTableDisplayName(string tableKey, string? fallback = null)
        => TableNames.TryGetValue(tableKey, out var name) ? name : Localize(fallback ?? tableKey);

    public static string GetFileName(TableDefinition definition) => SanitizeFileName(GetTableDisplayName(definition)) + ".csv";

    public static string GetColumnName(TableFieldDefinition field) => Localize(field.ExportName);

    public static bool IsIdHeader(string value)
        => value.Equals("ID", StringComparison.OrdinalIgnoreCase) || value is "编号" or "记录编号";

    public static bool MatchesField(TableFieldDefinition field, string header)
        => header.Equals(field.Name, StringComparison.OrdinalIgnoreCase) ||
           header.Equals(field.ExportName, StringComparison.OrdinalIgnoreCase) ||
           header.Equals(GetColumnName(field), StringComparison.OrdinalIgnoreCase) ||
           header.Equals(Localize(field.Name), StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a legacy key or a Chinese file stem. Ambiguous aliases are rejected.</summary>
    public static string ResolveTableKey(string keyOrFileName, IEnumerable<TableDefinition> definitions)
    {
        var fileName = Path.GetFileName(keyOrFileName);
        var token = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        var matches = definitions.Where(definition => token.Equals(definition.Key, StringComparison.OrdinalIgnoreCase) ||
            token.Equals(definition.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            token.Equals(GetTableDisplayName(definition), StringComparison.OrdinalIgnoreCase) ||
            token.Equals(SanitizeFileName(GetTableDisplayName(definition)), StringComparison.OrdinalIgnoreCase) ||
            definition.Key == "effect-messages" && token is "拓展特效说明" or "特效说明拓展")
            .Select(definition => definition.Key).Distinct().ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"CSV 名称未对应当前页面的物理表：{keyOrFileName}。"),
            _ => throw new InvalidDataException($"CSV 中文名称对应多张物理表：{keyOrFileName}；请使用元数据中的表标识。")
        };
    }

    private static string Localize(string value)
    {
        if (IsIdHeader(value)) return IdColumnName;
        if (FieldNames.TryGetValue(value, out var alias)) return alias;
        return value.Replace("HP", "气血", StringComparison.OrdinalIgnoreCase)
            .Replace("MP", "策略值", StringComparison.OrdinalIgnoreCase)
            .Replace("ID", "编号", StringComparison.OrdinalIgnoreCase)
            .Replace("AI", "智能", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string value)
        => new(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
}
