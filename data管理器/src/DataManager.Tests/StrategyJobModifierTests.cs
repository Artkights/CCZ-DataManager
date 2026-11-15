using System.Buffers.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Scanner;
using Iced.Intel;

namespace DataManager.Tests;

internal static class StrategyJobModifierTests
{
    internal static void Run(string basePath)
    {
        foreach (var name in new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" })
        {
            var exe = FindEngine(basePath, name);
            var before = File.ReadAllBytes(exe);
            var service = new StrategyHookSectionService();
            var rules = new List<StrategyJobModifierRule>
            {
                new(StrategyModifierSide.Receiver, StrategyModifierMatchKind.StrategyType, 0, 0x1E, 40, 0, true, "机关火系"),
                new(StrategyModifierSide.Caster, StrategyModifierMatchKind.StrategyType, 5, 0x0C, 100, 50, true, "女兵诱惑")
            };
            var validation = new StrategyJobModifierService().Validate(rules);
            TestSupport.Assert(!validation.HasErrors, "策略规则校验误报：" + string.Join("；", validation.Issues.Select(issue => issue.Message)));

            var candidate = service.Build(exe, before, rules, true);
            TestSupport.Assert(candidate.AfterBytes.Length == before.Length + StrategyJobModifierRule.SectionSize,
                name + "：追加 .strh 后的文件长度不正确。");
            var layout = ReadLayout(candidate.AfterBytes);
            var section = FindSection(candidate.AfterBytes, layout, StrategyHookSectionService.SectionName);
            TestSupport.Assert(section != null, name + "：未找到 .strh 节。");
            var tableOffset = checked((int)(section!.Value.RawOffset + 0x1000));
            TestSupport.Assert(candidate.AfterBytes.AsSpan(tableOffset, 4).SequenceEqual("STRH"u8),
                name + "：规则表 magic 不正确。");
            TestSupport.Assert(BinaryPrimitives.ReadUInt16LittleEndian(candidate.AfterBytes.AsSpan(tableOffset + 6)) == 2,
                name + "：规则数量不正确。");
            var damageOffset = VaToOffset(candidate.AfterBytes, layout, StrategyHookSectionService.DamageHookVa);
            var hitOffset = VaToOffset(candidate.AfterBytes, layout, StrategyHookSectionService.HitHookVa);
            TestSupport.Assert(candidate.AfterBytes[damageOffset] == 0xE9 && candidate.AfterBytes[hitOffset] == 0xE9,
                name + "：Hook 站点没有写入 JMP。");
            var damageTarget = StrategyHookSectionService.DamageHookVa + 5
                + (uint)BinaryPrimitives.ReadInt32LittleEndian(candidate.AfterBytes.AsSpan(damageOffset + 1));
            TestSupport.Assert(damageTarget == section.Value.VirtualAddress,
                name + "：伤害 Hook 没有跳到 .strh 节首。");
            AssertEvaluator(candidate.AfterBytes, section.Value.RawOffset, section.Value.VirtualAddress,
                StrategyHookSectionService.DamageReturnVa, name + " 伤害");
            AssertEvaluator(candidate.AfterBytes, section.Value.RawOffset + 0x0800, section.Value.VirtualAddress + 0x0800,
                StrategyHookSectionService.HitReturnVa, name + " 命中");

            var state = service.Read(candidate.AfterBytes);
            TestSupport.Assert(state is { HookInstalled: true } && state.Rules.Count == 2
                && state.Rules[0].Series == 0x1E && state.Rules[0].DamagePercent == 40
                && state.Rules[1].HitPercent == 50 && state.Rules[1].Note == "女兵诱惑",
                name + "：规则表/备注复读不一致。");

            var restored = service.RestoreHooks(candidate.AfterBytes);
            TestSupport.Assert(restored.AsSpan(damageOffset, 8).SequenceEqual(before.AsSpan(damageOffset, 8))
                && restored.AsSpan(hitOffset, 8).SequenceEqual(before.AsSpan(hitOffset, 8)),
                name + "：卸载 Hook 后没有恢复原始字节。");
            var restoredState = service.Read(restored);
            TestSupport.Assert(restoredState is { HookInstalled: false } && restoredState.Rules.Count == 2,
                name + "：卸载后规则表应保留且 Hook 状态应为未安装。");
            AssertBuiltInRules(name, before);
            Console.WriteLine($"STRATEGY_JOB_MODIFIER_CORE_OK {name} section=0x{section.Value.VirtualAddress:X8}");
        }
        Console.WriteLine("STRATEGY_JOB_MODIFIER_CORE_TESTS_OK three bases append/hook/read/restore");
    }

    private static void AssertBuiltInRules(string name, byte[] before)
    {
        var service = new StrategyBuiltInRuleService();
        var info = service.Read(before);
        TestSupport.Assert(info.Supported, name + "：内置条目读取失败：" + info.Diagnostic);
        var expected = name switch
        {
            "洗隋" => (Enabled: false, Series: (byte)0, Bonus: (sbyte)0x14, FireType: (byte)0),
            "加强版6.5未加密版" => (Enabled: true, Series: (byte)0x1E, Bonus: (sbyte)0x28, FireType: (byte)0),
            "复刻版6.6山贼" => (Enabled: true, Series: (byte)0x15, Bonus: (sbyte)0x14, FireType: (byte)0),
            _ => throw new InvalidOperationException(name)
        };
        TestSupport.Assert(info.Current.FireEnabled == expected.Enabled
            && info.Current.FireSeries == expected.Series
            && info.Current.FireBonus == expected.Bonus
            && info.Current.FireTriggerType == expected.FireType,
            $"{name}：火系内置值读取不一致：{info.Current}");
        TestSupport.Assert(info.Current.CharmDamageValue == 0x14
            && info.Current.CharmHitValue == 0x14
            && info.Current.CharmSeries == 0x0C,
            $"{name}：诱惑内置值读取不一致：{info.Current}");
        if (name == "复刻版6.6山贼")
            TestSupport.Assert(info.CharmDamageKind == StrategyBuiltInMatchKind.StrategyType
                && info.CharmHitKind == StrategyBuiltInMatchKind.StrategyId,
                name + "：6.6 山贼的伤害/命中判定依据识别错误。");

        var settings = new StrategyBuiltInRuleSettings(
            true, 1, 0x0C, 50, true, 0x15, 0x16, 0x0D);
        var after = service.Apply(before, settings);
        TestSupport.Assert(after[Offset(after, StrategyBuiltInRuleService.FireTriggerVa)] == 0x3C
            && after[Offset(after, StrategyBuiltInRuleService.FireTriggerVa) + 1] == 1,
            name + "：火系触发类型未写入。");
        TestSupport.Assert(after[Offset(after, StrategyBuiltInRuleService.FireSeriesVa)] == 0x0C
            && after[Offset(after, StrategyBuiltInRuleService.FireBonusVa)] == 0x32,
            name + "：火系兵种系/加成未写入。");
        TestSupport.Assert(after[Offset(after, StrategyBuiltInRuleService.CharmDamageValueVa)] == 0x15
            && after[Offset(after, StrategyBuiltInRuleService.CharmHitValueVa)] == 0x16
            && after[Offset(after, StrategyBuiltInRuleService.CharmDamageSeriesVa)] == 0x0D
            && after[Offset(after, StrategyBuiltInRuleService.CharmHitSeriesVa)] == 0x0D,
            name + "：诱惑判定值/兵种系未写入。");

        var disabled = service.Apply(before, settings with { FireEnabled = false, CharmEnabled = false });
        TestSupport.Assert(disabled[Offset(disabled, StrategyBuiltInRuleService.FireSeriesVa)]
                == StrategyBuiltInRuleSettings.DisabledSeries
            && disabled[Offset(disabled, StrategyBuiltInRuleService.CharmDamageValueVa)]
                == StrategyBuiltInRuleSettings.DisabledValue
            && disabled[Offset(disabled, StrategyBuiltInRuleService.CharmHitValueVa)]
                == StrategyBuiltInRuleSettings.DisabledValue,
            name + "：关闭内置条目没有写入不可达值。");

        var broken = (byte[])before.Clone();
        broken[Offset(broken, StrategyBuiltInRuleService.CharmDamageVa)] = 0x90;
        TestSupport.Assert(!service.Read(broken).Supported, name + "：错误签名没有被拒绝。");
        var threw = false;
        try { service.Apply(broken, settings); } catch (InvalidDataException) { threw = true; }
        TestSupport.Assert(threw, name + "：错误签名仍被写入。");

        var combined = new StrategyJobModifierService().BuildImage(before,
            [new StrategyJobModifierRule(StrategyModifierSide.Receiver, StrategyModifierMatchKind.StrategyType,
                0, 0x1E, 40, 0, true, "combined")], true, settings);
        var combinedState = new StrategyHookSectionService().Read(combined);
        TestSupport.Assert(combinedState is { HookInstalled: true }
            && combined[Offset(combined, StrategyBuiltInRuleService.FireSeriesVa)] == 0x0C,
            name + "：自定义规则与内置修改没有合并到同一镜像。");
    }

    private static int Offset(byte[] image, uint va) => StrategyHookSectionService.VaToFileOffset(image, va);

    private static void AssertEvaluator(byte[] image, uint rawOffset, uint va, uint expectedReturn, string label)
    {
        var reader = new ByteArrayCodeReader(image, checked((int)rawOffset), 0x200);
        var decoder = Decoder.Create(32, reader);
        decoder.IP = va;
        var calls = new List<uint>();
        var sawReturnJump = false;
        for (var index = 0; index < 400; index++)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid) break;
            if (instruction.Mnemonic == Mnemonic.Call) calls.Add(instruction.NearBranch32);
            if (instruction.Mnemonic == Mnemonic.Jmp && instruction.NearBranch32 == expectedReturn)
            {
                sawReturnJump = true;
                break;
            }
        }
        if (!sawReturnJump)
        {
            Console.WriteLine($"{label} code: {Convert.ToHexString(image, checked((int)rawOffset), 0x140)}");
            var dump = Decoder.Create(32, new ByteArrayCodeReader(image, checked((int)rawOffset), 0x140));
            dump.IP = va;
            for (var index = 0; index < 100; index++)
            {
                var instruction = dump.Decode();
                if (instruction.IsInvalid) { Console.WriteLine("INVALID @" + instruction.IP.ToString("X8")); break; }
                Console.WriteLine($"{instruction.IP:X8} {instruction.Mnemonic,-12} {instruction.Op0Kind,-14} {instruction.NearBranch32:X8}");
            }
        }
        TestSupport.Assert(calls.Contains(0x40BF0C) && calls.Contains(0x43B4A7)
            && calls.Contains(0x484002) && calls.Contains(0x44270C),
            $"{label}：求值器缺少兵种系/策略类型辅助调用。calls={string.Join(",", calls.Select(value => value.ToString("X8")))}");
        TestSupport.Assert(sawReturnJump, $"{label}：求值器没有跳回原函数 0x{expectedReturn:X8}。");
    }

    private static string FindEngine(string basePath, string name)
    {
        var candidates = new[]
        {
            Path.Combine(basePath, name, "Ekd5.exe"),
            Path.Combine(basePath, name, name, "Ekd5.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("找不到基底：" + name);
    }

    private sealed record Layout(int Pe, int Optional, int SectionTable, ushort Count, uint ImageBase,
        uint SectionAlignment, uint FileAlignment);

    private static Layout ReadLayout(byte[] image)
    {
        var pe = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
        var optional = pe + 24;
        return new Layout(pe, optional, optional + optionalSize, count,
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 28)),
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 32)),
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 36)));
    }

    private static (string Name, uint VirtualAddress, uint RawOffset, uint RawSize, uint VirtualSize)? FindSection(
        byte[] image, Layout layout, string name)
    {
        for (var i = 0; i < layout.Count; i++)
        {
            var at = layout.SectionTable + i * 40;
            var current = System.Text.Encoding.ASCII.GetString(image, at, 8).TrimEnd('\0');
            if (!current.Equals(name, StringComparison.Ordinal)) continue;
            return (current, layout.ImageBase + BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 20)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 16)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 8)));
        }
        return null;
    }

    private static int VaToOffset(byte[] image, Layout layout, uint va)
    {
        var rva = va - layout.ImageBase;
        for (var i = 0; i < layout.Count; i++)
        {
            var at = layout.SectionTable + i * 40;
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 8));
            var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 12));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 16));
            var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + 20));
            if (rva < virtualAddress || rva >= virtualAddress + Math.Max(virtualSize, rawSize)) continue;
            var delta = rva - virtualAddress;
            if (delta >= rawSize) break;
            return checked((int)(rawPointer + delta));
        }
        throw new InvalidOperationException($"VA 0x{va:X8} 不在任何节内。");
    }
}
