using System.Runtime.InteropServices;
using EngineGrowth.Core;
using TierManager.App;

namespace TransformManager.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (!AttachConsole(-1)) AllocConsole();
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
        if (args.Length >= 2 && args[0] == "--verify")
            return ScanVerifier.Run(args[1]) ? 0 : 1;

        if (args.Length >= 2 && args[0] == "--scan")
            return Scan(args[1]) ? 0 : 1;

        if (args.Length >= 2 && args[0] == "--sections")
        {
            var pe = new PeFile(args[1]);
            foreach (var section in pe.Sections)
                Console.WriteLine($"{section.Name} VA=0x{section.VirtualAddress:X} VSIZE=0x{section.VirtualSize:X} RAW=0x{section.RawSize:X} PTR=0x{section.RawPointer:X}");
            return 0;
        }

        if (args.Length >= 3 && args[0] == "--probe")
        {
            var pe = new PeFile(args[1]);
            uint entry = Convert.ToUInt32(args[2], 16);
            Console.WriteLine($"entry=0x{entry:X8}");
            Console.WriteLine("bytes=" + string.Join(' ', pe.ReadBytes(entry, 16).Select(b => b.ToString("X2"))));
            Console.WriteLine("entry+6=" + string.Join(' ', pe.ReadBytes(entry + 6, 3).Select(b => b.ToString("X2"))) +
                              "  entry+9=" + string.Join(' ', pe.ReadBytes(entry + 9, 4).Select(b => b.ToString("X2"))));
            var head = pe.ReadBytes(entry, 5);
            if (head.Length == 5 && head[0] == 0xE9)
            {
                uint target = entry + 5 + unchecked((uint)BitConverter.ToInt32(head, 1));
                var mem = pe.ReadBytes(target, 8192);
                var metadata = RuleCompiler.ReadMetadata(mem);
                Console.WriteLine($"jump=0x{target:X8} mem={mem.Length} magicAt={mem.AsSpan().IndexOf("TMR1"u8)} meta={(metadata == null ? "null" : "ok")}");
            }
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--roundtrip")
            return Roundtrip(args[1]) ? 0 : 1;

        if (args.Length >= 2 && args[0] == "--roundtrip-multi")
            return MultiRoundtrip(args[1]) ? 0 : 1;

        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm(args.Length >= 1 && Directory.Exists(args[0]) ? args[0] : null));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "transform-ui-error.log"), ex.ToString());
            throw;
        }
        return 0;
    }

    private static bool MultiRoundtrip(string sourceRoot)
    {
        string temp = Path.Combine(Path.GetTempPath(), "transform-multi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Console.WriteLine("TEMP=" + temp);
        try
        {
            foreach (string name in new[] { "Ekd5.exe", "Data.e5", "Star.e5" })
            {
                string from = Path.Combine(sourceRoot, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(temp, name));
            }
            NormalizeManagedFixture(temp);
            var service = TreasureTransformService.Load(temp);
            if (!service.Base.CanEdit)
            {
                Console.WriteLine($"FAIL  基底只读：{service.Base.ReadOnlyReason}");
                return false;
            }
            bool wasPatched = service.Base.IsPatched;
            var slots = service.Slots.Where(slot => slot.Refs.Count > 0).Take(2).ToArray();
            if (slots.Length < 2)
            {
                Console.WriteLine("FAIL  可管理槽位不足 2 个");
                return false;
            }
            int a = slots[0].Id, b = slots[1].Id;
            int first = service.Base.CoverageFirst;
            int effectCount = service.Base.CoverageLast - first + 1;
            var extraBefore = Enumerable.Range(first, effectCount)
                .Select(id => service.Base.ReadExtra(id)).ToArray();
            var beforeBytes = File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe"));
            int entryOffset = service.Base.Exe.VaToOffset(service.Base.TransformEntryVa);
            int caveOffset = service.Base.Cave is { } initialCave
                ? service.Base.Exe.VaToOffset(initialCave.Address) : -1;
            int caveLength = service.Base.Cave?.Length ?? 0;
            int offA = service.Base.Exe.VaToOffset(
                service.Base.CoverageTableVa + (uint)(a - first));
            int offB = service.Base.Exe.VaToOffset(
                service.Base.CoverageTableVa + (uint)(b - first));

            List<(int Offset, int Length)> Allowed(bool withCode)
            {
                var list = new List<(int Offset, int Length)> { (offA, 1), (offB, 1) };
                if (withCode)
                {
                    list.Add((entryOffset, 5));
                    if (caveLength > 0) list.Add((caveOffset, caveLength));
                }
                return list;
            }

            var rulesA = new List<RuleStage> { new(5, 5), new(9, 9) };
            var rulesB = new List<RuleStage> { new(5, 6), new(9, 8) };
            var write = service.Apply([
                new SlotEdit(a, 200, rulesA),
                new SlotEdit(b, 201, rulesB)
            ]);
            Console.WriteLine($"{(write.Success ? "PASS" : "FAIL")}  多槽写入：{write.Message}");
            if (!write.Success) return false;

            var reloaded = TreasureTransformService.Load(temp);
            bool coverageOk = reloaded.Find(a)?.Coverage == 200 && reloaded.Find(b)?.Coverage == 201;
            bool rulesOk = RuleEquals(reloaded, a, rulesA) && RuleEquals(reloaded, b, rulesB);
            bool extraPreserved = Enumerable.Range(first, effectCount)
                .Select(id => reloaded.Base.ReadExtra(id)).SequenceEqual(extraBefore);
            int diffWrite = CountUnexpectedChanges(beforeBytes,
                File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), Allowed(true).ToArray());
            Console.WriteLine($"{(coverageOk ? "PASS" : "FAIL")}  多槽覆盖值回读");
            Console.WriteLine($"{(rulesOk ? "PASS" : "FAIL")}  多槽规则回读");
            Console.WriteLine($"{(extraPreserved ? "PASS" : "FAIL")}  多槽写入后缀表未改动");
            Console.WriteLine($"{(diffWrite == 0 ? "PASS" : "FAIL")}  多槽写入仅授权区域变化（越界 {diffWrite} 字节）");
            if (!coverageOk || !rulesOk || !extraPreserved || diffWrite != 0) return false;

            var update = reloaded.Apply([
                new SlotEdit(a, null, new List<RuleStage> { new(5, 7) })
            ]);
            Console.WriteLine($"{(update.Success ? "PASS" : "FAIL")}  重复写入：{update.Message}");
            if (!update.Success) return false;

            var reloaded2 = TreasureTransformService.Load(temp);
            bool updateRulesOk = RuleEquals(reloaded2, a, new List<RuleStage> { new(5, 7) }) &&
                                 RuleEquals(reloaded2, b, rulesB);
            bool updateCoverageOk = reloaded2.Find(a)?.Coverage == 200 &&
                                    reloaded2.Find(b)?.Coverage == 201;
            int diffUpdate = CountUnexpectedChanges(beforeBytes,
                File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), Allowed(true).ToArray());
            Console.WriteLine($"{(updateRulesOk ? "PASS" : "FAIL")}  重复写入保留未编辑槽位规则");
            Console.WriteLine($"{(updateCoverageOk ? "PASS" : "FAIL")}  重复写入覆盖值");
            Console.WriteLine($"{(diffUpdate == 0 ? "PASS" : "FAIL")}  重复写入仅授权区域变化（越界 {diffUpdate} 字节）");
            if (!updateRulesOk || !updateCoverageOk || diffUpdate != 0) return false;

            var clearB = reloaded2.Apply([
                new SlotEdit(b, 0xFE, Array.Empty<RuleStage>())
            ]);
            Console.WriteLine($"{(clearB.Success ? "PASS" : "FAIL")}  单槽清理 B：{clearB.Message}");
            if (!clearB.Success) return false;

            var reloaded3 = TreasureTransformService.Load(temp);
            bool clearBRulesOk = RuleEquals(reloaded3, a, new List<RuleStage> { new(5, 7) }) &&
                                 !reloaded3.Rules.ContainsKey(b);
            bool clearBCoverageOk = reloaded3.Find(a)?.Coverage == 200 &&
                                    reloaded3.Find(b)?.Coverage == 0xFE;
            bool stillPatched = reloaded3.Base.Exe.ReadByte(reloaded3.Base.TransformEntryVa) == 0xE9;
            int diffClearB = CountUnexpectedChanges(beforeBytes,
                File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), Allowed(true).ToArray());
            Console.WriteLine($"{(clearBRulesOk ? "PASS" : "FAIL")}  单槽清理 B 保留 A 规则");
            Console.WriteLine($"{(clearBCoverageOk ? "PASS" : "FAIL")}  单槽清理 B 覆盖值");
            Console.WriteLine($"{(stillPatched ? "PASS" : "FAIL")}  单槽清理 B 后入口仍为规则跳转");
            Console.WriteLine($"{(diffClearB == 0 ? "PASS" : "FAIL")}  单槽清理 B 仅授权区域变化（越界 {diffClearB} 字节）");
            if (!clearBRulesOk || !clearBCoverageOk || !stillPatched || diffClearB != 0) return false;

            var clearA = reloaded3.Apply([
                new SlotEdit(a, 0xFE, Array.Empty<RuleStage>())
            ]);
            Console.WriteLine($"{(clearA.Success ? "PASS" : "FAIL")}  单槽清理 A：{clearA.Message}");
            if (!clearA.Success) return false;

            var final = TreasureTransformService.Load(temp);
            bool entryRestored = final.Base.Exe.ReadByte(final.Base.TransformEntryVa) == 0x55;
            bool caveRestored = final.Base.Cave is not { } cave ||
                                final.Base.Exe.ReadBytes(cave.Address, 16).AsSpan()
                                    .SequenceEqual(cave.OriginalBytes.AsSpan(0, 16));
            bool clearCoverage = final.Find(a)?.Coverage == 0xFE && final.Find(b)?.Coverage == 0xFE;
            int diffClear = CountUnexpectedChanges(beforeBytes,
                File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), Allowed(wasPatched).ToArray());
            Console.WriteLine($"{(entryRestored ? "PASS" : "FAIL")}  多槽清理入口恢复");
            Console.WriteLine($"{(caveRestored ? "PASS" : "FAIL")}  多槽清理代码洞恢复");
            Console.WriteLine($"{(clearCoverage ? "PASS" : "FAIL")}  多槽清理覆盖值");
            Console.WriteLine($"{(diffClear == 0 ? "PASS" : "FAIL")}  多槽清理仅覆盖值变化（越界 {diffClear} 字节）");
            if (!entryRestored || !caveRestored || !clearCoverage || diffClear != 0) return false;

            var afterClearBytes = File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe"));
            var coverageOnly = final.Apply([
                new SlotEdit(a, 0x7F, null),
                new SlotEdit(b, 0x6F, null)
            ]);
            int diffCoverageOnly = coverageOnly.Success
                ? CountUnexpectedChanges(afterClearBytes,
                    File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), Allowed(false).ToArray())
                : int.MaxValue;
            Console.WriteLine($"{(coverageOnly.Success ? "PASS" : "FAIL")}  多槽纯覆盖值：{coverageOnly.Message}");
            Console.WriteLine($"{(diffCoverageOnly == 0 ? "PASS" : "FAIL")}  多槽纯覆盖值只改授权字节（越界 {diffCoverageOnly} 字节）");
            return coverageOnly.Success && diffCoverageOnly == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  多槽往返异常：{ex.Message}");
            return false;
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("TRANSFORM_KEEP") != "1")
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }

    private static bool RuleEquals(TreasureTransformService service, int effectId,
        IReadOnlyList<RuleStage> expected)
    {
        if (!service.Rules.TryGetValue(effectId, out var actual) || actual.Count != expected.Count)
            return false;
        return expected.All(stage => actual.Any(other =>
            other.Level == stage.Level && other.Value == stage.Value));
    }

    private static void NormalizeManagedFixture(string root)
    {
        // Keep the transform-only diff assertions below strict. A historical
        // TIER1 migration can repair its own known CALL encoding and metadata,
        // so perform that verified coexistence setup on the test copy first.
        var path = Path.Combine(root, "Ekd5.exe");
        var before = File.ReadAllBytes(path);
        var service = TreasureTransformService.Load(root, before);
        var tiers = TierScanner.LoadFromBytes(path, before, root);
        if (!service.Base.IsPatched && !tiers.HasMarker) return;
        var plan = GrowthPatchCoordinator.Prepare(root, before, service.Catalog, null, null);
        plan.Verify(plan.AfterBytes);
        var afterTiers = TierScanner.LoadFromBytes(path, plan.AfterBytes, root);
        var afterRules = TreasureTransformService.Load(root, plan.AfterBytes, service.Catalog);
        if (afterTiers.TierBase != tiers.TierBase || !afterTiers.Thresholds.SequenceEqual(tiers.Thresholds) ||
            !afterTiers.Labels.SequenceEqual(tiers.Labels) || service.Rules.Count != afterRules.Rules.Count ||
            service.Rules.Any(pair => !RuleEquals(afterRules, pair.Key, pair.Value)))
            throw new InvalidDataException("测试副本旧成长补丁迁移改变了档位或已有质变规则。");
        File.WriteAllBytes(path, plan.AfterBytes);
        Console.WriteLine("PASS  旧成长补丁迁移（仅测试副本，档位与已有规则保持）");
    }

    private static int CountUnexpectedChanges(byte[] before, byte[] after,
        params (int Offset, int Length)[] allowed)
    {
        if (before.Length != after.Length) return int.MaxValue;
        var permitted = new HashSet<int>();
        foreach (var (offset, length) in allowed)
        {
            int start = Math.Max(0, offset);
            int end = Math.Min(after.Length, offset + length);
            for (int i = start; i < end; i++) permitted.Add(i);
        }
        int count = 0;
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i] && !permitted.Contains(i)) count++;
        return count;
    }

    private static bool Scan(string root)
    {
        var service = TreasureTransformService.Load(root);
        foreach (var line in service.Base.Diagnostics) Console.WriteLine(line);
        Console.WriteLine($"契约：{(service.ContractErrors.Count == 0 ? "通过" : string.Join("；", service.ContractErrors))}");
        Console.WriteLine($"槽位：{service.Slots.Count}，有规则的：{service.Slots.Count(slot => slot.Stages.Count > 0)}");
        foreach (var slot in service.Slots.Take(8))
        {
            Console.WriteLine($"  0x{slot.Id:X2}  {slot.Name}  覆盖=0x{slot.Coverage:X2}  引用={slot.Refs.Count}");
        }
        return service.ContractErrors.Count == 0 && service.Base.CanEdit;
    }

    private static bool Roundtrip(string sourceRoot)
    {
        string temp = Path.Combine(Path.GetTempPath(), "transform-roundtrip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Console.WriteLine("TEMP=" + temp);
        try
        {
            foreach (string name in new[] { "Ekd5.exe", "Data.e5", "Star.e5" })
            {
                string from = Path.Combine(sourceRoot, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(temp, name));
            }

            NormalizeManagedFixture(temp);
            var service = TreasureTransformService.Load(temp);
            if (!service.Base.CanEdit)
            {
                Console.WriteLine($"FAIL  基底只读：{service.Base.ReadOnlyReason}");
                return false;
            }
            bool wasPatched = service.Base.IsPatched;
            var slot = service.Find(0x1A) ?? service.Slots.FirstOrDefault();
            if (slot == null)
            {
                Console.WriteLine("FAIL  没有可用的特效号槽位");
                return false;
            }

            int firstEffect = service.Base.CoverageFirst;
            int effectCount = service.Base.CoverageLast - firstEffect + 1;
            var extraBefore = Enumerable.Range(firstEffect, effectCount)
                .Select(id => service.Base.ReadExtra(id)).ToArray();
            int untouchedId = slot.Id == firstEffect ? firstEffect + 1 : firstEffect;
            byte untouchedCoverageBefore = service.Base.ReadCoverage(untouchedId);
            var beforeBytes = File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe"));

            var rules = new List<RuleStage> { new(5, 5), new(9, 9) };
            var preview = service.Preview(slot.Id, rules, 200, ExtraMode.Auto);
            bool previewOk = preview.First(row => row.Level == 5).Value == 5 &&
                             preview.First(row => row.Level == 9).Value == 9;
            Console.WriteLine($"{(previewOk ? "PASS" : "FAIL")}  预览：5级={preview.First(r => r.Level == 5).Value}，9级={preview.First(r => r.Level == 9).Value}");

            var outcome = service.Apply([new SlotEdit(slot.Id, 200, rules)]);
            Console.WriteLine($"{(outcome.Success ? "PASS" : "FAIL")}  写入：{outcome.Message}");
            if (!outcome.Success) return false;

            var reloaded = TreasureTransformService.Load(temp);
            bool coverageOk = reloaded.Find(slot.Id)?.Coverage == 200;
            bool rulesOk = reloaded.Rules.TryGetValue(slot.Id, out var stored) &&
                           stored.Any(stage => stage.Level == 5 && stage.Value == 5) &&
                           stored.Any(stage => stage.Level == 9 && stage.Value == 9);
            bool extraPreserved = Enumerable.Range(firstEffect, effectCount)
                .Select(id => reloaded.Base.ReadExtra(id)).SequenceEqual(extraBefore);
            bool untouchedCoverage = reloaded.Base.ReadCoverage(untouchedId) == untouchedCoverageBefore;
            int coverageOffset = service.Base.Exe.VaToOffset(
                service.Base.CoverageTableVa + (uint)(slot.Id - service.Base.CoverageFirst));
            var writeAllowed = new List<(int Offset, int Length)>
            {
                (service.Base.Exe.VaToOffset(service.Base.TransformEntryVa), 5),
                (coverageOffset, 1)
            };
            if (service.Base.Cave is { } diffCave)
                writeAllowed.Add((service.Base.Exe.VaToOffset(diffCave.Address), diffCave.Length));
            int unexpected = CountUnexpectedChanges(beforeBytes,
                File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), writeAllowed.ToArray());
            bool fileDiffOk = unexpected == 0;
            Console.WriteLine($"{(coverageOk ? "PASS" : "FAIL")}  覆盖值回读");
            Console.WriteLine($"{(rulesOk ? "PASS" : "FAIL")}  规则回读");
            Console.WriteLine($"{(extraPreserved ? "PASS" : "FAIL")}  后缀类型表未改动");
            Console.WriteLine($"{(untouchedCoverage ? "PASS" : "FAIL")}  未编辑槽位覆盖值未改动");
            Console.WriteLine($"{(fileDiffOk ? "PASS" : "FAIL")}  全文件仅授权区域变化（越界 {unexpected} 字节）");
            if (Environment.GetEnvironmentVariable("TRANSFORM_SKIP_CLEAR") == "1")
                return previewOk && coverageOk && rulesOk && extraPreserved && untouchedCoverage && fileDiffOk;

            var clear = reloaded.Apply([new SlotEdit(slot.Id, 0xFE, Array.Empty<RuleStage>())]);
            Console.WriteLine($"{(clear.Success ? "PASS" : "FAIL")}  清理：{clear.Message}");
            if (!clear.Success) return false;

            var final = TreasureTransformService.Load(temp);
            bool entryRestored = final.Base.Exe.ReadByte(final.Base.TransformEntryVa) == 0x55;
            bool caveRestored = final.Base.Cave is not { } cave ||
                                final.Base.Exe.ReadBytes(cave.Address, 16).AsSpan()
                                    .SequenceEqual(cave.OriginalBytes.AsSpan(0, 16));
            var clearAllowed = new List<(int Offset, int Length)> { (coverageOffset, 1) };
            if (wasPatched)
            {
                clearAllowed.Add((service.Base.Exe.VaToOffset(service.Base.TransformEntryVa), 5));
                if (service.Base.Cave is { } beforeCave)
                    clearAllowed.Add((service.Base.Exe.VaToOffset(beforeCave.Address), beforeCave.Length));
            }
            int clearUnexpected = CountUnexpectedChanges(beforeBytes,
                File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), clearAllowed.ToArray());
            bool clearDiffOk = clearUnexpected == 0;
            Console.WriteLine($"{(entryRestored ? "PASS" : "FAIL")}  入口恢复");
            Console.WriteLine($"{(caveRestored ? "PASS" : "FAIL")}  代码洞恢复");
            Console.WriteLine($"{(clearDiffOk ? "PASS" : "FAIL")}  清理后仅覆盖值字节变化（越界 {clearUnexpected} 字节）");

            // 纯覆盖值写入：入口/代码洞必须完全不动，只允许覆盖表单字节变化。
            // 对迁移基底，清理后已回到未打补丁状态，因此以清理后的字节为基准。
            var afterClearBytes = File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe"));
            var coverageOnly = final.Apply([new SlotEdit(slot.Id, 0x7F, null)]);
            bool coverageOnlyOk = coverageOnly.Success;
            int coverageOnlyUnexpected = coverageOnlyOk
                ? CountUnexpectedChanges(afterClearBytes,
                    File.ReadAllBytes(Path.Combine(temp, "Ekd5.exe")), (coverageOffset, 1))
                : int.MaxValue;
            Console.WriteLine($"{(coverageOnlyOk ? "PASS" : "FAIL")}  纯覆盖值写入：{coverageOnly.Message}");
            Console.WriteLine($"{(coverageOnlyUnexpected == 0 ? "PASS" : "FAIL")}  纯覆盖值写入只改授权字节（越界 {coverageOnlyUnexpected} 字节）");

            return previewOk && coverageOk && rulesOk && extraPreserved && untouchedCoverage &&
                   fileDiffOk && entryRestored && caveRestored && clearDiffOk &&
                   coverageOnlyOk && coverageOnlyUnexpected == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  往返测试异常：{ex.Message}");
            return false;
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("TRANSFORM_KEEP") != "1")
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
