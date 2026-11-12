namespace TransformManager.App;

/// <summary>Headless checks used by --verify. Prints PASS/FAIL lines and returns true when all assertions pass.</summary>
public static class ScanVerifier
{
    public static bool Run(string root, bool verbose = true)
    {
        bool ok = true;
        void Check(string name, bool condition, string detail)
        {
            if (verbose) Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!condition) ok = false;
        }

        GameBase game;
        try { game = new GameBase(root); }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  打开基底  {ex.Message}");
            return false;
        }

        Console.WriteLine($"== {game.RootPath}");
        Console.WriteLine($"   版本={game.VersionText} 入口=0x{game.TransformEntryVa:X8} 覆盖表=0x{game.CoverageTableVa:X8} 名称源={game.NameSource}");

        if (game.ReadOnlyReason.Contains("加壳"))
        {
            Check("加壳基底拒绝", game.ReadOnlyReason.Contains("加壳"), game.ReadOnlyReason);
            return ok;
        }
        if (!game.CanEdit)
        {
            Check("未知变体只读", true, game.ReadOnlyReason);
            return ok;
        }

        Check("入口 5 字节", game.EntryOriginal.Length == 5 &&
            game.EntryOriginal[0] == 0x55 && game.EntryOriginal[1] == 0x8B &&
            game.EntryOriginal[2] == 0xEC && game.EntryOriginal[3] == 0x0F && game.EntryOriginal[4] == 0xB6,
            string.Join(' ', game.EntryOriginal.Select(b => b.ToString("X2"))));
        Check("覆盖表唯一引用", TreasureContract.CountImmediateReferences(game, game.CoverageTableVa) == 1,
            $"refs={TreasureContract.CountImmediateReferences(game, game.CoverageTableVa)}");
        if (game.Version == EngineVersion.V66 && game.ExtraTableVa is { } extraTable)
        {
            int extraRefs = TreasureContract.CountImmediateReferences(game, extraTable);
            Check("后缀表双用途", extraRefs == 2, $"refs={extraRefs}（质变门控 + 后缀格式化；工具只读）");
        }
        Check("名称池命中", game.NamePoolHits >= 10, $"{game.NamePoolHits}/18");
        Check("原生变化段", game.NativeStages.Count >= 1, string.Join(" / ", game.NativeStages));
        Check("代码洞", game.Cave is { Length: >= 600 }, game.Cave is { } cave ? $"0x{cave.Address:X8} {cave.Length}B" : "未找到");

        var errors = TreasureContract.Validate(game);
        Check("契约校验", errors.Count == 0, errors.Count == 0 ? "无问题" : string.Join("；", errors));

        var catalog = ItemCatalog.Load(game.RootPath, game.Version);
        int weapons = catalog.Items.Count(item => item.Category == ItemCategory.Weapon);
        int armors = catalog.Items.Count(item => item.Category == ItemCategory.Armor);
        int accessories = catalog.Items.Count(item => item.Category == ItemCategory.Accessory);
        int consumables = catalog.Items.Count(item => item.Category == ItemCategory.Consumable);
        Check("类别解析", weapons > 0 && armors > 0,
            $"武器 {weapons}，防具 {armors}，辅助 {accessories}，道具 {consumables}");
        var spear = catalog.Items.FirstOrDefault(item => item.Name.Contains("丈八蛇矛"));
        if (spear != null)
            Check("类别样本", spear.Category == ItemCategory.Weapon, $"{spear.Name} → {spear.CategoryName}");
        int combatRefs = catalog.ByEffect.Values.Sum(list => list.Count);
        bool onlyCombat = catalog.ByEffect.Values.SelectMany(list => list)
            .All(item => item.Category is ItemCategory.Weapon or ItemCategory.Armor);
        Check("引用仅武器/防具", onlyCombat && combatRefs > 0,
            $"引用 {combatRefs} 件；辅助/道具不计入");
        string firstName = game.EffectName(game.CoverageFirst);
        Check("特效名称", firstName.Length > 0, $"{game.NameSource}；0x{game.CoverageFirst:X2}={firstName}");

        if (game.Cave is { } foundCave)
        {
            var service = TreasureTransformService.Load(game.RootPath);
            var sample = service.Find(0x1A) ?? service.Slots.FirstOrDefault();
            if (sample != null)
            {
                int needed = RuleCompiler.RequiredSize(
                    new[] { new EffectRule(sample.Id, new List<RuleStage> { new(9, 9), new(5, 5) }) },
                    foundCave.Address, game.TransformEntryVa);
                Check("规则容量", needed <= foundCave.Length, $"需要 {needed}B / 可用 {foundCave.Length}B");
                var ten = Enumerable.Range(0, 10)
                    .Select(index => new EffectRule(0x1A + index, new List<RuleStage> { new(9, 9), new(5, 5) }))
                    .ToList();
                int tenNeeded = RuleCompiler.RequiredSize(ten, foundCave.Address, game.TransformEntryVa);
                Check("10 条规则容量", tenNeeded <= foundCave.Length, $"需要 {tenNeeded}B / 可用 {foundCave.Length}B");
            }
        }
        return ok;
    }
}
