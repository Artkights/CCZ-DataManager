using System.Text.Encodings.Web;
using System.Text.Json;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

/// <summary>
/// Proves every effect id has a resolvable read/write path: name/suffix slot,
/// description slot, assignment-table row, and item-selector mapping. Every
/// check is a preview over the untouched workspace; nothing is written.
/// </summary>
internal static class EngineEffectCoverageTests
{
    public static void Run(string root, string? outputDirectory = null)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(root);
        var report = new EngineEffectScanService().Scan(workspace);
        var writer = new EngineEffectWriteService();
        var name = Path.GetFileName(root);
        var nameSlots = 0;
        var nameWritable = 0;
        var descriptionSlots = 0;
        var descriptionWritable = 0;
        var tableRows = 0;
        var tableFailed = new List<string>();
        var nameFailures = new List<string>();
        var descriptionFailures = new List<string>();
        var entries = new List<object>();
        foreach (var row in report.Effects)
        {
            var preview = writer.BuildPreview(workspace, report, new EngineEffectWriteRequest
            {
                EffectId = row.EffectId,
                NewName = row.HasNameSlot ? row.Name : null,
                NewSuffix = row.HasNameSlot ? row.SuffixByte : null,
                NewDescription = row.HasDescriptionSlot ? row.Description : null
            });
            var nameErrors = preview.Errors
                .Where(error => error.StartsWith("名称", StringComparison.Ordinal) ||
                                error.StartsWith("后缀", StringComparison.Ordinal)).ToArray();
            var descriptionErrors = preview.Errors
                .Where(error => error.StartsWith("说明", StringComparison.Ordinal)).ToArray();
            if (row.HasNameSlot)
            {
                nameSlots++;
                if (nameErrors.Length == 0) nameWritable++;
                else nameFailures.Add($"{row.EffectId}：{string.Join("；", nameErrors)}");
            }
            if (row.HasDescriptionSlot)
            {
                descriptionSlots++;
                if (descriptionErrors.Length == 0) descriptionWritable++;
                else descriptionFailures.Add($"{row.EffectId}：{string.Join("；", descriptionErrors)}");
            }
            var table = report.AssignmentTable;
            if (table is { Verified: true } && row.EffectId < table.RowCount)
            {
                tableRows++;
                var expected = table.TableFileOffset + row.EffectId * (long)table.RowSize;
                if (row.HasAssignmentRow && row.AssignmentRowOffset != expected)
                    tableFailed.Add($"{row.EffectId}：行偏移 0x{row.AssignmentRowOffset:X}≠0x{expected:X}");
            }
            entries.Add(new
            {
                row.EffectId, row.Name, row.HasNameSlot, row.HasDescriptionSlot,
                NamePreviewOk = nameErrors.Length == 0, NameErrors = nameErrors,
                DescriptionPreviewOk = descriptionErrors.Length == 0, DescriptionErrors = descriptionErrors,
                AssignmentRow = table is { Verified: true } && row.EffectId < table.RowCount
                    ? $"0x{table.TableFileOffset + row.EffectId * (long)table.RowSize:X}"
                    : null,
                row.AssignmentCovered, row.AssignmentPersonIds, row.AssignmentJobIds
            });
        }
        TestSupport.Assert(nameSlots > 0 && nameWritable == nameSlots,
            "名称/后缀预览未覆盖全部名称槽：" + string.Join(" | ", nameFailures.Take(8)));
        TestSupport.Assert(descriptionSlots > 0 && descriptionWritable == descriptionSlots,
            "说明预览未覆盖全部说明槽：" + string.Join(" | ", descriptionFailures.Take(8)));
        TestSupport.Assert(tableFailed.Count == 0,
            "静态分配表行地址与几何不符：" + string.Join(" | ", tableFailed.Take(8)));

        var catalog = new EngineEffectAssignmentService().Read(workspace, report);
        var selectors = catalog.Items
            .Where(item => item.SelectorFileOffset >= 0 && item.Selector is >= 0 and < 255)
            .Select(item => item.Selector).Distinct().Order().ToArray();
        var knownIds = report.Effects.Select(row => row.EffectId).ToHashSet();
        var missing = selectors.Where(selector => !knownIds.Contains(selector)).ToArray();
        TestSupport.Assert(missing.Length == 0,
            "宝物 selector 指向不存在的特效编号：" + string.Join(",", missing));

        Console.WriteLine($"ENGINE_EFFECT_COVERAGE_OK {report.EngineVersion} {name} " +
                          $"effects={report.Effects.Count} nameSlots={nameSlots}/{nameWritable} " +
                          $"descSlots={descriptionSlots}/{descriptionWritable} tableRows={tableRows} " +
                          $"selectors={selectors.Length} invalidNames={nameFailures.Count}");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        Directory.CreateDirectory(outputDirectory);
        var payload = new
        {
            report.EngineVersion, report.ExeSha256,
            Counts = new { Effects = report.Effects.Count, NameSlots = nameSlots, NameWritable = nameWritable,
                DescriptionSlots = descriptionSlots, DescriptionWritable = descriptionWritable,
                TableRows = tableRows, Selectors = selectors.Length,
                InvalidNames = nameFailures.Count, InvalidDescriptions = descriptionFailures.Count },
            InvalidNames = nameFailures, InvalidDescriptions = descriptionFailures,
            Selectors = selectors.Select(selector => new
            {
                Selector = selector,
                Items = catalog.Items.Where(item => item.Selector == selector && item.SelectorFileOffset >= 0)
                    .Select(item => $"{item.Id}:{item.Name}").ToArray()
            }).ToArray(),
            Effects = entries
        };
        File.WriteAllText(Path.Combine(outputDirectory, $"引擎特效读写覆盖-{name}.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        RunEffectSlotRoundTrip(root);
    }

    /// <summary>
    /// 6.6-only person-effect channel: change one Effect.e5 slot value on a temp copy,
    /// save, reopen and re-read. 6.4/6.5 have no effect-archive document and skip.
    /// </summary>
    private static void RunEffectSlotRoundTrip(string root)
    {
        var copy = TestSupport.CreateCoreCopy(root, restoreKnown65Snapshot: false);
        try
        {
            // The core copy only carries Ekd5.exe/Imsg.e5; the person-effect channel
            // needs the extension file itself.
            var archive = Path.Combine(root, "Effect.e5");
            if (File.Exists(archive)) File.Copy(archive, Path.Combine(copy, "Effect.e5"), true);
            var workspace = new ProjectWorkspace();
            workspace.Open(copy);
            var service = new ExtendDataService(workspace);
            if (!service.GetStatus(ExtendDataCategory.Effects).Available) return;
            var references = new EngineEffectReferenceService().Build(workspace);
            var pair = references.Where(item => item.Value.PersonAssignments.Count > 0)
                .OrderBy(item => item.Key).FirstOrDefault();
            if (pair.Value == null) return;
            var assigned = pair.Value.PersonAssignments[0];
            var slots = service.GetEffects(assigned.PersonId);
            var slot = slots.First(item => item.EffectId == pair.Key);
            var value = slot.Value < 255 ? slot.Value + 1 : 0;
            var applied = service.SetEffect(assigned.PersonId, slot.Slot, pair.Key, value);
            TestSupport.Assert(applied.Applied && applied.ChangedCells > 0,
                "Effect.e5 人物槽写入未生效：" + string.Join("；", applied.Messages));
            var saved = workspace.Save();
            TestSupport.Assert(saved.Saved, "Effect.e5 人物槽保存失败：" + string.Join("；", saved.Messages));
            var reopened = new ProjectWorkspace();
            reopened.Open(copy);
            var reread = new ExtendDataService(reopened).GetEffects(assigned.PersonId)
                .First(item => item.Slot == slot.Slot);
            TestSupport.Assert(reread.EffectId == pair.Key && reread.Value == value,
                $"Effect.e5 人物槽复读不符：{reread.EffectId}/{reread.Value}，应为 {pair.Key}/{value}");
            var rereadRefs = new EngineEffectReferenceService().Build(reopened);
            TestSupport.Assert(rereadRefs[pair.Key].PersonAssignments.Any(item =>
                    item.PersonId == assigned.PersonId && item.Slot == slot.Slot && item.Value == value),
                "Effect.e5 写回后跨文件引用视图没有更新。");
            Console.WriteLine($"ENGINE_EFFECT_SLOT_ROUNDTRIP_OK 人物{assigned.PersonId} 槽{slot.Slot} " +
                              $"特效{pair.Key} 值{slot.Value}->{value}");
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }
}
