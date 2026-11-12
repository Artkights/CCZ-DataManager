namespace TransformManager.App;

public sealed record SlotEdit(int EffectId, byte? Coverage, IReadOnlyList<RuleStage>? Stages);

public sealed class TreasureTransformService
{
    public GameBase Base { get; }
    public ItemCatalog Catalog { get; }
    public IReadOnlyList<EffectSlot> Slots => _slots;
    public IReadOnlyList<string> ContractErrors => _contractErrors;
    public IReadOnlyDictionary<int, List<RuleStage>> Rules => _rules;

    private readonly List<EffectSlot> _slots = new();
    private readonly List<string> _contractErrors = new();
    private readonly Dictionary<int, List<RuleStage>> _rules = new();

    private TreasureTransformService(GameBase game, ItemCatalog catalog)
    {
        Base = game;
        Catalog = catalog;
    }

    public static TreasureTransformService Load(string root)
        => Load(root, File.ReadAllBytes(Path.Combine(root, "Ekd5.exe")));

    public static TreasureTransformService Load(string root, byte[] executableBytes, ItemCatalog? itemCatalog = null)
    {
        var game = new GameBase(root, executableBytes);
        var catalog = itemCatalog ?? ItemCatalog.Load(game.RootPath, game.Version);
        var service = new TreasureTransformService(game, catalog);
        service._contractErrors.AddRange(TreasureContract.Validate(game));
        service._rules.Clear();
        foreach (var rule in service.ReadRulesFromFile())
            service._rules[rule.EffectId] = rule.Stages;

        int last = game.CoverageLast > 0 ? game.CoverageLast : 0x7E;
        for (int id = game.CoverageFirst; id <= last; id++)
        {
            var refs = catalog.ForEffect(id);
            bool hasRule = service._rules.TryGetValue(id, out var stages);
            // 只展示武器/防具实际引用的特效号；已写入的旧规则仍保留可见，便于清理。
            if (refs.Count == 0 && !hasRule) continue;
            service._slots.Add(new EffectSlot
            {
                Id = id,
                Name = game.EffectName(id),
                Coverage = game.ReadCoverage(id),
                Extra = game.ReadExtra(id),
                Refs = refs,
                Stages = hasRule ? stages!.ToList() : new List<RuleStage>()
            });
        }
        return service;
    }

    private List<EffectRule> ReadRulesFromFile()
    {
        if (Base.Cave is not { } cave) return new List<EffectRule>();
        var memory = Base.Exe.ReadBytes(cave.Address, cave.Length);
        return memory.Length == 0 ? new List<EffectRule>() : RuleCompiler.ReadBlob(memory);
    }

    public EffectSlot? Find(int effectId) => _slots.FirstOrDefault(slot => slot.Id == effectId);

    public IReadOnlyList<PreviewRow> Preview(int effectId, IReadOnlyList<RuleStage> stages,
        byte coverage, ExtraMode extraMode, int maxLevel = 20)
    {
        var slot = Find(effectId);
        int baseValue = slot?.Refs.FirstOrDefault()?.EffectValue ?? 0;
        var rows = new List<PreviewRow>();
        for (int level = 1; level <= maxLevel; level++)
        {
            var matched = stages.Where(stage => stage.Level <= level)
                .OrderByDescending(stage => stage.Level)
                .FirstOrDefault();
            if (matched != null)
            {
                rows.Add(new PreviewRow(level, matched.Value & 0xFF, "规则"));
                continue;
            }
            var (value, source) = NativeValue(effectId, baseValue, level, coverage, extraMode);
            rows.Add(new PreviewRow(level, value, source));
        }
        return rows;
    }

    private (int Value, string Source) NativeValue(int effect, int start, int level, byte coverage, ExtraMode extraMode)
    {
        int value = start;
        string source = "原值";
        switch (Base.Version)
        {
            case EngineVersion.V65:
                if (level >= 7)
                {
                    value = Transform65(effect, value, level);
                    source = "原版公式";
                }
                if (level >= 10 && coverage != 0xFE)
                {
                    value = coverage;
                    source = "覆盖表";
                }
                break;
            case EngineVersion.V64:
                bool special = IsSpecial64(effect);
                if (special)
                {
                    if (level >= 12 && coverage != 0xFE) { value = coverage; source = "覆盖表"; }
                }
                else if (effect == 0x1F)
                {
                    value *= level;
                    source = "原版公式";
                }
                else if (level >= 18)
                {
                    value = Transform64(effect, value);
                    source = "原版公式";
                    if (coverage != 0xFE) { value = coverage; source = "覆盖表"; }
                }
                break;
            case EngineVersion.V66:
                if (effect == 0x1F) { value *= level + 1; source = "原版公式"; }
                if (level >= 16)
                {
                    value = Transform66(effect, value, extraMode);
                    source = "原版公式";
                }
                if (level >= 18 && coverage != 0xFE)
                {
                    value = coverage;
                    source = "覆盖表";
                }
                break;
        }
        return (value & 0xFF, source);
    }

    private static int Transform65(int effect, int value, int level) => effect switch
    {
        0x1F => value + level,
        0x55 => value * (level + 1),
        0x2F => value * level + 30,
        0x5C or 0x64 => value,
        0x33 when value == 4 => 7,
        0x32 => value * 2,
        0x36 or 0x42 or 0x46 => value + 1,
        _ => value * 3 / 2
    };

    private static int Transform64(int effect, int value) => effect switch
    {
        0x33 when value == 4 => 7,
        0x32 => value * 2,
        0x36 or 0x42 or 0x46 => value + 1,
        _ => value * 3 / 2
    };

    private int Transform66(int effect, int value, ExtraMode extraMode)
    {
        switch (effect)
        {
            case 0x72: return value * 3 / 4;
            case 0x33 when value == 4: return 7;
            case 0x32: return value * 2;
            case 0x36 or 0x42 or 0x46: return value + 1;
        }
        byte flag = Base.ReadExtra(effect);
        if (extraMode == ExtraMode.ForceScale) flag = 0;
        else if (extraMode == ExtraMode.SkipScale) flag = 7;
        return flag >= 7 ? value : value * 3 / 2;
    }

    private static bool IsSpecial64(int effect)
        => effect is 0x76 or 0x77 or 0x78 or 0x79 or 0x7A or 0x7B or 0x7C or 0x48 or 0x72 or 0x70 or 0x6F or 0x31 or 0x66 or 0x6C;

    internal EngineGrowth.Core.GrowthPatchPlan Prepare(IReadOnlyList<SlotEdit> edits)
    {
        if (!Base.CanEdit) throw new InvalidDataException(Base.ReadOnlyReason);
        var errors = TreasureContract.Validate(Base);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors));

        var after = (byte[])Base.Exe.Bytes.Clone();
        var image = new PeFile(Base.ExePath, after);
        int coverageChanged = 0;
        // 只有真正安装/清理规则时才允许入口与代码洞变化；纯覆盖值写入
        // 只能改覆盖表单字节。
        var allowed = new List<(int Offset, int Length)>();

        foreach (var edit in edits)
        {
            if (edit.Coverage is { } coverage && Base.CoverageTableVa != 0 &&
                edit.EffectId >= Base.CoverageFirst && edit.EffectId <= Base.CoverageLast &&
                coverage != Base.ReadCoverage(edit.EffectId))
            {
                uint address = Base.CoverageTableVa + (uint)(edit.EffectId - Base.CoverageFirst);
                image.Patch(address, [coverage]);
                allowed.Add((Base.Exe.VaToOffset(address), 1));
                coverageChanged++;
            }
        }

        // 规则采用“合并”语义：未在本次编辑中出现的槽位保留既有规则；
        // Stages=null 表示不动规则，空列表表示清除该槽位规则。
        bool rulesChanged = edits.Any(edit => edit.Stages is not null);
        var ruleMap = _rules.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        foreach (var edit in edits)
        {
            if (edit.Stages is null) continue;
            if (edit.Stages.Count == 0) ruleMap.Remove(edit.EffectId);
            else ruleMap[edit.EffectId] = edit.Stages.ToList();
        }
        var ruleList = ruleMap
            .Select(pair => new EffectRule(pair.Key, pair.Value))
            .Where(rule => rule.Stages.Count > 0)
            .OrderBy(rule => rule.EffectId)
            .ToList();

        string ruleMessage;
        if (!rulesChanged)
        {
            ruleMessage = "未修改等级规则";
        }
        else if (ruleList.Count > 0)
        {
            if (Base.Cave is not { } cave)
                throw new InvalidDataException("当前基底找不到安全代码洞，不能写入等级规则。");
            var (blob, _) = RuleCompiler.Build(ruleList, cave.Address, Base.TransformEntryVa,
                cave.OriginalBytes.Length > 0 ? cave.OriginalBytes[0] : 0x90, cave.Length);
            if (blob.Length > cave.Length)
                throw new InvalidDataException($"规则太多：需要 {blob.Length} 字节，代码洞只有 {cave.Length} 字节。");
            if (Base.IsPatched)
            {
                // Shrinking a managed block must remove its own old metadata,
                // otherwise a later coexistence scan would preserve those stale
                // bytes as foreign tail data. Restore only the verified prior
                // footprint; unrelated bytes after it remain untouched.
                int priorUsed = RuleCompiler.RequiredSize(_rules.Select(pair => new EffectRule(pair.Key, pair.Value)).ToArray(),
                    cave.Address, Base.TransformEntryVa, cave.OriginalBytes[0], cave.Length);
                int restoreLength = Math.Min(priorUsed, cave.OriginalBytes.Length);
                image.Patch(cave.Address, cave.OriginalBytes[..restoreLength]);
                allowed.Add((Base.Exe.VaToOffset(cave.Address), restoreLength));
            }
            image.Patch(cave.Address, blob);
            allowed.Add((Base.Exe.VaToOffset(cave.Address), blob.Length));
            var jump = BuildEntryJump(Base.TransformEntryVa, cave.Address);
            image.Patch(Base.TransformEntryVa, jump);
            allowed.Add((Base.Exe.VaToOffset(Base.TransformEntryVa), 5));
            ruleMessage = $"已写入 {ruleList.Count} 条等级规则";
        }
        else
        {
            bool previouslyPatched = Base.Exe.ReadByte(Base.TransformEntryVa) == 0xE9;
            if (previouslyPatched)
            {
                image.Patch(Base.TransformEntryVa, Base.EntryOriginal);
                allowed.Add((Base.Exe.VaToOffset(Base.TransformEntryVa), 5));
                if (Base.Cave is { } cave)
                {
                    image.Patch(cave.Address, cave.OriginalBytes);
                    allowed.Add((Base.Exe.VaToOffset(cave.Address), cave.OriginalBytes.Length));
                }
                ruleMessage = "已清除全部等级规则";
            }
            else
            {
                ruleMessage = "没有等级规则";
            }
        }

        // 白名单校验：只允许入口 5 字节、代码洞规则块、显式修改的覆盖值字节。
        // 6.6 的后缀类型表（0x4CB6BA）不在任何允许范围内，从机制上杜绝再次误写。
        string? violation = FindUnauthorizedChange(Base.Exe.Bytes, after, allowed);
        if (violation != null) throw new InvalidDataException(violation);

        string summary = $"{ruleMessage}；覆盖值 {coverageChanged} 项";
        return new EngineGrowth.Core.GrowthPatchPlan(Base.Exe.Bytes, after, summary);
    }

    private static string? FindUnauthorizedChange(byte[] before, byte[] after,
        IReadOnlyList<(int Offset, int Length)> allowed)
    {
        if (before.Length != after.Length) return "写入后文件长度变化，已取消写入。";
        var permitted = new HashSet<int>();
        foreach (var (offset, length) in allowed)
        {
            int start = Math.Max(0, offset);
            int end = Math.Min(after.Length, offset + length);
            for (int i = start; i < end; i++) permitted.Add(i);
        }
        var unexpected = new List<int>();
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i] || permitted.Contains(i)) continue;
            unexpected.Add(i);
            if (unexpected.Count >= 8) break;
        }
        if (unexpected.Count == 0) return null;
        string detail = string.Join("，", unexpected.Select(i =>
            $"0x{i:X}（{before[i]:X2}→{after[i]:X2}）"));
        return $"写入包含未授权字节：{detail}；已取消写入。";
    }

    private static byte[] BuildEntryJump(uint entry, uint cave)
    {
        int delta = unchecked((int)(cave - (entry + 5)));
        return
        [
            0xE9,
            (byte)delta, (byte)(delta >> 8), (byte)(delta >> 16), (byte)(delta >> 24)
        ];
    }
}
