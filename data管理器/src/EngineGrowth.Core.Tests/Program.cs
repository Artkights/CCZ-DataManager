using System.Security.Cryptography;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using EngineGrowth.Core;
using DataManager.Core.Models;
using System.Data;
using System.Globalization;
using TransformManager.App;
using TierManager.App;

static class Check
{
    static void Assert(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    static bool SameRules(TreasureTransformService service, int effect, IReadOnlyList<RuleStage> expected)
        => service.Rules.TryGetValue(effect, out var actual) && expected.OrderBy(r => r.Level).SequenceEqual(actual.OrderBy(r => r.Level));
    static List<TierEntry> Tiers(TierProfile profile, string first = "中")
    { var rows = profile.Entries.Select(GrowthPatchCoordinator.CloneTier).ToList(); rows[0].Label = first; return rows; }
    public static int Main(string[] args)
    {
        try
        {
            foreach (var root in args) RunShared(root);
            RunLegacyCoexistenceWorkspace(args.First(root => root.Contains("洗隋")));
            RunWorkspace(args.First(root => root.Contains("加强版6.5")));
            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
            var extended = Path.Combine(baseDirectory, "烈焰同盟6.6x(新修版)黄线完结+红线5关");
            if (Directory.Exists(extended)) RunExtendedReferences(extended);
            Console.WriteLine("ENGINE_GROWTH_CORE_TESTS_OK"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void RunShared(string root)
    {
        var path = Path.Combine(root, "Ekd5.exe"); var before = File.ReadAllBytes(path);
        var source = TreasureTransformService.Load(root, before); var tier = TierScanner.LoadFromBytes(path, before, root);
        Assert(source.Base.CanEdit && tier.CanWrite, $"fixture不可写 {root}: {source.Base.ReadOnlyReason}/{tier.ReadOnlyReason}");
        int id = source.Slots.First(s => s.Refs.Count > 0).Id;
        RuleStage[] stages = [new(5, 50), new(9, 90)]; var rows = Tiers(tier);
        var both = GrowthPatchCoordinator.Prepare(root, before, source.Catalog, [new(id, null, stages)], rows);
        both.Verify(both.AfterBytes); Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "纯候选生成修改了源基底");
        var loaded = TreasureTransformService.Load(root, both.AfterBytes, source.Catalog);
        var loadedTier = TierScanner.LoadFromBytes(path, both.AfterBytes, root);
        Assert(SameRules(loaded, id, stages) && loadedTier.Labels[0] == "中", "首次同时安装回读失败");
        var preview = loaded.Preview(id, stages, loaded.Find(id)!.Coverage, ExtraMode.Auto);
        Assert(preview[3].Source != "规则" && preview[4].Value == 50 && preview[7].Value == 50 && preview[8].Value == 90, "规则区间门槛语义错误");
        var twelve = Enumerable.Range(0,12).Select(i => new TierEntry { Threshold = i == 0 ? null : (byte)(i * 10), Label = "S" }).ToArray();
        var expanded = GrowthPatchCoordinator.Prepare(root, both.AfterBytes, source.Catalog, null, twelve);
        Assert(TierScanner.LoadFromBytes(path, expanded.AfterBytes, root).TierCount == 12, "12档写入/回读失败");
        var two = GrowthPatchCoordinator.Prepare(root, expanded.AfterBytes, source.Catalog, null, twelve.Take(2).ToArray());
        Assert(TierScanner.LoadFromBytes(path, two.AfterBytes, root).TierCount == 2, "2档写入/回读失败");
        Assert(loaded.Base.Cave!.Length < source.Base.Cave!.Length && loadedTier.CaveLength == 1024, "新安装没有收紧代码洞所有权");
        GrowthCaveAllocator.ValidateReservations(loaded.Base.Exe, GrowthCaveAllocator.ReadReservations(loaded.Base.Exe, loaded.Base, loadedTier));
        var cleared = GrowthPatchCoordinator.Prepare(root, both.AfterBytes, source.Catalog, [new(id, null, Array.Empty<RuleStage>())], null);
        Assert(!TreasureTransformService.Load(root, cleared.AfterBytes, source.Catalog).Rules.ContainsKey(id), "清除规则失败");
        Assert(TierScanner.LoadFromBytes(path, cleared.AfterBytes, root).Labels[0] == "中", "清除质变破坏档位");
        var repeat = GrowthPatchCoordinator.Prepare(root, both.AfterBytes, source.Catalog, null, Tiers(loadedTier, "AB"));
        Assert(SameRules(TreasureTransformService.Load(root, repeat.AfterBytes, source.Catalog), id, stages), "编辑档位丢失质变");
        Assert(TierPatchBuilder.Validate(tier, Tiers(tier, "ABC"), out _, out _) != null, "3字节显示未拒绝");
        Assert(TierPatchBuilder.Validate(tier, Tiers(tier, "中文"), out _, out _) != null, "4字节显示未拒绝");
        Assert(TierPatchBuilder.Validate(tier, Tiers(tier, "😀"), out _, out _) != null, "不可编码显示未拒绝");
        bool duplicate = false; try { GrowthPatchCoordinator.ValidateRules([new(5, 1), new(5, 2)]); } catch (InvalidDataException) { duplicate = true; }
        Assert(duplicate, "重复等级未拒绝");
        // Build an old TMR1 marker that reserves the full cave, then migrate.
        var legacy = before.ToArray(); var image = new PeFile(path, legacy); var cave = source.Base.Cave!;
        var blob = RuleCompiler.Build([new EffectRule(id, stages.ToList())], cave.Address, source.Base.TransformEntryVa, cave.OriginalBytes[0], cave.Length).Blob;
        image.Patch(cave.Address, blob);
        image.Patch(source.Base.TransformEntryVa, [0xE9, .. BitConverter.GetBytes(unchecked((int)(cave.Address - source.Base.TransformEntryVa - 5)))]);
        var legacyMigrated = GrowthPatchCoordinator.Prepare(root, legacy, source.Catalog, null, rows);
        Assert(SameRules(TreasureTransformService.Load(root, legacyMigrated.AfterBytes, source.Catalog), id, stages), "legacy质变迁移丢失规则");
        Assert(TierScanner.LoadFromBytes(path, legacyMigrated.AfterBytes, root).Labels[0] == "中", "legacy整洞未重新布局");
        // Old independent tier installation in the transform tail makes the
        // historical ownership claims overlap while actual code is separate.
        var legacyTier = TierScanner.LoadFromBytes(path, legacy, root);
        var coexist = TierPatchBuilder.Prepare(legacyTier, Tiers(legacyTier, "AB"), legacy).AfterBytes;
        var normalized = GrowthPatchCoordinator.Prepare(root, coexist, source.Catalog, [new(id, null, new[] {new RuleStage(6, 66)})], null);
        Assert(TierScanner.LoadFromBytes(path, normalized.AfterBytes, root).Labels[0] == "AB", "legacy同时存在重新布局破坏档位");
        // Unknown bytes in the claimed old tail cannot be reclaimed.
        var polluted = legacy.ToArray(); polluted[source.Base.Exe.VaToOffset(cave.Address) + cave.Length - 1] = 0x41;
        bool preserved = false;
        try
        {
            var safe = GrowthPatchCoordinator.Prepare(root, polluted, source.Catalog, null, rows);
            preserved = safe.AfterBytes[source.Base.Exe.VaToOffset(cave.Address) + cave.Length - 1] == 0x41;
        }
        catch (InvalidDataException) { preserved = true; }
        Assert(preserved, "旧洞未知内容被覆盖");
        if (source.Rules.Count == 0)
        {
            var normalizedSource = GrowthPatchCoordinator.Prepare(root, before, source.Catalog, null, null).AfterBytes;
            var longRules = GrowthPatchCoordinator.Prepare(root, normalizedSource, source.Catalog,
                [new(id, null, stages), new(id + 1, null, stages)], null).AfterBytes;
            var shortRules = GrowthPatchCoordinator.Prepare(root, longRules, source.Catalog,
                [new(id, null, new[] { new RuleStage(6, 66) }), new(id + 1, null, Array.Empty<RuleStage>())], null).AfterBytes;
            var noRules = GrowthPatchCoordinator.Prepare(root, shortRules, source.Catalog,
                [new(id, null, Array.Empty<RuleStage>())], null).AfterBytes;
            Assert(normalizedSource.AsSpan().SequenceEqual(noRules), "质变增改清空未精确还原基线，残留旧代码/元数据或改变填充");
        }
        Console.WriteLine($"SHARED_OK {source.Base.Version} first/coexist/clear/repeat/legacy/pollution");
    }
    static void RunExtendedReferences(string source)
    {
        string target = Path.Combine(AppContext.BaseDirectory, "growth-reference-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
        try
        {
            foreach (var name in new[] {"Ekd5.exe", "Data.e5", "Star.e5", "Imsg.e5"})
                File.Copy(Directory.GetFiles(source).Single(p => Path.GetFileName(p).Equals(name,StringComparison.OrdinalIgnoreCase)), Path.Combine(target,name));
            var starPath = Path.Combine(target,"Star.e5"); var raw = File.ReadAllBytes(starPath);
            foreach (int id in new[] {256,511}) { int offset = (id - 104) * 25; raw[offset+17] = 1; raw[offset+18] = 0x1A; }
            File.WriteAllBytes(starPath,raw);
            var workspace = new ProjectWorkspace(); workspace.Open(target,"ccz-6.6","66-standard-v1");
            Assert(workspace.Documents.ContainsKey("items-extended"), "真实512宝物fixture未识别扩展区");
            var growth = EngineGrowthDraftService.For(workspace);
            var slot = growth.Slots.Single(s => s.Id == 0x1A);
            Assert(slot.Refs.Any(r => r.Id == 256) && slot.Refs.Any(r => r.Id == 511), "扩展256/511引用缺失");
            Assert(slot.Refs.Select(r => r.Id).Distinct().Count() == slot.Refs.Count, "引用ID重复");
            workspace.ApplyCompositeTransaction("扩展511特效号草稿", [new("items-extended",255,"装备特效号",0x1B), new("items-extended",255,"名称","末条引用")]);
            growth.RefreshReferences();
            Assert(growth.Slots.Single(s => s.Id == 0x1B).Refs.Any(r => r.Id == 511 && r.Name == "末条引用"), "扩展511引用未读取当前工作区草稿");
            Assert(!growth.Slots.Single(s => s.Id == 0x1A).Refs.Any(r => r.Id == 511), "扩展511仍残留旧引用");
            Assert(File.ReadAllBytes(starPath).AsSpan().SequenceEqual(raw), "仅刷新引用写入了磁盘");
            Console.WriteLine("EXTENDED_REFERENCES_OK 256/511/current-draft/no-duplicates");
        }
        finally { Directory.Delete(target,true); }
    }

    static void RunLegacyCoexistenceWorkspace(string source)
    {
        string target = Path.Combine(AppContext.BaseDirectory, "growth-legacy-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            foreach (var name in new[] { "Ekd5.exe", "Data.e5", "Star.e5", "Imsg.e5" })
                File.Copy(Path.Combine(source, name), Path.Combine(target, name));
            var path = Path.Combine(target, "Ekd5.exe");
            var original = File.ReadAllBytes(path);
            var service = TreasureTransformService.Load(target, original);
            var cave = service.Base.Cave!;
            int id = service.Slots.First(slot => slot.Refs.Count > 0).Id;
            RuleStage[] oldRules = [new(5, 50), new(9, 90)];
            var legacy = original.ToArray();
            var pe = new PeFile(path, legacy);
            pe.Patch(cave.Address, RuleCompiler.Build([new(id, oldRules.ToList())], cave.Address,
                service.Base.TransformEntryVa, cave.OriginalBytes[0], cave.Length).Blob);
            pe.Patch(service.Base.TransformEntryVa, [0xE9, .. BitConverter.GetBytes(unchecked((int)(cave.Address - service.Base.TransformEntryVa - 5)))]);
            var tier = TierScanner.LoadFromBytes(path, legacy, target);
            var tierRows = Tiers(tier, "AB");
            var coexist = TierPatchBuilder.Prepare(tier, tierRows, legacy).AfterBytes;
            var oldTransform = TreasureTransformService.Load(target, coexist);
            var oldTier = TierScanner.LoadFromBytes(path, coexist, target);
            Assert(oldTransform.Base.IsPatched && oldTier.HasMarker && !oldTransform.Base.CanEdit &&
                oldTransform.Base.Cave!.Address < (ulong)oldTier.CaveVa + (uint)oldTier.CaveLength &&
                oldTier.CaveVa < (ulong)oldTransform.Base.Cave.Address + (uint)oldTransform.Base.Cave.Length,
                "旧全洞TMR1与尾部TIER1测试没有建立真实所有权重叠和旧洞只读原因");
            File.WriteAllBytes(path, coexist);
            var workspace = new ProjectWorkspace(); workspace.Open(target);
            var growth = EngineGrowthDraftService.For(workspace);
            Assert(growth.CanEditRules && growth.CanEditTiers && !growth.HasPendingChanges &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(coexist),
                "已验证旧共存工作区不能直接编辑，或探测迁移改变了磁盘/草稿");
            RuleStage[] revised = [new(6, 66)];
            growth.SetRules(id, revised);
            var saved = workspace.Save(); Assert(saved.Saved, "旧共存工作区保存失败：" + string.Join(";", saved.Messages));
            var reopened = new ProjectWorkspace(); reopened.Open(target);
            var reloaded = EngineGrowthDraftService.For(reopened);
            Assert(reloaded.CanEditRules && reloaded.CanEditTiers &&
                reloaded.Slots.Single(slot => slot.Id == id).Stages.SequenceEqual(revised) &&
                reloaded.TierEntries.Select(entry => (entry.Threshold,entry.Label)).SequenceEqual(tierRows.Select(entry => (entry.Threshold,entry.Label))),
                "旧共存工作区保存重开后质变/档位改变或失去编辑能力");
            var foreign = coexist.ToArray();
            BitConverter.GetBytes(cave.Address + 16).CopyTo(foreign, foreign.Length - 4);
            File.WriteAllBytes(path, foreign);
            var rejectedWorkspace = new ProjectWorkspace(); rejectedWorkspace.Open(target);
            var rejected = EngineGrowthDraftService.For(rejectedWorkspace);
            Assert(!rejected.CanEditRules && rejected.RulesReadOnlyReason.Contains("被数据引用", StringComparison.Ordinal),
                "旧共存存在未知外部绝对引用时错误开放了质变");
            bool refused = false;
            try { rejected.SetRules(id, revised); } catch (InvalidOperationException) { refused = true; }
            Assert(refused && !rejected.HasPendingChanges, "旧洞未知引用没有阻止质变草稿");
            var changedTiers = rejected.TierEntries.Select(GrowthPatchCoordinator.CloneTier).ToList(); changedTiers[0].Label = "中";
            rejected.SetTiers(changedTiers);
            Assert(!rejectedWorkspace.Save().Saved && rejected.HasTierChanges && File.ReadAllBytes(path).AsSpan().SequenceEqual(foreign),
                "未知外部引用未拒绝协调写入，或失败后丢失原文件/草稿");
            Console.WriteLine("LEGACY_WORKSPACE_OK overlapping-TMR1-TIER1/edit-save-reopen/foreign-reference-rejected");
        }
        finally { Directory.Delete(target, true); }
    }

    static void RunWorkspace(string source)
    {
        string target = Path.Combine(AppContext.BaseDirectory, "growth-workspace-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
        try
        {
            foreach (var name in new[] {"Ekd5.exe", "Data.e5", "Star.e5", "Imsg.e5", "ExtendData.dll"})
                if (File.Exists(Path.Combine(source, name))) File.Copy(Path.Combine(source, name), Path.Combine(target, name));
            var workspace = new ProjectWorkspace(); workspace.Open(target, "ccz-6.5", "65-standard-v1");
            var growth = EngineGrowthDraftService.For(workspace); Assert(growth.CanEditRules && growth.CanEditTiers, growth.RulesReadOnlyReason + growth.TiersReadOnlyReason);
            int id = growth.Slots.First(s => s.Refs.Count > 0).Id; var original = growth.Slots.Single(s => s.Id == id).Stages.ToArray();
            growth.SetRules(id, [new(5, 25)]); growth.SetTiers(Tiers(TierScanner.Load(target)));
            Assert(growth.HasRuleChanges && growth.HasTierChanges && workspace.IsDirty, "草稿未进入脏状态");
            workspace.Commands.Undo(); Assert(!growth.HasTierChanges && growth.HasRuleChanges, "档位undo失败");
            workspace.Commands.Undo(); Assert(!growth.HasPendingChanges, "规则undo失败");
            workspace.Commands.Redo(); workspace.Commands.Redo(); Assert(growth.HasPendingChanges, "redo失败");
            // Validation failure keeps both the disk and complete growth draft.
            var baseline = File.ReadAllBytes(Path.Combine(target,"Ekd5.exe"));
            var invalid = new PreparedBinaryEdit("Ekd5.exe", growth.SourceHash, [], "故障注入", "验证回滚",
                _ => throw new InvalidDataException("预期候选验证失败"));
            var failed = workspace.Save([invalid]);
            Assert(!failed.Saved && growth.HasRuleChanges && growth.HasTierChanges && File.ReadAllBytes(Path.Combine(target,"Ekd5.exe")).AsSpan().SequenceEqual(baseline), "失败保存未保留草稿/原文件");
            var before = File.ReadAllBytes(Path.Combine(target,"Ekd5.exe"));
            var saved = workspace.Save(); Assert(saved.Saved && !growth.HasPendingChanges, string.Join(";", saved.Messages));
            var after = File.ReadAllBytes(Path.Combine(target,"Ekd5.exe")); Assert(!after.AsSpan().SequenceEqual(before), "save未写入");
            Assert(workspace.UndoSaved().Success, "已保存undo失败"); growth.Reload(true); Assert(File.ReadAllBytes(Path.Combine(target,"Ekd5.exe")).AsSpan().SequenceEqual(before), "undo未恢复全部字节");
            Assert(workspace.RedoSaved().Success, "已保存redo失败"); growth.Reload(true); Assert(File.ReadAllBytes(Path.Combine(target,"Ekd5.exe")).AsSpan().SequenceEqual(after), "redo未恢复全部字节");
            // Workbench reference refresh reads current table drafts rather than the disk catalog.
            var item = workspace.GetDocument("items-low");
            int sourceIndex = Enumerable.Range(0,item.Data.Rows.Count).First(i => ItemSemanticResolver.Resolve(item,i).Category == DataManager.Core.Domain.ItemCategory.Weapon);
            int itemId = Convert.ToInt32(item.Data.Rows[sourceIndex]["ID"], CultureInfo.InvariantCulture);
            int replacementEffect = id == 0x1A ? 0x1B : 0x1A;
            workspace.ApplyCompositeTransaction("引用草稿变更", [new("items-low", sourceIndex, "装备特效号", replacementEffect)]);
            growth.RefreshReferences();
            Assert(growth.Slots.Single(s => s.Id == replacementEffect).Refs.Any(r => r.Id == itemId), "未保存特效号引用没有刷新");
            Assert(!growth.Slots.Where(s => s.Id != replacementEffect).SelectMany(s => s.Refs).Any(r => r.Id == itemId), "引用迁移后仍残留旧特效号");
            Console.WriteLine("WORKSPACE_OK draft/undo/redo/save/history/failure/reference");
        }
        finally { Directory.Delete(target, true); }
    }
}
