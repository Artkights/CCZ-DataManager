using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class StrategyEffectClassTests
{
    public static void Run(string basePath)
    {
        var repository = TestSupport.RepositoryRootFromBase(basePath);
        var fixtures = new[]
        {
            (Name: "洗隋", Profile: "ccz-6.4", Variant: "64-xisui-v1", Maximum: 0x24),
            (Name: "刘备6.4模组 - 副本", Profile: "ccz-6.4", Variant: "64-xisui-v1", Maximum: 0x24),
            (Name: "三国之召唤猛将6.4（60关版）基底", Profile: "ccz-6.4", Variant: "64-xisui-v1", Maximum: 0x24),
            (Name: "加强版6.5未加密版", Profile: "ccz-6.5", Variant: "65-standard-v1", Maximum: 0x24),
            (Name: "东吴霸王传6.5", Profile: "ccz-6.5", Variant: "65-standard-v1", Maximum: 0x24),
            (Name: "重生之氪金桓王传", Profile: "ccz-6.5", Variant: "65-standard-v1", Maximum: 0x24),
            (Name: "复刻版6.6山贼", Profile: "ccz-6.6", Variant: "66-standard-v1", Maximum: 0x29)
        };
        foreach (var fixture in fixtures)
        {
            var source = Path.Combine(repository, "基底", fixture.Name);
            var originalHashes = Known65FixtureSnapshot.CoreNames.ToDictionary(name => name,
                name => HashService.Sha256(ProjectFileLocator.Resolve(source, name)));
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            try
            {
                var workspace = Open(root, fixture.Profile, fixture.Variant);
                AssertLayoutAndOptions(workspace, fixture.Maximum);
                SingleByteSaveAndHistory(workspace);
                CsvNamesAndPreviousLayout(workspace);
                LegacyHistory(workspace);
                ReservedValues(root, fixture.Profile, fixture.Variant);
                ExternalConflict(root, fixture.Profile, fixture.Variant);
                DamagedConsumer(root, fixture.Profile, fixture.Variant);
            }
            finally { Directory.Delete(root, true); }
            foreach (var file in originalHashes)
                TestSupport.Assert(HashService.Sha256(ProjectFileLocator.Resolve(source, file.Key)) == file.Value,
                    $"策略效果回归改写了原件：{fixture.Name}/{file.Key}");
            Console.WriteLine($"STRATEGY_EFFECT_CLASS_FIXTURE_OK {fixture.Name} max={fixture.Maximum} single-byte CSV history reserved conflict consumer");
        }
        TestSupport.Assert(!StrategyEffectClassProbe.Inspect(new byte[0xB0000]).Verified,
            "没有PE与消费者的文件被当作有效效果类表。");
        Console.WriteLine("STRATEGY_EFFECT_CLASS_TESTS_OK seven fixtures originals-unchanged");
    }

    internal static string PreviousLayoutRevision(ProjectWorkspace workspace)
    {
        var apply = typeof(ProfileAugmentationService).GetMethod("ApplyPreStrategyEffects", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("缺少精确的上一版策略效果布局复现入口。");
        var profile = new ProfileRegistry().Resolve(workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId);
        return ((ResolvedProfile)apply.Invoke(new ProfileAugmentationService(), [workspace.RootPath, profile])!).LayoutRevision;
    }

    private static ProjectWorkspace Open(string root, string profile, string variant)
    {
        var workspace = new ProjectWorkspace(); workspace.Open(root, profile, variant); return workspace;
    }

    private static void AssertLayoutAndOptions(ProjectWorkspace workspace, int maximum)
    {
        var document = workspace.GetDocument(StrategyEffectClassService.TableKey);
        var layout = StrategyEffectClassProbe.Inspect(document.OriginalFileBytes);
        TestSupport.Assert(layout.Verified && layout.MaximumClass == maximum && layout.Offset == 0xA2D70,
            "当前引擎的效果类分派/表地址或内置类边界没有独立验证。");
        TestSupport.Assert(document.Definition is { Capability: TableCapability.Editable, RowCount: 144, RowSize: 1, ParentKey: "strategies" } &&
            document.Definition.Fields.Single() is { Name: StrategyEffectClassService.FieldName, Offset: 0, Size: 1, Kind: FieldKind.UInt8 },
            "效果类表不是按策略ID索引的单字节物理表。");
        var classification = workspace.GetDocument("strategy-learning");
        TestSupport.Assert(classification.Definition.DataOffset == 0xA2FB0 && classification.Definition.RowCount == 144 &&
            CsvDisplayNameRegistry.GetTableDisplayName(classification.Definition) == "策略类型索引",
            "策略类型索引改名改变了Mg8物理身份或仍显示旧名。");
        var strategies = workspace.GetDocument("strategies");
        TestSupport.Assert(strategies.Definition.Fields.Single(field => field.Name == "施展对象").Maximum == 2,
            "施展对象仍允许未验证的 5/6。");
        TestSupport.Assert(workspace.GetDocument("strategy-battle-ai").Definition.Fields.Single().Maximum == 0x0F &&
            workspace.GetDocument("strategy-training-ai").Definition.Fields.Single().Maximum == 0x0F &&
            workspace.GetDocument("strategy-damage-type").Definition.Fields.Single().Maximum == 4,
            "策略 AI/伤害类型上限未按引擎语义收紧。");
        var pageKeys = new BusinessDataService(workspace).GetAvailableTableKeys(BusinessPageKind.Strategies);
        TestSupport.Assert(pageKeys.Contains("strategy-extension") == (workspace.ActiveProfile.ProfileId == "ccz-6.6"),
            "6.4/6.5 填充型策略扩展表仍出现在策略页，或 6.6 被误删。");
        var options = StrategyEffectClassService.GetOptions(workspace, 0);
        TestSupport.Assert(Enumerable.Range(0, maximum + 1).All(id => options.Any(option => option.Id == id)) &&
            options.All(option => option.Id <= maximum || option.Id == Value(document, 0)) &&
            options.All(option => !string.IsNullOrWhiteSpace(option.Name)),
            "内置效果下拉缺项、出现未安装类或缺少名称。");
        TestSupport.Assert(StrategyEffectClassService.GetCompatibilityWarning(workspace, 0, 9) != null,
            "伤害策略跨族改成中毒没有兼容性提示。");
    }

    private static void SingleByteSaveAndHistory(ProjectWorkspace workspace)
    {
        var before = Snapshot(workspace.RootPath);
        var document = workspace.GetDocument(StrategyEffectClassService.TableKey);
        var original = Value(document, 0);
        var next = original == 9 ? 8 : 9;
        var revision = workspace.ActiveProfile.LayoutRevision;
        new BusinessDataService(workspace).BeginTransaction(BusinessPageKind.Strategies, "策略效果单字节回归")
            .Set(document.Definition.Key, 0, StrategyEffectClassService.FieldName, next).Apply();
        AssertSnapshot(workspace.RootPath, before, "选择效果下拉立即写入了磁盘。");
        workspace.Commands.Undo(); TestSupport.Assert(Value(document, 0) == original && !workspace.IsDirty, "效果类草稿无法撤回。");
        workspace.Commands.Redo(); TestSupport.Assert(Value(document, 0) == next && workspace.IsDirty, "效果类草稿无法重做。");
        var saved = workspace.Save();
        TestSupport.Assert(saved.Saved && saved.ChangedBytes == 1, string.Join("；", saved.Messages));
        foreach (var file in before)
        {
            var after = File.ReadAllBytes(ProjectFileLocator.Resolve(workspace.RootPath, file.Key));
            var changed = Enumerable.Range(0, after.Length).Where(index => after[index] != file.Value[index]).ToArray();
            TestSupport.Assert(after.Length == file.Value.Length && (file.Key == "Ekd5.exe"
                ? changed.SequenceEqual([checked((int)document.Definition.DataOffset)]) : changed.Length == 0),
                "效果类保存改写了指定单字节以外的文件或区域：" + file.Key);
        }
        var reopened = Open(workspace.RootPath, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId);
        TestSupport.Assert(Value(reopened.GetDocument(document.Definition.Key), 0) == next && reopened.ActiveProfile.LayoutRevision == revision,
            "修改效果类数据后重开丢失可写能力或改变了布局身份。");
        TestSupport.Assert(reopened.UndoSaved(saved.History!.OperationId).Success, "效果类已保存历史无法撤回。");
        AssertSnapshot(workspace.RootPath, before, "效果类历史没有精确恢复原字节。");
        TestSupport.Assert(reopened.RedoSaved(saved.History.OperationId).Success && reopened.UndoSaved(saved.History.OperationId).Success,
            "效果类已保存历史无法重做再撤回。");
        // Bring the original instance back to the exact disk baseline for the following cases.
        workspace.Open(workspace.RootPath, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId);
    }

    private static void CsvNamesAndPreviousLayout(ProjectWorkspace workspace)
    {
        var business = new BusinessDataService(workspace);
        var definitions = workspace.Documents.Values.Select(document => document.Definition).ToArray();
        foreach (var legacy in new[] { "策略效果索引.csv", "效果索引.csv", "strategy-learning" })
            TestSupport.Assert(CsvDisplayNameRegistry.ResolveTableKey(legacy, definitions) == "strategy-learning", "旧效果索引CSV指向了新效果类表。");
        TestSupport.Assert(CsvDisplayNameRegistry.ResolveTableKey("策略类型索引.csv", definitions) == "strategy-learning" &&
            CsvDisplayNameRegistry.ResolveTableKey("策略效果类.csv", definitions) == StrategyEffectClassService.TableKey,
            "新旧索引CSV没有独立且唯一的名称。");
        foreach (var (legacyName, key) in new[]
                 {
                     ("策略扩展标记.csv", "strategy-extension"),
                     ("策略伤害系数.csv", "strategy-damage-ratio"),
                     ("策略命中率.csv", "strategy-hit-rate"),
                     ("策略战场智能限制.csv", "strategy-battle-ai"),
                     ("策略练武场智能限制.csv", "strategy-training-ai")
                 })
            if (definitions.Any(definition => definition.Key == key))
                TestSupport.Assert(CsvDisplayNameRegistry.ResolveTableKey(legacyName, definitions) == key,
                    "旧策略 CSV 名未映射到新语义表：" + legacyName);
        var directory = Path.Combine(workspace.RootPath, "策略交换");
        var exported = business.ExportPageCsv(BusinessPageKind.Strategies, directory);
        TestSupport.Assert(exported.Any(file => file.TableKey == "strategy-learning" && Path.GetFileName(file.Path) == "策略类型索引.csv") &&
            exported.Any(file => file.TableKey == StrategyEffectClassService.TableKey && Path.GetFileName(file.Path) == "策略效果类.csv") &&
            exported.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == exported.Count,
            "导出CSV文件名错误或两个索引互相覆盖。");
        var previous = PreviousLayoutRevision(workspace);
        var revisions = LayoutRevisionCompatibility.GetLegacyRevisions(workspace.RootPath, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId);
        TestSupport.Assert(previous != workspace.ActiveProfile.LayoutRevision && revisions.Contains(previous) &&
            LayoutRevisionCompatibility.IsCompatible(workspace.RootPath, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId,
                previous, workspace.ActiveProfile.LayoutRevision) &&
            !LayoutRevisionCompatibility.IsCompatible(workspace.RootPath, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId,
                "DEADBEEF", workspace.ActiveProfile.LayoutRevision), "新表升级没有严格接受精确旧布局并拒绝未知布局。");
        var classification = workspace.GetDocument("strategy-learning");
        var old = Value(classification, 0, "内容"); var next = old ^ 1;
        var effectBefore = Value(workspace.GetDocument(StrategyEffectClassService.TableKey), 0);
        var oldPath = Path.Combine(directory, "策略效果索引.csv");
        CsvService.WriteRecords(oldPath, [
            new[] { "#DataManagerCSV", "1", workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId, "strategy-learning", previous },
            new[] { "ID", "内容" }, new[] { "0", next.ToString(CultureInfo.InvariantCulture) }]);
        TestSupport.Assert(business.ImportPageCsvAtomic(BusinessPageKind.Strategies,
            new Dictionary<string, string> { ["策略效果索引.csv"] = oldPath }).ChangedCells == 1 &&
            Value(classification, 0, "内容") == next && Value(workspace.GetDocument(StrategyEffectClassService.TableKey), 0) == effectBefore,
            "上一版效果索引CSV不能回导类型索引或串写了效果类。");
        workspace.Commands.Undo();
        TestSupport.Assert(!workspace.IsDirty, "撤回旧CSV后仍残留草稿。");
    }

    private static void LegacyHistory(ProjectWorkspace workspace)
    {
        var person = workspace.GetDocument("persons"); var old = Value(person, 0, "武力");
        workspace.ApplyCompositeTransaction("上一版人物字段历史", [new("persons", 0, "武力", (old + 1) % 200)]);
        var saved = workspace.Save(); TestSupport.Assert(saved.Saved, "旧布局历史准备失败。");
        var store = new HistoryStore(); var history = store.LoadOperation(workspace.RootPath, saved.History!.OperationId);
        history.LayoutRevision = PreviousLayoutRevision(workspace); store.Update(workspace.RootPath, history);
        var reopened = Open(workspace.RootPath, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId);
        TestSupport.Assert(reopened.UndoSaved(history.OperationId).Success && Value(reopened.GetDocument("persons"), 0, "武力") == old,
            "升级后不能撤回精确上一版布局的既有字段历史。");
        TestSupport.Assert(reopened.RedoSaved(history.OperationId).Success && reopened.UndoSaved(history.OperationId).Success,
            "上一版布局历史不能重做再撤回。");
    }

    private static void ReservedValues(string root, string profile, string variant)
    {
        var path = ProjectFileLocator.Resolve(root, "Ekd5.exe"); var original = File.ReadAllBytes(path);
        try
        {
            var seeded = original.ToArray(); seeded[0xA2D70 + 78] = 0x90; File.WriteAllBytes(path, seeded);
            var workspace = Open(root, profile, variant); var document = workspace.GetDocument(StrategyEffectClassService.TableKey);
            TestSupport.Assert(StrategyEffectClassService.GetOptions(workspace, 78).Any(option => option.Id == 0x90) &&
                StrategyEffectClassService.GetOptions(workspace, 0).All(option => option.Id != 0x90),
                "保留原值90应可显示，但不能成为其他策略可新选的内置类。");
            var csv = Path.Combine(root, "reserved-effect.csv");
            CsvService.WriteRecords(csv, [new[] { "ID", StrategyEffectClassService.FieldName }, new[] { "78", "144" }]);
            TestSupport.Assert(workspace.ImportCsv(document, csv).ChangedCells == 0 && workspace.Save().Saved &&
                File.ReadAllBytes(path).SequenceEqual(seeded), "原值90不能无损导回或无损保存。");
            foreach (var invalid in new[] { 0x2A, 0x2F, 0x30, 0x90, 0xFF })
            {
                ExpectFailure(() => workspace.ApplyCompositeTransaction("非法效果", [new(document.Definition.Key, 0, StrategyEffectClassService.FieldName, invalid)]),
                    "普通草稿接受了未安装或非法效果类。");
                ExpectFailure(() => workspace.ApplyBatch(document, [0], StrategyEffectClassService.FieldName, "统一赋值", invalid.ToString(CultureInfo.InvariantCulture)),
                    "批量修改绕过了效果类范围。");
                CsvService.WriteRecords(csv, [new[] { "ID", StrategyEffectClassService.FieldName }, new[] { "0", invalid.ToString(CultureInfo.InvariantCulture) }]);
                TestSupport.Assert(workspace.ImportCsv(document, csv).ChangedCells == 1 &&
                    Convert.ToInt32(document.Data.Rows[0][StrategyEffectClassService.FieldName]) == invalid,
                    "物理字节合法的 CSV 仍受已安装效果类范围限制。");
                workspace.Commands.Undo();
                TestSupport.Assert(!workspace.IsDirty, "自定义效果类 CSV 撤回留下草稿。");
            }
            document.Data.Rows[0][StrategyEffectClassService.FieldName] = 0x31;
            TestSupport.Assert(!workspace.Save().Saved && File.ReadAllBytes(path).SequenceEqual(seeded),
                "直接修改DataTable绕过了保存时效果类校验。");
        }
        finally { File.WriteAllBytes(path, original); }
    }

    private static void ExternalConflict(string root, string profile, string variant)
    {
        var workspace = Open(root, profile, variant); var document = workspace.GetDocument(StrategyEffectClassService.TableKey);
        var path = document.FilePath; var before = File.ReadAllBytes(path);
        try
        {
            workspace.ApplyCompositeTransaction("外改冲突", [new(document.Definition.Key, 0, StrategyEffectClassService.FieldName, 9)]);
            var external = before.ToArray(); external[^1] ^= 1; File.WriteAllBytes(path, external);
            TestSupport.Assert(!workspace.Save().Saved && File.ReadAllBytes(path).SequenceEqual(external) && workspace.IsDirty,
                "引擎外改后保存覆盖了外部字节或清除了用户草稿。");
        }
        finally { File.WriteAllBytes(path, before); }
    }

    private static void DamagedConsumer(string root, string profile, string variant)
    {
        var path = ProjectFileLocator.Resolve(root, "Ekd5.exe"); var before = File.ReadAllBytes(path);
        var layout = StrategyEffectClassProbe.Inspect(before);
        try
        {
            var damaged = before.ToArray(); damaged[MapAddress(damaged, layout.DispatchAddress) + 8] = 0x90;
            File.WriteAllBytes(path, damaged);
            TestSupport.Assert(!StrategyEffectClassProbe.Inspect(damaged).Verified, "策略效果消费者损坏仍通过探测。");
            var workspace = Open(root, profile, variant);
            TestSupport.Assert(!workspace.Documents.ContainsKey(StrategyEffectClassService.TableKey) && workspace.Documents.ContainsKey("strategies"),
                "消费者损坏仍开放效果表或阻断了普通策略数据。");
        }
        finally { File.WriteAllBytes(path, before); }
    }

    private static int MapAddress(byte[] image, uint address)
    {
        var pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
        var sections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20, 2));
        var imageBase = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe + 52, 4));
        var rva = address - imageBase;
        for (var index = 0; index < sections; index++)
        {
            var section = pe + 24 + optionalSize + index * 40;
            var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(section + 12, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(section + 16, 4));
            if (rva >= virtualAddress && rva - virtualAddress < size)
                return checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(section + 20, 4)) + rva - virtualAddress));
        }
        throw new InvalidDataException("测试分派地址没有映射到PE文件。");
    }

    private static int Value(TableDocument document, int row, string field = StrategyEffectClassService.FieldName)
        => Convert.ToInt32(document.Data.Rows[row][field], CultureInfo.InvariantCulture);
    private static Dictionary<string, byte[]> Snapshot(string root)
        => Known65FixtureSnapshot.CoreNames.ToDictionary(name => name, name => File.ReadAllBytes(ProjectFileLocator.Resolve(root, name)));
    private static void AssertSnapshot(string root, IReadOnlyDictionary<string, byte[]> expected, string message)
    {
        foreach (var file in expected) TestSupport.Assert(File.ReadAllBytes(ProjectFileLocator.Resolve(root, file.Key)).SequenceEqual(file.Value), message);
    }
    private static void ExpectFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
