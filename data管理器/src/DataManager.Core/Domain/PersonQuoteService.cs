using System.Data;
using System.Globalization;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

public sealed record PersonQuoteRow(string TableKey, int Id, string Text);
public sealed record PersonQuoteMapping(int PersonId, int CriticalType, bool IsSpecial,
    IReadOnlyList<PersonQuoteRow> Critical, PersonQuoteRow? Retreat, string Evidence);

public enum PersonCriticalQuoteMode { Ordinary, Special }

public sealed record PersonSpecialQuoteSlot(int Id, int PersonId, string PersonName)
{
    public bool IsEmpty => PersonId == PersonQuoteService.EmptySpecialPersonId;
    public string Display => IsEmpty ? $"{Id}：空槽" : $"{Id}：{PersonId}：{PersonName}";
}

public sealed record PersonQuoteAssignmentPreview(
    int PersonId,
    PersonCriticalQuoteMode Mode,
    int CriticalType,
    int? SpecialSlot,
    PersonQuoteMapping Current,
    IReadOnlyList<PersonQuoteRow> Critical,
    PersonQuoteRow? Retreat,
    IReadOnlyList<int> ClearedSpecialSlots,
    int? ConflictingPersonId,
    string Evidence)
{
    public bool RequiresOverwrite => ConflictingPersonId.HasValue;
}

/// <summary>Maps a person's quote selectors to Imsg rows, never by a coincidental row ordinal.</summary>
public sealed class PersonQuoteService(ProjectWorkspace workspace)
{
    public const int EmptySpecialPersonId = 1024;
    public const int SpecialSlotCount = 21;

    /// <summary>The row ID is the Imsg special quote slot; its UInt32 value is a person ID.</summary>
    public IReadOnlyList<PersonSpecialQuoteSlot> GetSpecialSlots()
    {
        if (!workspace.Documents.TryGetValue("person-critical-special-map", out var mappings)) return [];
        return mappings.Data.Rows.Cast<DataRow>().Select(row =>
        {
            var personId = Convert.ToInt32(row["人物ID"], CultureInfo.InvariantCulture);
            var person = FindRow("persons", personId);
            var name = person?.Table.Columns.Contains("名称") == true
                ? Convert.ToString(person["名称"], CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
            return new PersonSpecialQuoteSlot(Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture), personId, name);
        }).OrderBy(slot => slot.Id).ToArray();
    }

    /// <summary>Returns a side-effect-free mode/slot preview, including an occupied-slot conflict.</summary>
    public PersonQuoteAssignmentPreview PreviewAssignment(int personId, PersonCriticalQuoteMode mode,
        int criticalType, int? specialSlot = null)
    {
        EnsureWritableMapping();
        if (personId is < 0 or >= EmptySpecialPersonId) throw new ArgumentOutOfRangeException(nameof(personId));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var current = Resolve(personId);
        var slots = GetSpecialSlots();
        if (slots.Count != SpecialSlotCount || !slots.Select(slot => slot.Id).SequenceEqual(Enumerable.Range(0, SpecialSlotCount)))
            throw new InvalidDataException("特殊暴击分配表必须包含编号 0～20 的 21 个槽位。");
        var cleared = slots.Where(slot => slot.PersonId == personId && (mode == PersonCriticalQuoteMode.Ordinary || slot.Id != specialSlot))
            .Select(slot => slot.Id).ToArray();
        int? conflict = null;
        int[] quoteIds;
        string evidence;
        if (mode == PersonCriticalQuoteMode.Special)
        {
            if (specialSlot is null or < 0 or >= SpecialSlotCount) throw new ArgumentOutOfRangeException(nameof(specialSlot), "特殊槽必须为 0～20。");
            var target = slots.Single(slot => slot.Id == specialSlot);
            if (!target.IsEmpty && target.PersonId != personId) conflict = target.PersonId;
            quoteIds = [specialSlot.Value];
            // Special quotes take precedence. Keep the ordinary selector verbatim as the fallback.
            criticalType = current.CriticalType;
            evidence = $"特殊槽 {specialSlot} 对应 Imsg 暴击台词行 {specialSlot}；保留普通暴击类型 {criticalType}。";
            if (conflict.HasValue) evidence += $" 此槽被人物 {conflict} 占用，覆盖后该人物将使用其普通类型或其他已绑定特殊槽。";
        }
        else
        {
            if (criticalType is < 0 or > 25) throw new ArgumentOutOfRangeException(nameof(criticalType));
            specialSlot = null;
            quoteIds = Enumerable.Range(21 + criticalType * 3, 3).ToArray();
            evidence = $"普通暴击类型 {criticalType} 对应 Imsg 行 {quoteIds[0]}～{quoteIds[^1]}；修改三句文本会影响共享此类型的所有人物。";
        }
        var quotes = quoteIds.Select(id => Quote("person-critical-quotes", id)
            ?? throw new InvalidDataException($"暴击台词表缺少编号 {id}，无法完整提交。")).ToArray();
        if (cleared.Length > 0) evidence += $" 将清除原特殊槽 {string.Join("、", cleared)}，写入空人物编号 {EmptySpecialPersonId}。";
        return new PersonQuoteAssignmentPreview(personId, mode, criticalType, specialSlot, current, quotes,
            current.Retreat, cleared, conflict, evidence);
    }

    /// <summary>Collects all mapping, selector and text edits into the caller's page transaction.</summary>
    public PersonQuoteAssignmentPreview StageAssignment(CompositePageTransaction transaction, int personId,
        PersonCriticalQuoteMode mode, int criticalType, int? specialSlot, IReadOnlyList<string> criticalTexts,
        string? retreatText, bool overwriteOccupiedSlot = false)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(criticalTexts);
        var preview = PreviewAssignment(personId, mode, criticalType, specialSlot);
        if (preview.RequiresOverwrite && !overwriteOccupiedSlot)
            throw new InvalidOperationException($"特殊暴击槽 {preview.SpecialSlot} 已由人物 {preview.ConflictingPersonId} 占用；请查看冲突后显式选择覆盖。");
        if (criticalTexts.Count != preview.Critical.Count)
            throw new InvalidDataException($"当前台词应有 {preview.Critical.Count} 行。");
        if (retreatText != null && preview.Retreat == null)
            throw new InvalidOperationException("此人物不在 0～48 撤退台词映射范围内。");
        foreach (var slot in preview.ClearedSpecialSlots)
            transaction.SetById("person-critical-special-map", slot, "人物ID", EmptySpecialPersonId);
        if (mode == PersonCriticalQuoteMode.Special)
            transaction.SetById("person-critical-special-map", preview.SpecialSlot!.Value, "人物ID", personId);
        else transaction.SetById("persons", personId, "暴击台词", criticalType);
        for (var index = 0; index < preview.Critical.Count; index++)
            transaction.SetById("person-critical-quotes", preview.Critical[index].Id, "台词", criticalTexts[index]);
        if (retreatText != null)
            transaction.SetById("person-retreat-quotes", preview.Retreat!.Id, "台词", retreatText);
        return preview;
    }

    public CompositeApplyResult ApplyAssignment(int personId, PersonCriticalQuoteMode mode, int criticalType,
        int? specialSlot, IReadOnlyList<string> criticalTexts, string? retreatText, bool overwriteOccupiedSlot = false)
    {
        var transaction = new BusinessDataService(workspace).BeginTransaction(BusinessPageKind.Persons, $"人物 {personId} 台词模式与文本");
        StageAssignment(transaction, personId, mode, criticalType, specialSlot, criticalTexts, retreatText, overwriteOccupiedSlot);
        return transaction.Apply();
    }

    private void EnsureWritableMapping()
    {
        if (!workspace.Documents.TryGetValue("person-critical-special-map", out var mapping) ||
            mapping.Definition.Capability != TableCapability.Editable)
            throw new InvalidOperationException("特殊暴击人物映射未经 PE 引用验证，无法确定人物实际台词，当前只读。");
    }

    public PersonQuoteMapping Resolve(int personId)
    {
        var person = FindRow("persons", personId) ?? throw new KeyNotFoundException($"人物 {personId} 不存在。");
        var type = Convert.ToInt32(person["暴击台词"], CultureInfo.InvariantCulture);
        var special = workspace.Documents.TryGetValue("person-critical-special-map", out var mappings)
            ? mappings.Data.Rows.Cast<DataRow>().FirstOrDefault(row => Convert.ToInt32(row["人物ID"], CultureInfo.InvariantCulture) == personId)
            : null;
        var ids = special is null ? Enumerable.Range(21 + type * 3, 3) : [Convert.ToInt32(special["ID"], CultureInfo.InvariantCulture)];
        var critical = ids.Select(id => Quote("person-critical-quotes", id)).Where(row => row != null).Cast<PersonQuoteRow>().ToArray();
        var retreat = personId is >= 0 and < 49 ? Quote("person-retreat-quotes", personId) : null;
        var evidence = special is null
            ? $"普通暴击类型 {type} 对应 Imsg 行 {21 + type * 3}–{23 + type * 3}，编辑会影响共享该类型的人物。"
            : $"特殊暴击槽 {special["ID"]} 经 PE 引用验证，优先于人物暴击类型字段。";
        evidence += retreat is null ? " 此人物没有 0..48 的专属撤退行。" : $" 撤退文本按人物 ID {personId} 读取，人物字段另行保留。";
        if (!workspace.Documents.ContainsKey("person-critical-special-map")) evidence += " 特殊人物映射缺少验证证据；普通台词仅供查看。";
        return new PersonQuoteMapping(personId, type, special != null, critical, retreat, evidence);
    }

    public CompositeApplyResult Apply(int personId, int criticalType, IReadOnlyList<string> criticalTexts, string? retreatText)
    {
        if (criticalType is < 0 or > 25) throw new ArgumentOutOfRangeException(nameof(criticalType));
        if (!workspace.Documents.ContainsKey("person-critical-special-map"))
            throw new InvalidOperationException("特殊暴击人物映射未经 PE 引用验证，无法确定人物实际台词，当前只读。");
        var mapping = Resolve(personId);
        var transaction = new BusinessDataService(workspace).BeginTransaction(BusinessPageKind.Persons, $"人物 {personId} 台词");
        transaction.SetById("persons", personId, "暴击台词", criticalType);
        var quoteIds = mapping.IsSpecial ? mapping.Critical.Select(row => row.Id).ToArray() : Enumerable.Range(21 + criticalType * 3, 3).ToArray();
        if (criticalTexts.Count != quoteIds.Length) throw new InvalidDataException($"当前台词应有 {quoteIds.Length} 行。");
        for (var i = 0; i < quoteIds.Length; i++) transaction.SetById("person-critical-quotes", quoteIds[i], "台词", criticalTexts[i]);
        if (retreatText != null && mapping.Retreat != null) transaction.SetById("person-retreat-quotes", mapping.Retreat.Id, "台词", retreatText);
        return transaction.Apply();
    }

    private PersonQuoteRow? Quote(string key, int id)
        => FindRow(key, id) is { } row ? new PersonQuoteRow(key, id, Convert.ToString(row["台词"], CultureInfo.InvariantCulture) ?? string.Empty) : null;
    private DataRow? FindRow(string key, int id)
        => workspace.Documents.TryGetValue(key, out var document)
            ? document.Data.Rows.Cast<DataRow>().FirstOrDefault(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture) == id) : null;
}
