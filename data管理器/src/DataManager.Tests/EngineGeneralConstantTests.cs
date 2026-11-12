using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineGeneralConstantTests
{
    public static void Run(string sourceRoot)
    {
        var sourceHash = HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe"));
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new EngineConstantService();
            var before = File.ReadAllBytes(Path.Combine(copy, "Ekd5.exe"));
            var values = service.Read(workspace).ToDictionary(value => value.Definition.Id);
            var selected = new[] { "equip-level-cap-special", "equip-level-cap-normal", "money-cap",
                "ai4-hit-low", "ai4-score-low", "strategy-series-bonus", "hit-direction-step",
                "equip-generation-base-0", "ai3-score" };
            var edits = selected.Select(id =>
            {
                var value = values[id];
                TestSupport.Assert(value.SignatureMatches, $"{id} 基底签名未匹配：{value.Diagnostic}");
                var next = value.NumericValue!.Value == value.Definition.Maximum
                    ? value.NumericValue.Value - 1 : value.NumericValue.Value + 1;
                return new EngineConstantEdit(id, next, null);
            }).ToArray();
            var candidate = service.PrepareBinaryEdit(workspace, edits);
            TestSupport.Assert(before.AsSpan().SequenceEqual(File.ReadAllBytes(candidate.FilePath)),
                "准备常量候选写入了文件。");
            TestSupport.Assert(!before.AsSpan().SequenceEqual(candidate.AfterBytes), "准备候选没有包含编辑。");
            var result = service.Commit(workspace, edits);
            TestSupport.Assert(result.Success, "相邻常量组合写入失败：" + result.Message);
            service.ValidateSavedEdits(workspace, edits);
            foreach (var id in new[] { "equip-level-cap-special", "equip-level-cap-normal" })
            {
                var value = service.Read(workspace).Single(item => item.Definition.Id == id);
                TestSupport.Assert(value.ExtraFileOffsets?.Count > 0, "装备封顶缺少同步站点。");
                var bytes = File.ReadAllBytes(candidate.FilePath);
                for (var index = 0; index < value.ExtraFileOffsets!.Count; index++)
                    TestSupport.Assert(bytes[(int)value.ExtraFileOffsets[index]] ==
                        value.NumericValue + value.Layout.ExtraFields![index].ValueOffset,
                        "装备换代/经验关联上限未同步。");
            }
            var afterValid = HashService.Sha256(candidate.FilePath);
            foreach (var invalid in new[] { new EngineConstantEdit("equip-special-level-divisor", 0, null),
                         new EngineConstantEdit("ai4-score-low", 128, null),
                         new EngineConstantEdit("promotion-max-job-id", 80, null) })
            {
                var rejected = service.Commit(workspace, [invalid]);
                TestSupport.Assert(!rejected.Success, $"非法数值未被拒绝：{invalid.Id}");
                TestSupport.Assert(HashService.Sha256(candidate.FilePath) == afterValid, "非法编辑修改了文件。");
            }
            var restore = service.Commit(workspace, selected.Select(id =>
                new EngineConstantEdit(id, values[id].NumericValue, null)).ToArray());
            TestSupport.Assert(restore.Success && File.ReadAllBytes(candidate.FilePath).AsSpan().SequenceEqual(before),
                "组合常量恢复后没有逐字节还原。");

            var correctionSummary = EngineTierCorrectionReader.ReadSummary(workspace);
            if (workspace.ActiveProfile.Profile.Version == "6.6")
            {
                TestSupport.Assert(correctionSummary.Contains("7 组，0 条记录，16 字节", StringComparison.Ordinal),
                    "6.6 修正表只读扫描没有识别7个空组：" + correctionSummary);
                var correctionBytes = before.ToArray();
                correctionBytes[0xD96C0] = 0;
                File.WriteAllBytes(candidate.FilePath, correctionBytes);
                TestSupport.Assert(EngineTierCorrectionReader.ReadSummary(workspace)
                        .Contains("非空/未知组结构", StringComparison.Ordinal),
                    "6.6 未知修正结构被误判为空组。");
                TestSupport.Assert(File.ReadAllBytes(candidate.FilePath).AsSpan().SequenceEqual(correctionBytes),
                    "修正表只读扫描修改了文件。");
                File.WriteAllBytes(candidate.FilePath, before);
            }
            else
                TestSupport.Assert(correctionSummary == "当前基底不适用", "修正表扫描错误套用了其他基底。");

            // Damage an immutable opcode, then prove exact-address and relocation both refuse it.
            var guarded = values["money-cap"];
            var tampered = before.ToArray();
            tampered[(int)guarded.FileOffset - 1] ^= 0x40;
            File.WriteAllBytes(candidate.FilePath, tampered);
            var unavailable = service.Read(workspace).Single(value => value.Definition.Id == "money-cap");
            TestSupport.Assert(!unavailable.SignatureMatches, "被破坏的金钱上限指令仍可编辑。");
            var refused = service.Commit(workspace, [new("money-cap", 1000000, null)]);
            TestSupport.Assert(!refused.Success && File.ReadAllBytes(candidate.FilePath).AsSpan().SequenceEqual(tampered),
                "签名被破坏时仍然写入。");
            Console.WriteLine($"ENGINE_GENERAL_CONSTANTS_OK {workspace.ActiveProfile.Profile.Version} " +
                $"definitions={values.Count} staged={edits.Length} linkedCaps=2 invalidRejected=3 guardRejected=1");
        }
        finally
        {
            Directory.Delete(copy, true);
            TestSupport.Assert(HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe")) == sourceHash,
                "通用常量验证修改了真实基底。");
        }
    }
}
