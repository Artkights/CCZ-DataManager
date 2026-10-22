using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class ResourceTransactionTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManagerTransaction_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StalePlansAndPayloads(root);
            JournalFailures(root);
            RecoveryContinuesAfterLockedFile(root);
            NestedRenameHistory(root);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static void StalePlansAndPayloads(string root)
    {
        var folder = Path.Combine(root, "stale");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "Face.e5");
        var input = Path.Combine(folder, "source.png");
        File.WriteAllBytes(input, Png(Color.Green));
        var original = Archive(Png(Color.Red));
        File.WriteAllBytes(target, original);
        var profile = Profile(folder);
        var history = new HistoryService(Path.Combine(root, "history-stale"));
        var service = new ImageMutationService(history);
        var resource = new ImageResourceDescriptor("Logo.e5", "背景图", ImageCategory.Interface,
            ResourceKind.E5Indexed, [target], 1, true, true, false, "", "");
        var plan = service.PlanAppend(profile, resource, [input]);
        var external = Archive(Png(Color.Blue));
        File.WriteAllBytes(target, external);
        var rejected = service.Commit(profile, plan);
        Assert(!rejected.Success, "候选生成后的外部改动没有被拒绝");
        Equal(external, File.ReadAllBytes(target), "过期候选覆盖了外部改动");
        Assert(history.List(profile).Count == 0, "过期候选写入了活动历史");

        // Preserve timestamp and length: mutation builders must bypass the preview cache.
        // File imports now need a verified target template. Use equal-size native BMPs
        // so this still exercises a content change hidden by length and timestamp.
        var firstPayload = Bmp(Color.Red);
        var secondPayload = Bmp(Color.Blue);
        var a = Archive(firstPayload);
        var b = Archive(secondPayload);
        Assert(a.Length == b.Length, "缓存过期夹具未保持同等长度");
        File.WriteAllBytes(target, a);
        _ = E5Archive.Open(target);
        var timestamp = File.GetLastWriteTimeUtc(target);
        File.WriteAllBytes(target, b);
        File.SetLastWriteTimeUtc(target, timestamp);
        var freshPlan = service.PlanAppend(profile, resource, [input]);
        var committed = service.Commit(profile, freshPlan);
        Assert(committed.Success, string.Join("；", committed.Diagnostics));
        Equal(secondPayload, E5Archive.OpenFresh(target).ReadDecoded(1), "复用了内容已经过期的预览缓存");

        File.WriteAllBytes(input, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var before = File.ReadAllBytes(target);
        var invalidBlocked = false;
        try { _ = service.PlanAppend(profile, resource, [input]); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { invalidBlocked = true; }
        Assert(invalidBlocked, "损坏 PNG 仅凭文件头通过了普通 E5 新增");
        Equal(before, File.ReadAllBytes(target), "新增素材校验失败改动了目标");

        var created = Path.Combine(folder, "created.e5");
        var creation = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Append, "创建",
            [new MutationFile(created, original, "创建", AllowCreate: true)], [], false);
        File.WriteAllBytes(created, external);
        Assert(!service.Commit(profile, creation).Success, "预定新建路径已存在时仍然覆盖");
        Equal(external, File.ReadAllBytes(created), "新建路径冲突覆盖了外部文件");
    }

    private static void JournalFailures(string root)
    {
        var folder = Path.Combine(root, "journal");
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "one.e5");
        var second = Path.Combine(folder, "two.e5");
        var original1 = Archive([1, 2, 3]);
        var original2 = Archive([4, 5, 6]);
        File.WriteAllBytes(first, original1);
        File.WriteAllBytes(second, original2);
        File.SetAttributes(first, File.GetAttributes(first) | FileAttributes.ReadOnly);
        var profile = Profile(folder);
        var history = new HistoryService(Path.Combine(root, "history-journal"));
        var service = new ImageMutationService(history);
        MutationPlan Plan() => new(Guid.NewGuid().ToString("N"), MutationKind.Replace, "双文件",
            [new(first, Archive([8, 8]), "one"), new(second, Archive([9, 9]), "two")], [], false);
        var journal = Path.Combine(history.BackupRoot(profile), "History", "history.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "");
        using (var locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = service.Commit(profile, Plan());
            Assert(!result.Success, "历史被锁时仍返回资源保存成功");
            Equal(original1, File.ReadAllBytes(first), "历史失败后第一个文件未恢复");
            Equal(original2, File.ReadAllBytes(second), "历史失败后第二个文件未恢复");
            Assert((File.GetAttributes(first) & FileAttributes.ReadOnly) != 0, "失败恢复丢失只读属性");
        }
        Assert(history.List(profile).Count == 0, "失败的资源事务留下了活动历史");
        var success = service.Commit(profile, Plan());
        Assert(success.Success, string.Join("；", success.Diagnostics));
        var after1 = File.ReadAllBytes(first);
        var after2 = File.ReadAllBytes(second);
        var historyBytes = File.ReadAllBytes(journal);
        using (var locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var undo = history.Undo(profile, success.HistoryId!);
            Assert(!undo.Success, "撤回的历史无法持久化时仍返回成功");
            Equal(after1, File.ReadAllBytes(first), "撤回失败后第一个文件未恢复写后状态");
            Equal(after2, File.ReadAllBytes(second), "撤回失败后第二个文件未恢复写后状态");
        }
        Equal(historyBytes, File.ReadAllBytes(journal), "撤回失败改动了历史状态");
        Assert(history.Undo(profile, success.HistoryId!).Success, "正常撤回失败");
        var undoneJournal = File.ReadAllBytes(journal);
        using (var locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var redo = history.Redo(profile, success.HistoryId!);
            Assert(!redo.Success, "前进的历史无法持久化时仍返回成功");
            Equal(original1, File.ReadAllBytes(first), "前进失败后第一个文件未恢复");
            Equal(original2, File.ReadAllBytes(second), "前进失败后第二个文件未恢复");
        }
        Equal(undoneJournal, File.ReadAllBytes(journal), "前进失败改动了历史状态");
        Assert(history.Redo(profile, success.HistoryId!).Success, "正常前进失败");
        Assert((File.GetAttributes(first) & FileAttributes.ReadOnly) != 0, "成功保存/撤回/前进丢失只读属性");
        Assert(!Directory.EnumerateFiles(root, "*.tmp.*", SearchOption.AllDirectories).Any(), "原子历史写入留下临时文件");
    }

    private static void RecoveryContinuesAfterLockedFile(string root)
    {
        var folder = Path.Combine(root, "recovery");
        Directory.CreateDirectory(folder);
        HistoryTarget Target(string name)
        {
            var path = Path.Combine(folder, name);
            var snapshot = path + ".before";
            File.WriteAllBytes(snapshot, [1, 2, 3]);
            File.WriteAllBytes(path, [8, 8, 8]);
            return new HistoryTarget { TargetPath = path, SnapshotPath = snapshot, BeforeExists = true,
                BeforeLength = 3, BeforeSha256 = HistoryService.HashFile(snapshot) };
        }
        var unlocked = Target("unlocked.bin");
        var locked = Target("locked.bin");
        using (var handle = new FileStream(locked.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var method = typeof(ImageMutationService).Assembly.GetType("ImageManager.Core.ResourceFileRecovery")!
                .GetMethod("RestoreAll", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
            var errors = (IReadOnlyList<string>)method.Invoke(null, [new[] { unlocked, locked }, false])!;
            Assert(errors.Count == 1 && errors[0].Contains(locked.TargetPath, StringComparison.Ordinal), "未汇总锁定文件的恢复错误");
            Equal([1, 2, 3], File.ReadAllBytes(unlocked.TargetPath), "首个恢复失败阻断了其余文件恢复");
        }
    }

    private static void NestedRenameHistory(string root)
    {
        var folder = Path.Combine(root, "rename");
        var source = Path.Combine(folder, "a", "a", "a.txt");
        var target = Path.Combine(folder, "b", "b", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "原始文件内容");
        Directory.CreateDirectory(Path.Combine(folder, "a", "empty"));
        var profile = Profile(folder);
        var history = new HistoryService(Path.Combine(root, "history-rename"));
        var rename = new BatchRenameService(history);
        RenamePlan Plan() => rename.Preview(folder, new RenameRule("a", "b", "", "", null, false, true));
        var journal = Path.Combine(history.BackupRoot(profile), "History", "history.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "");
        using (var locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = rename.Apply(profile, Plan());
            Assert(!failed.Success, "目录改名历史失败时返回成功");
            Assert(File.Exists(source) && !File.Exists(target), "目录改名历史失败没有恢复全部路径");
            Assert(Directory.Exists(Path.Combine(folder, "a", "empty")), "目录改名失败丢失空目录");
        }
        var applied = rename.Apply(profile, Plan());
        Assert(applied.Success, string.Join("；", applied.Diagnostics));
        Assert(File.Exists(target) && !Directory.Exists(Path.Combine(folder, "a")), "嵌套父子目录改名没有正确安装");
        var savedJournal = File.ReadAllBytes(journal);
        using (var locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert(!history.Undo(profile, applied.HistoryId!).Success, "改名撤回历史失败时仍返回成功");
            Assert(File.Exists(target) && !File.Exists(source), "改名撤回历史失败没有恢复操作前路径");
        }
        Equal(savedJournal, File.ReadAllBytes(journal), "改名撤回失败改动了历史");
        var undo = history.Undo(profile, applied.HistoryId!);
        Assert(undo.Success, string.Join("；", undo.Diagnostics));
        Assert(File.Exists(source) && !Directory.Exists(Path.Combine(folder, "b")), "目录改名撤回没有恢复原路径");
        using (var locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert(!history.Redo(profile, applied.HistoryId!).Success, "改名前进历史失败时仍返回成功");
            Assert(File.Exists(source) && !File.Exists(target), "改名前进历史失败没有恢复操作前路径");
        }
        var redo = history.Redo(profile, applied.HistoryId!);
        Assert(redo.Success, string.Join("；", redo.Diagnostics));
        Assert(File.Exists(target), "目录改名前进没有重建目标路径");
        File.WriteAllText(target, "外部改动");
        Assert(!history.Undo(profile, applied.HistoryId!).Success, "目录内容被外部改动后仍然撤回");
        Assert(File.ReadAllText(target) == "外部改动", "改名撤回覆盖了外部内容");
    }

    private static ProjectProfile Profile(string root) => new(root, "资源事务测试", ProjectVersion.Ccz65, false,
        new Dictionary<string, string>(), []);

    private static byte[] Bmp(Color color)
    {
        using var bitmap = new Bitmap(4, 4);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Bmp); return stream.ToArray();
    }

    private static byte[] Png(Color color)
    {
        using var bitmap = new Bitmap(4, 4);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] Archive(byte[] payload)
    {
        var bytes = new byte[E5Archive.IndexOffset + E5Archive.EntrySize + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset), payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 4), payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 8), E5Archive.IndexOffset + E5Archive.EntrySize);
        payload.CopyTo(bytes, E5Archive.IndexOffset + E5Archive.EntrySize);
        return bytes;
    }

    private static void Equal(byte[] expected, byte[] actual, string message) => Assert(expected.AsSpan().SequenceEqual(actual), message);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
