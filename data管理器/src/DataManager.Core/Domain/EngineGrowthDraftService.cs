using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;
using EngineGrowth.Core;
using TransformManager.App;
using TierManager.App;
using GrowthItemCategory = TransformManager.App.ItemCategory;

namespace DataManager.Core.Domain;

/// <summary>One global growth draft per workspace; UI selection never owns the draft.</summary>
public sealed class EngineGrowthDraftService : IWorkspaceDraftParticipant
{
    private static readonly ConditionalWeakTable<ProjectWorkspace, EngineGrowthDraftService> Instances = new();
    private readonly ProjectWorkspace _workspace;
    private string _projectKey = "";
    private byte[] _sourceBytes = [];
    private TreasureTransformService? _transform;
    private TierProfile? _tierProfile;
    private ItemCatalog _catalog = ItemCatalog.FromItems([]);
    private Dictionary<int, List<RuleStage>> _savedRules = [];
    private Dictionary<int, List<RuleStage>> _rules = [];
    private List<TierEntry> _savedTiers = [];
    private List<TierEntry> _tiers = [];
    private readonly HashSet<int> _touchedEffects = [];
    private IReadOnlyList<EffectSlot> _slots = [];
    private EngineGrowthDraftService(ProjectWorkspace workspace) { _workspace = workspace; Reload(true); }
    public static EngineGrowthDraftService For(ProjectWorkspace workspace)
    {
        var result = Instances.GetValue(workspace, current => new(current));
        if (result._projectKey != Key(workspace)) result.Reload(true);
        workspace.RegisterDraftParticipant(result);
        return result;
    }
    private static string Key(ProjectWorkspace workspace) => $"{workspace.RootPath}|{workspace.ActiveProfile.ProfileId}|{workspace.ActiveProfile.VariantId}|{workspace.ActiveProfile.LayoutRevision}";
    public event EventHandler? Changed;
    public IReadOnlyList<EffectSlot> Slots => _slots;
    public IReadOnlyList<TierEntry> TierEntries => _tiers.Select(GrowthPatchCoordinator.CloneTier).ToArray();
    public int TierBase => _tierProfile?.TierBase ?? 1;
    public string SourceHash { get; private set; } = "";
    public string ExecutablePath => Path.Combine(_workspace.RootPath, "Ekd5.exe");
    public bool CanEditRules { get; private set; }
    public bool CanEditTiers { get; private set; }
    public string RulesReadOnlyReason { get; private set; } = "尚未扫描。";
    public string TiersReadOnlyReason { get; private set; } = "尚未扫描。";
    public bool HasRuleChanges => RuleSignature(_rules) != RuleSignature(_savedRules);
    public bool HasTierChanges => TierSignature(_tiers) != TierSignature(_savedTiers);
    public bool HasPendingChanges => HasRuleChanges || HasTierChanges;

    public void Reload(bool discardChanges = false)
    {
        if (HasPendingChanges && !discardChanges) { RefreshReferences(); return; }
        _projectKey = Key(_workspace);
        _touchedEffects.Clear(); _rules.Clear(); _savedRules.Clear(); _tiers.Clear(); _savedTiers.Clear();
        CanEditRules = CanEditTiers = false;
        RulesReadOnlyReason = TiersReadOnlyReason = "尚未打开有效项目。";
        _transform = null; _tierProfile = null; _sourceBytes = []; SourceHash = "";
        try
        {
            _sourceBytes = File.ReadAllBytes(ExecutablePath);
            SourceHash = Convert.ToHexString(SHA256.HashData(_sourceBytes));
            _catalog = ReadCatalog();
            _transform = TreasureTransformService.Load(_workspace.RootPath, _sourceBytes, _catalog);
            _tierProfile = TierScanner.LoadFromBytes(ExecutablePath, _sourceBytes, _workspace.RootPath);
            _savedRules = CloneRules(_transform.Rules); _rules = CloneRules(_savedRules);
            _savedTiers = _tierProfile.Entries.Select(GrowthPatchCoordinator.CloneTier).ToList();
            _tiers = _savedTiers.Select(GrowthPatchCoordinator.CloneTier).ToList();
            RulesReadOnlyReason = _transform.Base.ReadOnlyReason;
            if (_transform.ContractErrors.Count > 0) RulesReadOnlyReason = string.Join("；", _transform.ContractErrors);
            CanEditRules = RulesReadOnlyReason.Length == 0;
            TiersReadOnlyReason = _tierProfile.ReadOnlyReason;
            CanEditTiers = _tierProfile.CanWrite;
            if (!CanEditRules && CanMigrateVerifiedLegacyCoexistence())
            { CanEditRules = true; RulesReadOnlyReason = ""; }
            if (!CanEditTiers && TiersReadOnlyReason == "没有足够大的安全代码洞。" && _transform.Base.IsPatched)
            { CanEditTiers = true; TiersReadOnlyReason = ""; }
            // The coordinator can safely repack verified legacy whole-cave markers.
            // A missing cave still remains a read-only condition until preparation.
            if (_transform.Base.Cave == null && !_tierProfile.HasMarker)
            { CanEditRules = false; if (RulesReadOnlyReason.Length == 0) RulesReadOnlyReason = "没有可用的安全代码洞。"; }
        }
        catch (Exception ex) { RulesReadOnlyReason = TiersReadOnlyReason = ex.Message; }
        RebuildSlots(); Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanMigrateVerifiedLegacyCoexistence()
    {
        const string referencedCaveReason = "已安装规则的代码洞位于被数据引用的区域";
        if (_transform is not { Base.IsPatched: true } transform || _tierProfile is not { HasMarker: true } tiers ||
            transform.ContractErrors.Count != 0 || !RulesReadOnlyReason.StartsWith(referencedCaveReason, StringComparison.Ordinal) ||
            transform.Base.Cave is not { } cave ||
            cave.Address >= (ulong)tiers.CaveVa + (uint)tiers.CaveLength ||
            tiers.CaveVa >= (ulong)cave.Address + (uint)cave.Length)
            return false;
        try
        {
            var pe = transform.Base.Exe;
            if (!new MarkerReader(pe, tiers.MarkerVa).TryRead(out var marker)) return false;
            int transformUsed = RuleCompiler.RequiredSize(transform.Rules.Select(pair => new EffectRule(pair.Key, pair.Value)).ToArray(),
                cave.Address, transform.Base.TransformEntryVa, cave.OriginalBytes[0], cave.Length);
            int tierUsed = checked((int)(tiers.MarkerVa - tiers.CaveVa)) + MarkerCodec.Build(marker).Length;
            var knownRanges = new List<(int Offset, int Length)>
            {
                (pe.VaToOffset(cave.Address), transformUsed),
                (pe.VaToOffset(tiers.CaveVa), tierUsed),
                (pe.VaToOffset(transform.Base.TransformEntryVa), 5),
                (pe.VaToOffset(tiers.DisplayPointerVa), 5)
            };
            knownRanges.AddRange(tiers.CallSites.Select(site => (pe.VaToOffset(site), 5)));
            // The old full-cave claim can include managed tier pointers. Only
            // recognized, completely reproducible managed bytes may explain
            // those references; an external pointer must remain read-only.
            for (int offset = 0; offset <= _sourceBytes.Length - 4; offset++)
            {
                uint target = BitConverter.ToUInt32(_sourceBytes, offset);
                if (target >= cave.Address && target < (ulong)cave.Address + (uint)cave.Length &&
                    !knownRanges.Any(range => offset >= range.Offset && (long)offset + 4 <= (long)range.Offset + range.Length))
                    return false;
            }
            var plan = GrowthPatchCoordinator.Prepare(_workspace.RootPath, _sourceBytes, _catalog, null, null);
            plan.Verify(plan.AfterBytes);
            var migratedRules = TreasureTransformService.Load(_workspace.RootPath, plan.AfterBytes, _catalog);
            var migratedTiers = TierScanner.LoadFromBytes(ExecutablePath, plan.AfterBytes, _workspace.RootPath);
            return migratedRules.Base.CanEdit && migratedTiers.CanWrite &&
                RuleSignature(migratedRules.Rules) == RuleSignature(_savedRules) && migratedTiers.TierBase == tiers.TierBase &&
                TierSignature(migratedTiers.Entries) == TierSignature(_savedTiers);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException)
        { return false; }
    }

    public void RefreshReferences()
    {
        _catalog = ReadCatalog(); RebuildSlots(); Changed?.Invoke(this, EventArgs.Empty);
    }

    private ItemCatalog ReadCatalog()
    {
        var refs = new List<ItemRef>();
        foreach (var key in new[] { "items-low", "items-high", "items-extended" })
        {
            if (!_workspace.Documents.TryGetValue(key, out var document)) continue;
            for (int index = 0; index < document.Data.Rows.Count; index++)
            {
                var row = document.Data.Rows[index]; if (row.RowState == DataRowState.Deleted) continue;
                var semantic = ItemSemanticResolver.Resolve(document, index);
                var category = semantic.Category switch
                {
                    ItemCategory.Weapon => GrowthItemCategory.Weapon,
                    ItemCategory.Armor => GrowthItemCategory.Armor,
                    _ => GrowthItemCategory.Unknown
                };
                if (category == GrowthItemCategory.Unknown) continue;
                int id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                refs.Add(new ItemRef(id, Convert.ToString(row["名称"], CultureInfo.InvariantCulture) ?? "",
                    category, semantic.Semantics.CategoryName, semantic.Semantics.CategoryName,
                    semantic.ActualEffectNumber, semantic.EffectValue ?? 0,
                    Path.GetFileName(document.FilePath).Equals("Star.e5", StringComparison.OrdinalIgnoreCase)));
            }
        }
        return ItemCatalog.FromItems(refs);
    }

    private void RebuildSlots()
    {
        if (_transform == null) { _slots = []; return; }
        var game = _transform.Base;
        int last = game.CoverageLast > 0 ? game.CoverageLast : 0x7E;
        _slots = Enumerable.Range(game.CoverageFirst, Math.Max(0, last - game.CoverageFirst + 1))
            .Where(id => _catalog.ForEffect(id).Count > 0 || _savedRules.ContainsKey(id) || _rules.ContainsKey(id) || _touchedEffects.Contains(id))
            .Select(id => new EffectSlot
            {
                Id = id, Name = game.EffectName(id), Coverage = game.ReadCoverage(id), Extra = game.ReadExtra(id),
                Refs = _catalog.ForEffect(id), Stages = _rules.TryGetValue(id, out var stages) ? stages.ToList() : []
            }).ToArray();
    }

    public void SetRules(int effectId, IReadOnlyList<RuleStage> stages)
    {
        if (!CanEditRules || _transform == null) throw new InvalidOperationException(RulesReadOnlyReason);
        if (effectId < _transform.Base.CoverageFirst || effectId > _transform.Base.CoverageLast)
            throw new InvalidDataException("特效号不在当前质变表范围内。");
        GrowthPatchCoordinator.ValidateRules(stages);
        var before = Snapshot();
        var next = CloneRules(_rules);
        if (stages.Count == 0) next.Remove(effectId); else next[effectId] = stages.OrderByDescending(s => s.Level).ToList();
        if (RuleSignature(next) == RuleSignature(_rules)) return;
        _touchedEffects.Add(effectId);
        _rules = next;
        CompleteEdit(before, $"质变规则 0x{effectId:X2}");
    }

    public void SetTiers(IReadOnlyList<TierEntry> rows)
    {
        if (!CanEditTiers || _tierProfile == null) throw new InvalidOperationException(TiersReadOnlyReason);
        var error = TierPatchBuilder.Validate(_tierProfile, rows, out _, out _);
        if (error != null) throw new InvalidDataException(error);
        if (TierSignature(rows) == TierSignature(_tiers)) return;
        var before = Snapshot();
        _tiers = rows.Select((row, i) => new TierEntry
        { Ordinal = TierBase + i, Threshold = row.Threshold, Label = row.Label, OriginalReturn = checked((byte)(TierBase + i)) }).ToList();
        CompleteEdit(before, "全局档位设定");
    }

    private sealed record DraftSnapshot(Dictionary<int, List<RuleStage>> Rules, List<TierEntry> Tiers, int[] Touched);
    private DraftSnapshot Snapshot() => new(CloneRules(_rules), _tiers.Select(GrowthPatchCoordinator.CloneTier).ToList(), _touchedEffects.ToArray());
    private void Restore(DraftSnapshot snapshot)
    {
        _rules = CloneRules(snapshot.Rules); _tiers = snapshot.Tiers.Select(GrowthPatchCoordinator.CloneTier).ToList();
        _touchedEffects.Clear(); _touchedEffects.UnionWith(snapshot.Touched);
        RebuildSlots(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void CompleteEdit(DraftSnapshot before, string description)
    {
        var after = Snapshot();
        _workspace.Commands.Push(new DelegateEditCommand(description, () => Restore(before), () => Restore(after)));
        RebuildSlots(); Changed?.Invoke(this, EventArgs.Empty);
    }

    public GrowthPatchPlan PreparePlan()
    {
        if (EngineEffectWriteService.IsGameRunning(_workspace)) throw new IOException("游戏正在运行，请先退出游戏再保存引擎设定。");
        if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ExecutablePath))).Equals(SourceHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ekd5.exe 在扫描后被外部修改，请重新载入后再保存。");
        var edits = _savedRules.Keys.Union(_rules.Keys).Where(id =>
            StageSignature(_savedRules.GetValueOrDefault(id) ?? []) != StageSignature(_rules.GetValueOrDefault(id) ?? []))
            .Select(id => new SlotEdit(id, null, _rules.GetValueOrDefault(id) ?? [])).ToArray();
        return GrowthPatchCoordinator.Prepare(_workspace.RootPath, _sourceBytes, _catalog,
            edits.Length > 0 ? edits : null, HasTierChanges ? _tiers : null);
    }

    public IReadOnlyList<PreparedBinaryEdit> PrepareBinaryEdits()
    {
        if (!HasPendingChanges) return [];
        var plan = PreparePlan();
        var patches = new List<PreparedBytePatch>();
        for (int i = 0; i < plan.BeforeBytes.Length; i++)
        {
            if (plan.BeforeBytes[i] == plan.AfterBytes[i]) continue;
            int start = i++;
            while (i < plan.BeforeBytes.Length && plan.BeforeBytes[i] != plan.AfterBytes[i]) i++;
            patches.Add(new(start, plan.BeforeBytes[start..i], plan.AfterBytes[start..i])); i--;
        }
        return patches.Count == 0 ? [] : [new("Ekd5.exe", SourceHash, patches, "质变与档位", plan.Summary, plan.Verify)];
    }

    public Action CaptureSaveRecovery()
    {
        var snapshot = Snapshot(); var source = _sourceBytes; var hash = SourceHash;
        var savedRules = CloneRules(_savedRules); var savedTiers = _savedTiers.Select(GrowthPatchCoordinator.CloneTier).ToList();
        var transform = _transform; var profile = _tierProfile; var catalog = _catalog; var projectKey = _projectKey;
        var canRules = CanEditRules; var canTiers = CanEditTiers;
        var ruleReason = RulesReadOnlyReason; var tierReason = TiersReadOnlyReason;
        return () => { _sourceBytes = source; SourceHash = hash; _savedRules = savedRules; _savedTiers = savedTiers;
            _transform = transform; _tierProfile = profile; _catalog = catalog; _projectKey = projectKey;
            CanEditRules = canRules; CanEditTiers = canTiers; RulesReadOnlyReason = ruleReason; TiersReadOnlyReason = tierReason; Restore(snapshot); };
    }
    public void AcceptSaved() => Reload(true);
    public void Discard() => Reload(true);
    private static Dictionary<int, List<RuleStage>> CloneRules(IReadOnlyDictionary<int, List<RuleStage>> rules)
        => rules.ToDictionary(p => p.Key, p => p.Value.ToList());
    private static string StageSignature(IEnumerable<RuleStage> stages) => string.Join(";", stages.OrderBy(s => s.Level).Select(s => $"{s.Level}:{s.Value}"));
    private static string RuleSignature(IReadOnlyDictionary<int, List<RuleStage>> rules)
        => string.Join("|", rules.OrderBy(p => p.Key).Where(p => p.Value.Count > 0).Select(p => $"{p.Key}={StageSignature(p.Value)}"));
    private static string TierSignature(IEnumerable<TierEntry> rows) => string.Join(";", rows.Select(r => $"{r.Threshold}:{r.Label}"));
}
