using System.Buffers.Binary;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;

namespace DataManager.Tests;

internal static class ExtendDataTests
{
    public static void Run(string basePath)
    {
        ProbeMappedConsumers();
        var root = TestSupport.CreateCoreCopy(basePath, restoreKnown65Snapshot: false);
        var sourceHashes = Directory.EnumerateFiles(basePath).Where(path => new[] { "Data.e5", "Ekd5.exe", "Imsg.e5", "Star.e5" }
            .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)).ToDictionary(path => path, HashService.Sha256);
        try
        {
            WriteFixtures(root);
            var workspace = new ProjectWorkspace(); workspace.Open(root, "ccz-6.5", "65-standard-v1");
            var service = new ExtendDataService(workspace);
            CheckReadIdentity(service);
            CheckAtomicity(service);
            CheckEffectEdits(service);
            CheckStrategies(service);
            CheckForge(service);
            CheckSaveHistory(service);
            CheckExternalModification(service);
            CheckMalformedFiles(root);
            Console.WriteLine("EXTEND_DATA_TESTS_OK");
        }
        finally
        {
            foreach (var (path, hash) in sourceHashes) TestSupport.Assert(HashService.Sha256(path) == hash, "拓展专项测试误写真实源文件");
            Directory.Delete(root, true);
        }
    }

    private static void WriteFixtures(string root)
    {
        var effects = Enumerable.Repeat((byte)255, 3 * 1024).ToArray();
        new byte[] { 3, 17, 199, 4 }.CopyTo(effects, 0);
        new byte[] { 5, 2, 201, 0 }.CopyTo(effects, 8);
        new byte[] { 7, 11, 188, 9 }.CopyTo(effects, 2 * 1024 + 255 * 4);
        File.WriteAllBytes(Path.Combine(root, "Effect.e5"), effects);
        var growth = new byte[256 * 8];
        new byte[] { 2, 30, 60, 5, 8, 2, 3, 9 }.CopyTo(growth, 0);
        growth[^1] = 91;
        File.WriteAllBytes(Path.Combine(root, "Effectmsg.e5"), growth);
        var strategies = Enumerable.Repeat((byte)255, 3 * 64).ToArray();
        new byte[] { 5, 4 }.CopyTo(strategies, 0);
        new byte[] { 7, 253 }.CopyTo(strategies, 6);
        new byte[] { 9, 33 }.CopyTo(strategies, 2 * 64 + 31 * 2);
        File.WriteAllBytes(Path.Combine(root, "MeffStudy.e5"), strategies);
        var forge = new byte[2 * 64];
        EncodingService.GameEncoding.GetBytes("配方实体").CopyTo(forge, 0);
        for (var slot = 0; slot < 8; slot++) BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(30 + slot * 3, 2), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(30, 2), 1); forge[32] = 130;
        BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(33, 2), 256); forge[35] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(forge.AsSpan(36, 2), 1); forge[38] = 200;
        BinaryPrimitives.WriteUInt32LittleEndian(forge.AsSpan(54, 4), uint.MaxValue);
        forge[58] = 2; forge[59] = 7; forge[60] = 2; forge[61] = 255; forge[62] = 137; forge[63] = 242;
        File.WriteAllBytes(Path.Combine(root, "Forge.e5"), forge);
        var items = new byte[256 * 25];
        EncodingService.GameEncoding.GetBytes("独立扩展物品").CopyTo(items, 0);
        File.WriteAllBytes(Path.Combine(root, "Item.e5"), items);
    }

    private static void CheckReadIdentity(ExtendDataService service)
    {
        TestSupport.Assert(service.GetStatus(ExtendDataCategory.Effects).RowCount == 3 &&
            service.GetStatus(ExtendDataCategory.Growth).RowCount == 256 &&
            service.GetStatus(ExtendDataCategory.Strategies).RowCount == 3 &&
            service.GetStatus(ExtendDataCategory.Forge).RowCount == 2, "四表没有按各自实际完整行数加载");
        var person = Convert.ToString(service.Workspace.GetDocument("persons").Data.Rows[0]["名称"]);
        TestSupport.Assert(service.GetEntityOptions(ExtendDataCategory.Effects)[0].Name == person &&
            service.GetEntityOptions(ExtendDataCategory.Strategies)[0].Name == person &&
            service.GetEntityOptions(ExtendDataCategory.Forge)[0].Name == "配方实体", "人物与配方列表仍混用名称");
        TestSupport.Assert(service.GetEntityOptions(ExtendDataCategory.Growth).Last().Id == 255 &&
            service.GetEffectOptions().Count == 256 && service.GetEffectOptions().Last().Name == "无", "特技空值或只读255记录缺失");
        var items = service.GetItemOptions();
        TestSupport.Assert(items.Single(item => item.Id == 256).Name == "独立扩展物品" &&
            items.Any(item => item.Id == 510) && !items.Any(item => item.Id == 511), "扩展物品列表没有按根目录25字节记录识别");
        TestSupport.Assert(service.ReadLong(ExtendDataCategory.Forge, 0, "价格") == uint.MaxValue &&
            service.GetRaw(ExtendDataCategory.Effects, 0).SequenceEqual(service.GetRaw(ExtendDataCategory.Effects, 0, false)) &&
            !service.Workspace.IsDirty, "读取目录/原始视图修改草稿或丢失UInt32价格");
        var noOp = service.Workspace.Save();
        TestSupport.Assert(noOp.Saved && noOp.ChangedFields == 0 && noOp.History == null && !service.Workspace.IsDirty &&
            service.GetRaw(ExtendDataCategory.Effects, 0).SequenceEqual(service.GetRaw(ExtendDataCategory.Effects, 0, false)),
            "原样保存重写或清洗原始空槽字节");
        var growth = service.GetGrowth(0);
        TestSupport.Assert(growth.Enabled && growth.InitialValue == 30 && growth.GrowthCap == 60 && growth.TemporaryTurns == 5 &&
            growth.BookExperience == 8 && growth.AttackExperience == 2 && growth.HitExperience == 3 && growth.KillExperience == 9,
            "成长字段语义错位");
        TestSupport.Assert(service.GetDiagnostics(ExtendDataCategory.Effects, 0).Any(line => line.Contains("下溢", StringComparison.Ordinal)) &&
            service.GetDiagnostics(ExtendDataCategory.Strategies, 0).Any(line => line.Contains("扣次", StringComparison.Ordinal)), "空洞或0回合诊断缺失");
        TestSupport.Assert(service.GetDiagnostics(ExtendDataCategory.Effects, 0).Any(line => line.Contains("第 2 槽（槽号 1）", StringComparison.Ordinal)) &&
            service.GetDiagnostics(ExtendDataCategory.Strategies, 0).Any(line => line.Contains("第 2 槽（槽号 1）", StringComparison.Ordinal)), "首空槽具体位置缺失或存在序号偏差");
    }

    private static void CheckAtomicity(ExtendDataService service)
    {
        var before = service.GetRaw(ExtendDataCategory.Forge, 0);
        Reject(() => service.ApplyEdits(ExtendDataCategory.Forge, 0, new Dictionary<string, object?> { ["名称"] = "不能写入", ["材料项数"] = 9 }));
        TestSupport.Assert(service.GetRaw(ExtendDataCategory.Forge, 0).SequenceEqual(before) && !service.Workspace.Commands.CanUndo,
            "非法材料项数造成部分草稿或撤销记录");
        Reject(() => service.ApplyEdits(ExtendDataCategory.Growth, 255, new Dictionary<string, object?> { ["Enabled"] = 1 }));
        service.ApplyEdits(ExtendDataCategory.Growth, 0, new Dictionary<string, object?> { ["Enabled"] = 2 });
        TestSupport.Assert(!service.Workspace.IsDirty && service.ReadInt(ExtendDataCategory.Growth, 0, "Enabled") == 2,
            "非标准启用字节被读取/原样提交规范为1");
    }

    private static void CheckEffectEdits(ExtendDataService service)
    {
        service.SetEffect(0, 0, 4, 18);
        TestSupport.Assert(service.GetEffects(0)[0] == new ExtendEffectSlot(0, 4, 18, 199, 4), "编辑已有特技重置经验/回合");
        service.Workspace.Commands.Undo();
        TestSupport.Assert(!service.Workspace.IsDirty, "已有特技修改不能整体撤销");
        service.SetEffect(0, 1, 6, 21);
        TestSupport.Assert(service.GetEffects(0)[1] == new ExtendEffectSlot(1, 6, 21, 0, 255), "新增特技默认值错误");
        service.ClearEffect(0, 1);
        TestSupport.Assert(service.GetEffects(0)[1] == new ExtendEffectSlot(1, 255, 0, 0, 255), "主动清空没有写规范四字节");
        service.Workspace.Commands.Undo(); service.Workspace.Commands.Undo();
        var before = service.GetRaw(ExtendDataCategory.Effects, 2);
        service.Compact(ExtendDataCategory.Effects, 2);
        TestSupport.Assert(service.GetEffects(2)[0] == new ExtendEffectSlot(0, 7, 11, 188, 9) &&
            service.GetEffects(2).Skip(1).All(slot => slot.IsEmpty && slot.Value == 0 && slot.Experience == 0 && slot.RemainingTurns == 255),
            "整理没有覆盖末槽或没有移动完整四字节");
        service.Workspace.Commands.Undo();
        TestSupport.Assert(service.GetRaw(ExtendDataCategory.Effects, 2).SequenceEqual(before), "整理撤销未恢复非标准空槽字节");
        service.Workspace.Commands.Redo();
    }

    private static void CheckStrategies(ExtendDataService service)
    {
        service.SetStrategy(0, 0, 6);
        TestSupport.Assert(service.GetStrategies(0)[0].UseCount == 4, "修改已学策略重置次数");
        service.Workspace.Commands.Undo();
        service.SetStrategy(0, 1, 8);
        TestSupport.Assert(service.GetStrategies(0)[1].UseCount == 255, "新增策略次数默认不是255");
        service.ClearStrategy(0, 1);
        TestSupport.Assert(service.GetStrategies(0)[1] == new ExtendStrategySlot(1, 255, 0), "清空策略未清次数");
        service.Workspace.Commands.Undo(); service.Workspace.Commands.Undo();
        service.Compact(ExtendDataCategory.Strategies, 2);
        TestSupport.Assert(service.GetStrategies(2)[0] == new ExtendStrategySlot(0, 9, 33) &&
            service.GetStrategies(2).Skip(1).All(slot => slot.StrategyId == 255 && slot.UseCount == 0), "策略末槽整理丢失次数或顺序");
    }

    private static void CheckForge(ExtendDataService service)
    {
        var slots = service.GetForgeSlots(0);
        TestSupport.Assert(slots[0].ExactLevel && slots[0].Amount == 2 && slots[1].IsConsumable && slots[1].Amount == 3 &&
            !slots[2].IsMaterial && slots[2].Amount == 200 && slots[7].IsEmpty && slots[7].ItemId == 65535,
            "材料与产物高位解释混用或UInt16空值被截断");
        TestSupport.Assert(new ExtendForgeSlot(0, 600, 201, true).Amount == 201,
            "未知UInt16材料ID的原始数量被强行按已知物品拆高位");
        var before = service.GetRaw(ExtendDataCategory.Forge, 0);
        Reject(() => service.ApplyEdits(ExtendDataCategory.Forge, 0, new Dictionary<string, object?> { ["名称"] = "不能写入", ["材料2数量"] = 130 }));
        TestSupport.Assert(service.GetRaw(ExtendDataCategory.Forge, 0).SequenceEqual(before), "消耗品非法高位造成部分写入");
        service.ApplyEdits(ExtendDataCategory.Forge, 0, new Dictionary<string, object?> { ["材料3数量"] = 201, ["商店编号"] = 8 });
        TestSupport.Assert(service.GetForgeSlots(0)[2].Amount == 201 && service.GetRaw(ExtendDataCategory.Forge, 0)[60] == 2 &&
            service.GetRaw(ExtendDataCategory.Forge, 0)[62] == 137 && service.GetRaw(ExtendDataCategory.Forge, 0)[63] == 242,
            "产物完整byte/启用原始字节/未知尾部未保留");
        var document = service.GetDocument(ExtendDataCategory.Forge);
        document.Data.Rows[0]["材料2数量"] = 130;
        Reject(() => ExtendDataService.ValidateForgeDraft(document, 0));
        document.Data.Rows[0]["材料2数量"] = 3;
        ExtendDataService.ValidateForgeDraft(document, 0);
    }

    private static void CheckExternalModification(ExtendDataService service)
    {
        var workspace = service.Workspace;
        workspace.Open(workspace.RootPath, "ccz-6.5", "65-standard-v1");
        service.SetEffect(0, 0, 4, 27);
        var file = service.GetDocument(ExtendDataCategory.Effects).FilePath;
        var external = File.ReadAllBytes(file); external[^1] ^= 1; File.WriteAllBytes(file, external);
        var saved = workspace.Save();
        TestSupport.Assert(!saved.Saved && File.ReadAllBytes(file).SequenceEqual(external), "外部修改冲突没有阻止拓展数据保存");
        workspace.Open(workspace.RootPath, "ccz-6.5", "65-standard-v1");
    }

    private static void CheckSaveHistory(ExtendDataService service)
    {
        var workspace = service.Workspace;
        var files = new[] { "Effect.e5", "Effectmsg.e5", "MeffStudy.e5", "Forge.e5" }
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(workspace.RootPath, name)));
        var expected = Enum.GetValues<ExtendDataCategory>().ToDictionary(category => category,
            category => Enumerable.Range(0, service.GetStatus(category).RowCount).Select(id => service.GetRaw(category, id)).ToArray());
        var saved = workspace.Save();
        TestSupport.Assert(saved.Saved, "拓展专项保存失败：" + string.Join("；", saved.Messages));
        var reopened = new ProjectWorkspace(); reopened.Open(workspace.RootPath, "ccz-6.5", "65-standard-v1");
        var reread = new ExtendDataService(reopened);
        foreach (var (category, rows) in expected)
            for (var id = 0; id < rows.Length; id++) TestSupport.Assert(reread.GetRaw(category, id).SequenceEqual(rows[id]), "保存重开拓展字节不一致");
        TestSupport.Assert(reopened.UndoSaved().Success && files.All(pair => File.ReadAllBytes(Path.Combine(workspace.RootPath, pair.Key)).SequenceEqual(pair.Value)),
            "跨拓展文件已保存撤销未恢复全部原始字节");
        TestSupport.Assert(reopened.RedoSaved().Success, "跨拓展文件已保存重做失败");
    }

    private static void CheckMalformedFiles(string root)
    {
        File.WriteAllBytes(Path.Combine(root, "Forge.e5"), new byte[65]);
        var workspace = new ProjectWorkspace(); workspace.Open(root, "ccz-6.5", "65-standard-v1");
        var service = new ExtendDataService(workspace);
        TestSupport.Assert(!service.GetStatus(ExtendDataCategory.Forge).Available &&
            service.GetStatus(ExtendDataCategory.Forge).Message.Contains("无法读取", StringComparison.Ordinal), "截断文件被误当完整短表");
        File.Delete(Path.Combine(root, "Forge.e5"));
        workspace.Open(root, "ccz-6.5", "65-standard-v1");
        TestSupport.Assert(!service.GetStatus(ExtendDataCategory.Forge).Available &&
            service.GetStatus(ExtendDataCategory.Forge).Message.Contains("未提供", StringComparison.Ordinal), "缺失与损坏文件诊断未区分");
    }

    private static void ProbeMappedConsumers()
    {
        var bytes = SyntheticPe();
        var probe = new EffectNamePoolProbe();
        var good = probe.Inspect(bytes);
        TestSupport.Assert(good.Success && good.FileOffset == 0x1000 && good.Names[0] == "真实名称" && good.Names[1] == string.Empty,
            "双消费者名称定位未按PE RVA映射或拒绝空名称");
        var corrupt = bytes.ToArray(); corrupt[0x1000] = 0x81; corrupt[0x1001] = 0;
        TestSupport.Assert(!probe.Inspect(corrupt).Success, "严格GBK名称校验漏过损坏名称");
        var noNull = bytes.ToArray(); noNull.AsSpan(0x1000, 16).Fill((byte)'A');
        TestSupport.Assert(!probe.Inspect(noNull).Success, "无结束符名称槽被接受");
        var missing = bytes.ToArray(); missing[0x300] = 0;
        TestSupport.Assert(!probe.Inspect(missing).Success, "仅单一名称消费者被信任");
        var conflicting = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(conflicting.AsSpan(0x308, 4), 0x502010);
        TestSupport.Assert(!probe.Inspect(conflicting).Success, "两消费者冲突地址未拒绝");
        var overlay = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(overlay.AsSpan(0x180, 4), 0x1000);
        TestSupport.Assert(!probe.Inspect(overlay).Success, "名称池越过映射范围仍从overlay读取");
        var duplicate = bytes.ToArray(); bytes.AsSpan(0x200, 28).CopyTo(duplicate.AsSpan(0x400));
        TestSupport.Assert(!probe.Inspect(duplicate).Success, "重复消费者被误当唯一来源");
        var nonExecutable = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(nonExecutable.AsSpan(0x19C, 4), 0x40000040);
        TestSupport.Assert(!probe.Inspect(nonExecutable).Success, "数据节签名被误当可执行消费者");
    }

    private static byte[] SyntheticPe()
    {
        var bytes = new byte[0x3000]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80, 4), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94, 2), 0xE0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98, 2), 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xB4, 4), 0x500000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x180, 4), 0x2800); // VirtualSize.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x184, 4), 0x1000); // RVA.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x188, 4), 0x2800); // RawSize.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18C, 4), 0x200); // RawStart.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x19C, 4), 0x60000020);
        var first = Convert.FromHexString("558BEC83EC408B4508C1E00405001E500052508D45C050E800000000");
        first.CopyTo(bytes, 0x200);
        var second = Convert.FromHexString("0FB640076BC01005001E5000508D55D052E800000000");
        second.CopyTo(bytes, 0x300);
        EncodingService.GameEncoding.GetBytes("真实名称").CopyTo(bytes, 0x1000);
        return bytes;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException("非法拓展编辑没有被拒绝。");
    }
}
