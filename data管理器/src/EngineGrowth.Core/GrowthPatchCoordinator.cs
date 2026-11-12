using System.Runtime.CompilerServices;
using System.Text;
using TransformManager.App;
using TierManager.App;

namespace EngineGrowth.Core;

internal static class EncodingBootstrap
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Shared binary codecs require GBK before standalone or embedded callers use them.")]
    [ModuleInitializer] internal static void Initialize() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

public sealed record GrowthPatchPlan(byte[] BeforeBytes, byte[] AfterBytes, string Summary)
{
    public Action<byte[]>? CandidateVerifier { get; init; }
    public void Verify(byte[] bytes) => CandidateVerifier?.Invoke(bytes);
}

/// <summary>Both growth patches allocate from one baseline and own disjoint bounded ranges.</summary>
public static class GrowthPatchCoordinator
{
    public static GrowthPatchPlan Prepare(string root, byte[] before, ItemCatalog? catalog,
        IReadOnlyList<SlotEdit>? ruleEdits, IReadOnlyList<TierEntry>? tierRows)
    {
        var original = TreasureTransformService.Load(root, before, catalog ?? ItemCatalog.FromItems([]));
        var originalTiers = TierScanner.LoadFromBytes(original.Base.ExePath, before, root);
        byte[] clean;
        TreasureTransformService service;
        TierProfile tiers;
        List<GrowthCaveReservation> reserved;
        bool preservedOldTail = false;
        try
        {
            clean = ManagedGrowthLayout.RestoreBaseline(original, originalTiers, before);
            service = TreasureTransformService.Load(root, clean, catalog ?? ItemCatalog.FromItems([]));
            tiers = TierScanner.LoadFromBytes(service.Base.ExePath, clean, root);
            reserved = GrowthCaveAllocator.ReadReservations(service.Base.Exe, service.Base, tiers).ToList();
        }
        catch (GrowthTailPreservationRequiredException)
        {
            // Keep unrecognized leftovers byte-for-byte at their old location.
            // Only a proven fill tail can be released; no original path moves.
            preservedOldTail = true;
            clean = before; service = original; tiers = originalTiers;
            reserved = ManagedGrowthLayout.PreserveAndCompact(service, tiers, before).ToList();
        }
        var game = service.Base;
        GrowthCaveAllocator.ValidateReservations(game.Exe, reserved);
        if (original.Base.IsPatched)
        {
            var merged = original.Rules.ToDictionary(p => p.Key, p => new SlotEdit(p.Key, null, p.Value));
            foreach (var edit in ruleEdits ?? []) merged[edit.EffectId] = new SlotEdit(edit.EffectId, edit.Coverage,
                edit.Stages ?? original.Rules.GetValueOrDefault(edit.EffectId));
            ruleEdits = merged.Values.ToArray();
        }
        if (originalTiers.HasMarker && tierRows == null) tierRows = originalTiers.Entries.Select(CloneTier).ToArray();
        var expectedRules = service.Rules.ToDictionary(p => p.Key, p => p.Value.ToList());
        var plans = new List<GrowthPatchPlan>();
        if (ruleEdits is { Count: > 0 })
        {
            foreach (var edit in ruleEdits)
            {
                if (edit.EffectId < game.CoverageFirst || edit.EffectId > game.CoverageLast)
                    throw new InvalidDataException($"特效号 0x{edit.EffectId:X2} 不在当前质变表范围内。");
                if (edit.Stages == null) continue;
                ValidateRules(edit.Stages);
                if (edit.Stages.Count == 0) expectedRules.Remove(edit.EffectId);
                else expectedRules[edit.EffectId] = edit.Stages.OrderByDescending(s => s.Level).ToList();
            }
            if (!game.IsPatched && expectedRules.Count > 0 && ruleEdits.Any(e => e.Stages != null))
            {
                var rules = expectedRules.Select(p => new EffectRule(p.Key, p.Value)).ToArray();
                int required = RuleCompiler.RequiredSize(rules, 0x401000, game.TransformEntryVa);
                int length = Math.Max(1024, checked((required + 255) / 256 * 256));
                var allocation = GrowthCaveAllocator.Allocate(game.Exe, reserved, length, "质变");
                reserved.Add(allocation);
                game.UseCave(new CaveInfo(allocation.Address, allocation.Length,
                    game.Exe.ReadBytes(allocation.Address, allocation.Length)));
            }
            if (preservedOldTail && game.IsPatched && game.Cave is { } oldCave && expectedRules.Count > 0)
            {
                int oldUsed = RuleCompiler.RequiredSize(original.Rules.Select(p => new EffectRule(p.Key,p.Value)).ToArray(),
                    oldCave.Address, game.TransformEntryVa, oldCave.OriginalBytes[0], original.Base.Cave!.Length);
                int newUsed = RuleCompiler.RequiredSize(expectedRules.Select(p => new EffectRule(p.Key,p.Value)).ToArray(),
                    oldCave.Address, game.TransformEntryVa, oldCave.OriginalBytes[0], oldCave.Length);
                int offset = game.Exe.VaToOffset(oldCave.Address);
                if (newUsed > oldUsed && Enumerable.Range(oldUsed, Math.Max(0, Math.Min(newUsed, oldCave.Length) - oldUsed))
                    .Any(i => clean[offset+i] is not (0x00 or 0x90 or 0xCC)))
                {
                    int length = Math.Max(1024, (newUsed + 255) / 256 * 256);
                    var allocation = GrowthCaveAllocator.Allocate(game.Exe, reserved, length, "质变");
                    reserved.Add(allocation);
                    game.UseCave(new CaveInfo(allocation.Address, allocation.Length, game.Exe.ReadBytes(allocation.Address,allocation.Length)));
                }
            }
            plans.Add(service.Prepare(ruleEdits));
        }
        if (tierRows != null)
        {
            if (!tiers.HasMarker)
            {
                // A 1 KiB ownership range fits the supported 2..12 tiers and is
                // never lent to transform rules, including its unused tail.
                var allocation = GrowthCaveAllocator.Allocate(game.Exe, reserved, 1024, "档位");
                reserved.Add(allocation);
                tiers.CaveVa = allocation.Address;
                tiers.CaveLength = allocation.Length;
                if (tiers.ReadOnlyReason == "没有足够大的安全代码洞。") tiers.ReadOnlyReason = "";
            }
            var tierPlan = TierPatchBuilder.Prepare(tiers, tierRows, clean);
            if (preservedOldTail && tiers.HasMarker)
            {
                if (!new MarkerReader(game.Exe, tiers.MarkerVa).TryRead(out var oldMarker))
                    throw new InvalidDataException("旧档位 marker 无法读取。");
                int oldUsed = checked((int)(tiers.MarkerVa - tiers.CaveVa)) + MarkerCodec.Build(oldMarker).Length;
                int offset = game.Exe.VaToOffset(tiers.CaveVa);
                bool overwrites = Enumerable.Range(oldUsed, tiers.CaveLength - oldUsed)
                    .Any(i => clean[offset+i] is not (0x00 or 0x90 or 0xCC) && tierPlan.AfterBytes[offset+i] != clean[offset+i]);
                if (overwrites)
                {
                    var allocation = GrowthCaveAllocator.Allocate(game.Exe, reserved, 1024, "档位");
                    reserved.Add(allocation);
                    tiers.CaveVa = allocation.Address; tiers.CaveLength = allocation.Length;
                    tierPlan = TierPatchBuilder.Prepare(tiers, tierRows, clean);
                }
            }
            plans.Add(tierPlan);
        }
        var after = clean.ToArray();
        var written = new bool[before.Length];
        foreach (var plan in plans)
        {
            if (plan.AfterBytes.Length != before.Length) throw new InvalidDataException("成长补丁不能改变 EXE 长度。");
            for (int i = 0; i < before.Length; i++)
            {
                if (clean[i] == plan.AfterBytes[i]) continue;
                if (written[i] && after[i] != plan.AfterBytes[i])
                    throw new InvalidDataException($"质变与档位在 0x{i:X} 存在字节冲突。");
                after[i] = plan.AfterBytes[i]; written[i] = true;
            }
        }
        var expectedTiers = (tierRows ?? tiers.Entries).Select(CloneTier).ToArray();
        bool verifyTiers = tierRows != null || tiers.HasMarker;
        bool verifyRules = game.IsPatched || ruleEdits != null;
        void Verify(byte[] bytes)
        {
            if (bytes.Length != before.Length) throw new InvalidDataException("成长补丁回读文件长度变化。");
            if (verifyRules)
            {
                var actual = TreasureTransformService.Load(root, bytes, catalog ?? ItemCatalog.FromItems([]));
                if (!actual.Base.CanEdit || actual.ContractErrors.Count != 0 || !RulesEqual(expectedRules, actual.Rules))
                    throw new InvalidDataException("质变规则候选回读不一致。");
                if (game.ExtraTableVa is uint extra)
                {
                    int offset = game.Exe.VaToOffset(extra);
                    int length = game.CoverageLast - game.CoverageFirst + 1;
                    if (!before.AsSpan(offset, length).SequenceEqual(bytes.AsSpan(offset, length)))
                        throw new InvalidDataException("质变写入不能改变 6.6 装备后缀/门控表。");
                }
            }
            if (verifyTiers) TierPatchBuilder.Verify(tiers, expectedTiers, bytes);
            var pe = new PeFile(game.ExePath, bytes);
            var currentGame = new GameBase(root, bytes);
            var currentTiers = TierScanner.LoadFromBytes(game.ExePath, bytes, root);
            GrowthCaveAllocator.ValidateReservations(pe, GrowthCaveAllocator.ReadReservations(pe, currentGame, currentTiers));
        }
        Verify(after);
        return new GrowthPatchPlan(before, after, string.Join("；", plans.Select(p => p.Summary)))
        { CandidateVerifier = Verify };
    }

    public static void ValidateRules(IReadOnlyList<RuleStage> stages)
    {
        if (stages.Count > RuleCompiler.MaxStagesPerEffect)
            throw new InvalidDataException($"一个特效号最多 {RuleCompiler.MaxStagesPerEffect} 条规则。");
        if (stages.Any(s => s.Level is < 1 or > 99)) throw new InvalidDataException("等级必须在 1..99。");
        if (stages.Any(s => s.Value is < 0 or > 255)) throw new InvalidDataException("特效值必须在 0..255。");
        if (stages.Select(s => s.Level).Distinct().Count() != stages.Count) throw new InvalidDataException("规则等级不能重复。");
    }

    public static TierEntry CloneTier(TierEntry row) => new()
    { Ordinal = row.Ordinal, Threshold = row.Threshold, Label = row.Label, OriginalReturn = row.OriginalReturn };

    private static bool RulesEqual(IReadOnlyDictionary<int, List<RuleStage>> expected,
        IReadOnlyDictionary<int, List<RuleStage>> actual)
        => expected.Count == actual.Count && expected.All(p => actual.TryGetValue(p.Key, out var rows)
            && p.Value.OrderBy(s => s.Level).SequenceEqual(rows.OrderBy(s => s.Level)));
}
