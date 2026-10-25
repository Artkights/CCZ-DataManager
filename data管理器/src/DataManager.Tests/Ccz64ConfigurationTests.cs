using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class Ccz64ConfigurationTests
{
    public static void Run(string source)
    {
        var original = ProfileDetector.CoreFiles.ToDictionary(name => name,
            name => HashService.Sha256(ProjectFileLocator.Resolve(source, name)));
        var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        try
        {
            var workspace = Open(root);
            AssertOpen(workspace);
            Mapping(workspace);
            WriteAndHistory(workspace);
            PreviousRevision(root);
            Consumers(root);
        }
        finally { Directory.Delete(root, true); }
        foreach (var pair in original)
            TestSupport.Assert(HashService.Sha256(ProjectFileLocator.Resolve(source, pair.Key)) == pair.Value,
                "配置专项改写原件：" + pair.Key);
        Console.WriteLine("CCZ64_CONFIGURATION_TESTS_OK retreat-49 person-defaults-level-exp permissions-26 native-mapping corrupt-consumers strict-prior-history byte-isolation originals-unchanged");
    }

    private static ProjectWorkspace Open(string root)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(root); return workspace;
    }

    private static void AssertOpen(ProjectWorkspace workspace)
    {
        var probe = Ccz64ConfigurationProbe.Inspect(File.ReadAllBytes(ProjectFileLocator.Resolve(workspace.RootPath, "Ekd5.exe")));
        TestSupport.Assert(probe is { RetreatQuotes: true, PersonDefaults: true, EquipmentPermissions: true, SpecialCriticalMap: true },
            "洗隋完整配置消费链未全部通过：" + probe);
        var quotes = workspace.GetDocument("person-retreat-quotes");
        TestSupport.Assert(quotes.Definition is { DataOffset: 130000, RowCount: 49, RowSize: 200 } && quotes.Data.Rows.Count == 49,
            "撤退台词几何错误");
        TestSupport.Assert(Convert.ToString(quotes.Data.Rows[0]["台词"]) == "剑在人在，剑亡…人亡！" &&
            Convert.ToString(quotes.Data.Rows[48]["台词"]) == "何人？竟识破我的计谋？", "撤退首末文本不是实际洗隋内容");
        TestSupport.Assert(workspace.GetDocument("persons").Definition.Fields.Where(field => field.Offset >= 27)
            .All(field => field.Policy == FieldPolicy.Editable), "人物级别/经验/默认装备未开放");
        TestSupport.Assert(workspace.GetDocument("detailed-job-growth").Definition.Fields.Count(field =>
            field.Offset >= 9 && field.Size == 1 && field.Policy == FieldPolicy.Editable) == 26, "26类许可字段不全");
        TestSupport.Assert(!workspace.Documents.ContainsKey("strategy-extension"), "策略扩展未经证明而开放");
    }

    private static void Mapping(ProjectWorkspace workspace)
    {
        var service = new PersonQuoteService(workspace);
        TestSupport.Assert(service.Resolve(0).Retreat?.Id == 0 && service.Resolve(48).Retreat?.Id == 48,
            "专属撤退行首末人物映射错误");
        var persons = workspace.GetDocument("persons");
        var old = Convert.ToInt32(persons.Data.Rows[49]["撤退台词"]);
        var low = old == 0 ? 1 : 0;
        workspace.ApplyCompositeTransaction("撤退映射证据", [new("persons", 49, "撤退台词", low)]);
        TestSupport.Assert(service.Resolve(49).Retreat == null && service.Resolve(1023).Retreat == null,
            "错误地用人物记录+15替代原生人物ID撤退映射");
        workspace.Commands.Undo();
        TestSupport.Assert(!workspace.IsDirty, "撤退映射测试没有清理草稿");
    }

    private static void WriteAndHistory(ProjectWorkspace workspace)
    {
        var before = ProfileDetector.CoreFiles.ToDictionary(name => name,
            name => File.ReadAllBytes(ProjectFileLocator.Resolve(workspace.RootPath, name)));
        var edits = new List<CompositeCellEdit>();
        foreach (var row in new[] { 0, 1023 })
        foreach (var field in new[] { "级别", "经验", "默认武器", "默认防具", "默认辅助" })
        {
            var old = Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[row][field]);
            var value = field.StartsWith("默认", StringComparison.Ordinal) ? 255 : (old + 1) % 256;
            // The selected 6.4 fixture has equipment in both boundary records.
            if (old == value && field.StartsWith("默认", StringComparison.Ordinal))
                value = PersonEquipmentService.GetOptions(workspace.Documents, field).First(option => option.Id != old).Id;
            edits.Add(new("persons", row, field, value));
        }
        foreach (var row in new[] { 0, 79 })
        foreach (var field in ItemTypeService.EquipmentColumns)
            edits.Add(new("detailed-job-growth", row, field,
                (Convert.ToInt32(workspace.GetDocument("detailed-job-growth").Data.Rows[row][field]) + 1) % 256));
        edits.Add(new("person-retreat-quotes", 0, "台词", "六四首条撤退验证"));
        edits.Add(new("person-retreat-quotes", 48, "台词", "六四末条撤退验证"));
        workspace.ApplyCompositeTransaction("6.4配置首末与全部许可写回", edits);
        TestSupport.Assert(workspace.Save().Saved, "6.4配置保存失败");
        var reopened = Open(workspace.RootPath);
        TestSupport.Assert(reopened.ActiveProfile.LayoutRevision == workspace.ActiveProfile.LayoutRevision,
            "数据保存改变了已验证消费链的布局身份");
        foreach (var edit in edits)
            TestSupport.Assert(Convert.ToString(reopened.GetDocument(edit.TableKey).Data.Rows[edit.RowIndex][edit.FieldName]) ==
                Convert.ToString(edit.NewValue), "配置磁盘复读失败：" + edit);
        foreach (var pair in before)
        {
            var allowed = edits.Select(edit => (Edit: edit, Table: reopened.GetDocument(edit.TableKey).Definition))
                .Where(item => item.Table.FileName.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(item =>
                {
                    var field = item.Table.Fields.Single(field => field.Name == item.Edit.FieldName);
                    return Enumerable.Range(checked((int)(item.Table.DataOffset + item.Edit.RowIndex * (long)item.Table.RowSize + field.Offset)), field.Size);
                }).ToHashSet();
            var after = File.ReadAllBytes(ProjectFileLocator.Resolve(workspace.RootPath, pair.Key));
            TestSupport.Assert(after.Length == pair.Value.Length && pair.Value.Zip(after)
                .Select((bytes, index) => bytes.First == bytes.Second || allowed.Contains(index)).All(value => value),
                "配置保存越过本次字段区间：" + pair.Key);
        }
        TestSupport.Assert(reopened.UndoSaved().Success, "配置保存无法撤回");
        AssertBytes(workspace.RootPath, before);
        TestSupport.Assert(reopened.RedoSaved().Success && reopened.UndoSaved().Success, "配置保存无法重做再撤回");
        AssertBytes(workspace.RootPath, before);
    }

    private static void PreviousRevision(string root)
    {
        var workspace = Open(root);
        var revisions = LayoutRevisionCompatibility.GetLegacyRevisions(root, "ccz-6.4", "64-xisui-v1");
        TestSupport.Assert(revisions.Count == 1 && revisions[0] != workspace.ActiveProfile.LayoutRevision,
            "未严格复现已发布的上一版6.4布局");
        TestSupport.Assert(LayoutRevisionCompatibility.IsCompatible(root, "ccz-6.4", "64-xisui-v1", revisions[0], workspace.ActiveProfile.LayoutRevision) &&
            !LayoutRevisionCompatibility.IsCompatible(root, "ccz-6.4", "64-xisui-v1", "DEADBEEF", workspace.ActiveProfile.LayoutRevision),
            "上一版布局白名单边界错误");
        var oldPower = Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]);
        var value = (oldPower + 1) % 200;
        workspace.ApplyCompositeTransaction("上一版人物字段历史", [new("persons", 0, "武力", value)]);
        TestSupport.Assert(workspace.Save().Saved, "旧字段历史准备失败");
        var store = new HistoryStore(); var history = store.Load(root).Last(entry => entry.Status == HistoryStatus.Active);
        history.LayoutRevision = revisions[0]; store.Update(root, history);
        var reopened = Open(root);
        TestSupport.Assert(reopened.UndoSaved().Success && Convert.ToInt32(reopened.GetDocument("persons").Data.Rows[0]["武力"]) == oldPower,
            "严格冻结的上一版人物字段历史无法撤回");
        TestSupport.Assert(reopened.RedoSaved().Success && reopened.UndoSaved().Success, "上一版历史重做失败");
        var csv = Path.Combine(root, "prior-person-fields.csv");
        CsvService.WriteRecords(csv, [
            new[] { "#DataManagerCSV", "1", "ccz-6.4", "64-xisui-v1", "persons", revisions[0] },
            new[] { "ID", "武力" }, new[] { "0", oldPower.ToString() }]);
        TestSupport.Assert(reopened.ImportCsv(reopened.GetDocument("persons"), csv).ChangedCells == 0 && !reopened.IsDirty,
            "上一版相同物理字段CSV不兼容");
    }

    private static void Consumers(string root)
    {
        var path = ProjectFileLocator.Resolve(root, "Ekd5.exe"); var original = File.ReadAllBytes(path);
        try
        {
            var custom = original.ToArray(); custom[0x89C30] ^= 1; custom[0xD0D60] ^= 1;
            File.WriteAllBytes(path, custom);
            TestSupport.Assert(Ccz64ConfigurationProbe.Inspect(custom) is { RetreatQuotes: true, PersonDefaults: true, EquipmentPermissions: true, SpecialCriticalMap: true },
                "修改配置数据使消费链失效");
            foreach (var (offset, channel) in new[] { (0xB41A, "retreat"), (0x40BBA, "retreat"), (0x2B07F, "messages"),
                (0x800C, "person"), (0x1AA25, "person"), (0x1AB69, "permission"), (0x1BA18, "permission"),
                (0xA84C, "permission"), (0x7481, "permission"), (0xB447, "critical") })
            {
                var damaged = original.ToArray(); damaged[offset] ^= 1; File.WriteAllBytes(path, damaged);
                var workspace = Open(root);
                if (channel is "retreat" or "messages") TestSupport.Assert(!workspace.Documents.ContainsKey("person-retreat-quotes"), "损坏撤退消费链仍读取台词");
                if (channel is "critical" or "messages") TestSupport.Assert(!workspace.Documents.ContainsKey("person-critical-special-map"), "损坏暴击消费链仍开放映射");
                if (channel == "person") TestSupport.Assert(workspace.GetDocument("persons").Definition.Fields.Where(field => field.Offset >= 27)
                    .All(field => field.Policy == FieldPolicy.Preserve), "损坏人物消费链未保留尾字段");
                if (channel == "permission") TestSupport.Assert(workspace.GetDocument("detailed-job-growth").Definition.Fields.Where(field => field.Offset >= 9)
                    .All(field => field.Policy == FieldPolicy.Preserve), "损坏许可消费链仍开放26字节");
                TestSupport.Assert(workspace.GetDocument("persons").Definition.Fields.Single(field => field.Name == "武力").Policy == FieldPolicy.Editable,
                    "辅助消费链失效关闭了无关人物字段");
            }
            var duplicate = original.ToArray(); Array.Copy(original, 0xB411, duplicate, 0x60000, 30);
            TestSupport.Assert(!Ccz64ConfigurationProbe.Inspect(duplicate).RetreatQuotes, "重复完整消费者被当作唯一证据");
            TestSupport.Assert(Ccz64ConfigurationProbe.Inspect([]) is { RetreatQuotes: false, PersonDefaults: false, EquipmentPermissions: false, SpecialCriticalMap: false },
                "无PE时猜测配置地址");
        }
        finally { File.WriteAllBytes(path, original); }
    }

    private static void AssertBytes(string root, IReadOnlyDictionary<string, byte[]> expected)
    {
        foreach (var pair in expected)
            TestSupport.Assert(File.ReadAllBytes(ProjectFileLocator.Resolve(root, pair.Key)).SequenceEqual(pair.Value), "历史未逐字节恢复：" + pair.Key);
    }
}
