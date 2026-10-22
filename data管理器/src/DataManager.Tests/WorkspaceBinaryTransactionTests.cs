using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class WorkspaceBinaryTransactionTests
{
    public static void Run(string basePath)
    {
        TestBinaryCommitAndRollback(basePath);
        TestLegacyPoolCapacityAndSuffix(basePath);
        TestFixedPoolSentinelsAndHistory(basePath);
        TestAccessoryGroupWorkspaceWrite(basePath);
    }

    private static void TestBinaryCommitAndRollback(string basePath)
    {
        WithProject(basePath, root =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var path = Path.Combine(root, "Ekd5.exe");
            var before = File.ReadAllBytes(path);
            var after = before.ToArray();
            after[^1] ^= 1;
            var candidate = new WorkspaceBinaryCandidate(path, before, after, "事务测试");
            var transaction = new WorkspaceBinaryTransaction();
            var result = transaction.Commit(workspace, "强制语义复读失败", [candidate], () => throw new InvalidDataException("test semantic failure"));
            TestSupport.Assert(!result.Saved && File.ReadAllBytes(path).SequenceEqual(before), "二进制语义复读失败未回滚");
            TestSupport.Assert(workspace.GetHistory().Last().Status == HistoryStatus.Failed, "二进制失败历史状态错误");
            result = transaction.Commit(workspace, "测试专用数据保存", [candidate]);
            TestSupport.Assert(result.Saved && workspace.GetHistory().Last().Status == HistoryStatus.Active, "二进制事务未写入可撤回历史");
            TestSupport.Assert(workspace.Documents.Values.Where(document => Path.GetFileName(document.FilePath) == "Ekd5.exe")
                .All(document => document.OriginalFileHash == HashService.Sha256(after)), "二进制保存后工作区未刷新 SHA 基线");
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(path).SequenceEqual(before), "专用二进制历史撤回失败");
            TestSupport.Assert(workspace.RedoSaved().Success && File.ReadAllBytes(path).SequenceEqual(after), "专用二进制历史重做失败");

            var person = workspace.GetDocument("persons");
            var original = Convert.ToInt32(person.Data.Rows[0]["武力"]);
            person.Data.Rows[0]["武力"] = original + 1;
            result = transaction.Commit(workspace, "草稿冲突", [new WorkspaceBinaryCandidate(path, after, before, "事务测试")]);
            TestSupport.Assert(!result.Saved && workspace.IsDirty && Convert.ToInt32(person.Data.Rows[0]["武力"]) == original + 1,
                "专用事务丢弃了未保存页面草稿");
            TestSupport.Assert(File.ReadAllBytes(path).SequenceEqual(after), "草稿冲突仍写入了二进制数据");
            person.Data.Rows[0]["武力"] = original;

            var external = after.ToArray();
            external[^2] ^= 1;
            File.WriteAllBytes(path, external);
            result = transaction.Commit(workspace, "外改冲突", [new WorkspaceBinaryCandidate(path, external, before, "事务测试")]);
            TestSupport.Assert(!result.Saved && File.ReadAllBytes(path).SequenceEqual(external), "专用事务覆盖了工具外修改");
        });
    }

    private static void TestLegacyPoolCapacityAndSuffix(string basePath)
    {
        WithProject(basePath, root =>
        {
            var path = Path.Combine(root, "Ekd5.exe");
            var bytes = File.ReadAllBytes(path);
            const int offset = 567784;
            Array.Clear(bytes, offset, 885);
            bytes[offset] = (byte)'A';
            for (var index = 1; index < 62; index++) bytes[offset + 20 + (index - 1) * 2] = (byte)'A';
            var suffix = new byte[] { 201, 202, 203, 204 };
            suffix.CopyTo(bytes, offset + 600);
            File.WriteAllBytes(path, bytes);
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var service = new EffectNamePoolService();
            var result = service.WriteItemName(workspace, 0x1A, "LongName");
            TestSupport.Assert(result.ChangedBytes > 0 && service.Read(workspace, EffectNameChannel.Item).First().Name == "LongName", "有零空闲容量的变长名称扩展失败");
            TestSupport.Assert(File.ReadAllBytes(path).AsSpan(offset + 600, suffix.Length).SequenceEqual(suffix), "变长名称重建移动或清空了非零保留后缀");
            TestSupport.Assert(service.Read(workspace, EffectNameChannel.Item).Take(62).Skip(1).All(slot => slot.Name == "A"), "变长名称重建破坏了其他已知名称槽");
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(path).SequenceEqual(bytes), "变长名称写入未进入工作区撤回历史");
        });
    }

    private static void TestFixedPoolSentinelsAndHistory(string basePath)
    {
        WithProject(basePath, root =>
        {
            var path = Path.Combine(root, "Ekd5.exe");
            var bytes = File.ReadAllBytes(path);
            var poolOffset = checked((int)EffectNamePoolService.ItemFixedOffset66);
            Array.Clear(bytes, poolOffset, 230 * EffectNamePoolService.FixedSlotSize);
            foreach (var (slot, name) in new[] { (0, "恢复HP和状态"), (28, "引导攻击"), (56, "回复周围HP状态"), (72, "策略连击") })
                EncodingService.EncodeFixedString(name, 16).CopyTo(bytes, poolOffset + slot * 16);
            File.WriteAllBytes(path, bytes);
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.6", "66-standard-v1");
            var service = new EffectNamePoolService();
            foreach (var slot in new[] { 0, 28, 56, 72 }) service.WriteItemName(workspace, 0x1A + slot, "自定义");
            // None of the canonical names remain, and the sparse fixture cannot pass
            // structural fallback. The checked saved snapshot must keep it writable.
            service.WriteItemName(workspace, 0x1A + 1, "测试");
            TestSupport.Assert(service.Read(workspace, EffectNameChannel.Item)[1].Name == "测试", "自定义哨兵名称后名称池被永久锁定");
            TestSupport.Assert(workspace.UndoSaved().Success, "6.6 名称池专用写入不可撤回");
        });
    }

    private static void TestAccessoryGroupWorkspaceWrite(string basePath)
    {
        WithProject(basePath, root =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var path = Path.Combine(root, "Ekd5.exe");
            var before = File.ReadAllBytes(path);
            var service = new AccessoryJobGroupService();
            var reread = service.Write(workspace, [[0, 1], [2, 3]]);
            TestSupport.Assert(reread.Groups.Count == 2 && reread.Groups[0].JobSeriesIds.SequenceEqual([0, 1]), "工作区辅助分组写后复读失败");
            TestSupport.Assert(workspace.GetHistory().Last().OperationKind == "专用二进制保存", "辅助分组绕过了工作区历史");
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(path).SequenceEqual(before), "辅助分组专用写入不可撤回");
        });
    }

    private static void WithProject(string basePath, Action<string> test)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try { test(root); }
        finally { Directory.Delete(root, true); }
    }
}
