using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Migration;

namespace DataManager.Tests;

internal static class MigrationResourceTests
{
    public static void Run(string basePath)
    {
        TestResourceCoverageAndTargetLayout(basePath);
        TestPhysicalBasenameDoesNotHideNestedImageArchive(basePath);
        TestImageResourceFamiliesEnumerated(basePath);
        TestResourceStalePlan(basePath);
        TestAuxiliaryPhysicalFile(basePath);
        TestCommitRollback(basePath);
        TestNestedRootsRejected(basePath);
        TestMissingAuxiliaryFilesCreatedByTargetCodec(basePath);
        TestImageStorageConversionReported(basePath);
        TestAllAuxiliaryFilesAcrossProfiles(basePath);
        TestExtensionDataAndRuntimeCodeAreNotImages(basePath);
        TestAudioAndArchiveConfiguration(basePath);
        TestExtendedItemSegmentIgnoredWithoutTargetConsumer(basePath);
    }

    private static void TestResourceCoverageAndTargetLayout(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "RS/R_00.eex", [1, 2, 3]);
            Write(source, "E5/Item.e5", [9, 8, 7]);
            Write(target, "Item.e5", [1, 1, 1]);
            Write(source, "Map/M001.jpg", [4, 5, 6]);
            Write(source, "Logo.avi", [7, 8, 9]);
            Write(source, ".data管理器历史/old.bin", [1]);
            Write(source, ".形象管理器历史/old.e5", [2]);
            var sourceHash = HashService.Sha256(Path.Combine(source, "Data.e5"));
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            TestSupport.Assert(plan.SourceCoreFileFingerprints.TryGetValue("Item.e5", out var missingRootItems) && missingRootItems.Length == 0 &&
                !plan.SourceCoreFileFingerprints.ContainsKey(Path.Combine("E5", "Item.e5")),
                "扩展物品物理表指纹错误地回退至 E5 图标档案，或漏记根目录物理表缺失状态");
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableDisplayName == Path.Combine("RS", "R_00.eex")), "迁移漏掉 RS eex 资源");
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableDisplayName == "Logo.avi"), "迁移漏掉动画资源");
            TestSupport.Assert(!plan.Changes.Any(change => change.TableDisplayName.Contains("历史", StringComparison.Ordinal)), "迁移不应扫描工具历史目录");
            var item = plan.WritableChanges.SingleOrDefault(change => change.TableDisplayName == Path.Combine("E5", "Item.e5"));
            TestSupport.Assert(item != null, "图标资源未生成可写迁移：" + string.Join('；', plan.Changes.Where(change => change.TableDisplayName.Contains("Item", StringComparison.OrdinalIgnoreCase))
                .Select(change => $"{change.TableKey}/{change.TableDisplayName}/{change.Kind}/{change.Reason}")));
            TestSupport.Assert(item!.Kind == MigrationChangeKind.ResourceChanged && item.TargetRelativePath == "Item.e5", "根目录/E5 同名资源未映射至目标原位置");
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success, "资源迁移失败：" + string.Join("；", result.Messages));
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "Item.e5")).SequenceEqual(new byte[] { 9, 8, 7 }), "目标图标资源未写回");
            TestSupport.Assert(!File.Exists(Path.Combine(target, "E5", "Item.e5")), "迁移创建了遮蔽目标原资源的副本");
            TestSupport.Assert(File.Exists(Path.Combine(target, "RS", "R_00.eex")) && File.Exists(Path.Combine(target, "Map", "M001.jpg")), "新增资源未提交");
            TestSupport.Assert(sourceHash == HashService.Sha256(Path.Combine(source, "Data.e5")), "迁移修改了源核心文件");
            TestSupport.Assert(result.BackupPath != null && File.Exists(Path.Combine(result.BackupPath, "migration.json")), "资源迁移未写入历史日志");
            TestSupport.Assert(!Directory.EnumerateFiles(target, "*.tmp", SearchOption.AllDirectories).Any(), "迁移遗留候选临时文件");
        });
    }

    private static void TestResourceStalePlan(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "Map/M001.jpg", [1, 2]);
            Write(target, "Map/M001.jpg", [3, 4]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            Write(source, "Map/M001.jpg", [5, 6]);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success && result.Messages.Any(message => message.Contains("预览后发生变化", StringComparison.Ordinal)), "资源源 SHA 变更未阻止旧计划");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "Map", "M001.jpg")).SequenceEqual(new byte[] { 3, 4 }), "失败预检修改了目标资源");
            plan = service.Preview(source, target);
            Write(target, "Map/M001.jpg", [7, 8]);
            result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success && result.Messages.Any(message => message.Contains("预览后发生变化", StringComparison.Ordinal)), "资源目标 SHA 变更未阻止旧计划");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "Map", "M001.jpg")).SequenceEqual(new byte[] { 7, 8 }), "过期计划覆盖了外部修改");
        });
    }

    private static void TestPhysicalBasenameDoesNotHideNestedImageArchive(string basePath)
    {
        // Data.e5 is a physical table at the project root. An E5 image archive
        // may legally use the same basename in the E5 directory; resource
        // enumeration must compare resolved paths, not just file names.
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "E5/Data.e5", [9, 8, 7]);
            Write(target, "E5/Data.e5", [1, 1, 1]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var resource = plan.WritableChanges.SingleOrDefault(change =>
                change.TableKey == "__resource__" &&
                change.TableDisplayName.Equals(Path.Combine("E5", "Data.e5"), StringComparison.OrdinalIgnoreCase));
            TestSupport.Assert(resource != null, "E5/Data.e5 与根物理 Data.e5 同名时被资源枚举错误过滤");
            TestSupport.Assert(resource!.Kind == MigrationChangeKind.ResourceChanged,
                "嵌套同名 E5 资源没有按资源变更处理");
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success &&
                               File.ReadAllBytes(Path.Combine(target, "E5", "Data.e5")).SequenceEqual(new byte[] { 9, 8, 7 }),
                "嵌套同名 E5 资源没有写回目标原位置");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(source, "E5", "Data.e5")).SequenceEqual(new byte[] { 9, 8, 7 }),
                "嵌套同名 E5 资源迁移修改了源文件");
        });
    }

    private static void TestImageResourceFamiliesEnumerated(string basePath)
    {
        // These are the storage files used by the integrated image manager.
        // Keep this list explicit so adding a new image category cannot silently
        // fall outside the migration file scan.
        var families = new[]
        {
            "E5/Face.e5", "Tou.dll", "Pmapobj.e5", "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5",
            "E5/Item.e5", "E5/Mtem.e5", "E5/Hitarea.e5", "E5/Effarea.e5", "E5/Meff.e5", "E5/Mcall00.e5",
            "Itemicon.dll", "Mgcicon.dll"
        };
        WithProjects(basePath, (source, target) =>
        {
            foreach (var family in families)
            {
                Write(source, family, [9, 8, 7]);
                Write(target, family, [1, 1, 1]);
            }
            var changes = new MigrationService().Preview(source, target).Changes
                .Where(change => change.TableKey == "__resource__" &&
                                 change.Kind is MigrationChangeKind.ResourceChanged or MigrationChangeKind.ResourceAdded)
                .ToDictionary(change => change.TableDisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (var family in families)
            {
                var relative = family.Replace('/', Path.DirectorySeparatorChar);
                TestSupport.Assert(changes.ContainsKey(relative), $"资源枚举漏掉图片档案：{family}");
            }
            var sourceHashes = families.ToDictionary(family => family,
                family => HashService.Sha256(Path.Combine(source, family)));
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success, "图片族资源迁移失败：" + string.Join("；", result.Messages));
            foreach (var family in families)
            {
                TestSupport.Assert(HashService.Sha256(Path.Combine(target, family)) == sourceHashes[family],
                    $"图片族资源没有完整写入：{family}");
                TestSupport.Assert(HashService.Sha256(Path.Combine(source, family)) == sourceHashes[family],
                    $"图片族资源迁移修改了源文件：{family}");
            }
        });
    }

    private static void TestAuxiliaryPhysicalFile(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var sourceBytes = new byte[1024 * 64];
            var targetBytes = new byte[1024 * 64];
            sourceBytes[30] = 7; // Material 1 ID.
            sourceBytes[58] = 1; // The first of the eight items is a material.
            sourceBytes[62] = 201; // Undocumented tail bytes stay native to the target.
            targetBytes[62] = 99;
            Write(source, "Forge.e5", sourceBytes);
            Write(target, "Forge.e5", targetBytes);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "forge"), "锻造物理表差异未进入逻辑迁移");
            TestSupport.Assert(!plan.Changes.Any(change => change.TableKey == "__resource__" && Path.GetFileName(change.TableDisplayName).Equals("Forge.e5", StringComparison.OrdinalIgnoreCase)), "锻造文件被错误地加入原样资源迁移");
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success, "拓展物理表迁移失败：" + string.Join("；", result.Messages));
            var migrated = File.ReadAllBytes(Path.Combine(target, "Forge.e5"));
            TestSupport.Assert(migrated[30] == 7 && migrated[58] == 1 && migrated[62] == 99, "拓展表没有迁移材料项数或保留未知尾字节");
            TestSupport.Assert(result.BackupPath != null && File.ReadAllBytes(Path.Combine(result.BackupPath, "files", "Forge.e5")).SequenceEqual(targetBytes), "拓展物理文件没有完整备份");

            plan = service.Preview(source, target);
            migrated[31] = 1;
            Write(target, "Forge.e5", migrated);
            result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success, "拓展物理文件外改未使旧计划失效");
        });
    }

    private static void TestCommitRollback(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "First.png", [10]);
            Write(target, "First.png", [11]);
            Write(source, "Second.png", [20]);
            Write(target, "Second.png", [21]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var resourceChanges = plan.Changes.Where(change => change.Kind is MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged).ToArray();
            var last = resourceChanges.Last();
            // Read-sharing allows preview, backup and staging, while prohibiting replacement.
            using var blocker = new FileStream(Path.Combine(target, last.TargetRelativePath ?? last.TableDisplayName),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success, "锁定目标资源应触发提交失败");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "First.png")).SequenceEqual(new byte[] { 11 }) &&
                               File.ReadAllBytes(Path.Combine(target, "Second.png")).SequenceEqual(new byte[] { 21 }), "提交失败没有恢复所有已替换资源");
            TestSupport.Assert(result.BackupPath != null && File.Exists(Path.Combine(result.BackupPath, "failure.json")), "迁移失败未保留回滚记录");
        });
    }

    private static void TestNestedRootsRejected(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var rejected = false;
            try { new MigrationService().Preview(root, Path.Combine(root, "target")); }
            catch (InvalidOperationException) { rejected = true; }
            TestSupport.Assert(rejected, "迁移不应允许源目标目录互相包含");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestMissingAuxiliaryFilesCreatedByTargetCodec(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var forge = new byte[1024 * 64];
            forge[30] = 17;
            forge[58] = 1;
            forge[62] = 222; // Unknown source tail bytes must never be cloned to the new target.
            Write(source, "Forge.e5", forge);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            TestSupport.Assert(plan.WritableChanges.Any(change => change.Kind == MigrationChangeKind.PhysicalFileAdded && change.TableDisplayName == "Forge.e5"), "缺失锻造文件未提供按目标几何新建项");
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "forge" && change.FieldName == "材料1ID"), "新建物理文件未显示逻辑字段预览");
            var fieldOnly = plan.Changes.Select((change, index) => (Change: change, Index: index))
                .Where(item => item.Change.TableKey == "forge").ToDictionary(item => item.Index, _ => MigrationChoice.Overwrite);
            var result = service.Apply(plan, fieldOnly);
            TestSupport.Assert(result.Success && !File.Exists(Path.Combine(target, "Forge.e5")), "未选择文件创建仍创建了拓展文件");
            result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success, "目标缺失拓展文件创建迁移失败：" + string.Join("；", result.Messages));
            var migrated = File.ReadAllBytes(Path.Combine(target, "Forge.e5"));
            TestSupport.Assert(migrated.Length == 1024 * 64 && migrated[30] == 17 && migrated[58] == 1 && migrated[62] == 0, "拓展文件创建未按目标 Codec 映射或复制了源未知尾字节");
            TestSupport.Assert(forge.SequenceEqual(File.ReadAllBytes(Path.Combine(source, "Forge.e5"))), "拓展文件创建迁移修改了源文件");
        });
        WithProjects(basePath, (source, target) =>
        {
            var forge = new byte[1024 * 64]; forge[30] = 13;
            Write(source, "Forge.e5", forge);
            Write(source, "Locked.png", [1]); Write(target, "Locked.png", [2]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            using var blocker = new FileStream(Path.Combine(target, "Locked.png"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success && !File.Exists(Path.Combine(target, "Forge.e5")), "迁移失败没有删除本次新建拓展文件");
        });
    }

    private static void TestImageStorageConversionReported(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "Itemicon.dll", [1]);
            Write(target, "E5/Item.e5", [2]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var change = plan.Changes.Single(change => change.TableDisplayName == "Itemicon.dll");
            TestSupport.Assert(change.Kind == MigrationChangeKind.Incompatible && change.Reason!.Contains("DLL ↔ E5", StringComparison.Ordinal), "跨图片存储格式没有显式不可转换预览");
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success && !File.Exists(Path.Combine(target, "Itemicon.dll")), "原样复制了在目标中不生效的图片存储");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "E5", "Item.e5")).SequenceEqual(new byte[] { 2 }), "不可转换图标资源影响了目标原资源");
        });
    }

    private static void TestAllAuxiliaryFilesAcrossProfiles(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var effect = new byte[1024 * 1024]; effect[0] = 13; effect[1] = 14; effect[2] = 222;
            var messages = new byte[255 * 8]; messages[0] = 1;
            var study = new byte[1024 * 64]; study[0] = 17; study[1] = 3;
            var forge = new byte[1024 * 64]; forge[30] = 19; forge[58] = 1; forge[62] = 222;
            Write(source, "Effect.e5", effect); Write(source, "Effectmsg.e5", messages);
            Write(source, "MeffStudy.e5", study); Write(source, "Forge.e5", forge);
            var service = new MigrationService();
            var plan = service.Preview(source, target, targetProfile: "ccz-6.6", targetVariant: "66-standard-v1");
            TestSupport.Assert(plan.PhysicalFileCreations.Count == 4, "跨 Profile 迁移未提供四类目标拓展文件创建");
            var keys = new HashSet<string>(["__physical_file__", "effect-archive", "effect-messages", "strategy-study-extended", "forge"], StringComparer.OrdinalIgnoreCase);
            var choices = plan.Changes.Select((change, index) => (Change: change, Index: index)).Where(item => keys.Contains(item.Change.TableKey))
                .ToDictionary(item => item.Index, _ => MigrationChoice.Overwrite);
            var result = service.Apply(plan, choices);
            TestSupport.Assert(result.Success, "跨 Profile 拓展文件迁移失败：" + string.Join("；", result.Messages));
            var migratedEffect = File.ReadAllBytes(Path.Combine(target, "Effect.e5"));
            TestSupport.Assert(migratedEffect[0] == 13 && migratedEffect[1] == 14 && migratedEffect[2] == 222, "跨 Profile 特效拓展没有迁移已定义的经验字段");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(target, "Effectmsg.e5"))[0] == 1 &&
                               File.ReadAllBytes(Path.Combine(target, "MeffStudy.e5"))[0] == 17 &&
                               File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[30] == 19 &&
                               File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[58] == 1 &&
                               File.ReadAllBytes(Path.Combine(target, "Forge.e5"))[62] == 0,
                "跨 Profile 拓展文件逻辑字段没有完整写回");
        });
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void TestExtensionDataAndRuntimeCodeAreNotImages(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "Item.e5", new byte[256 * 25]);
            Write(source, "Itemmsg.e5", new byte[256 * 200]);
            Write(source, "Itemicon.dll", [1]);
            Write(source, "ExtendData.dll", [3, 4]);
            Write(source, "ExtendModifier.exe", [5, 6]);
            Write(target, "E5/Item.e5", [7, 8]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            TestSupport.Assert(!plan.WritableChanges.Any(change => change.TableDisplayName is "Item.e5" or "Itemmsg.e5" or "ExtendData.dll" or "ExtendModifier.exe"),
                "扩展物品数据或引擎程序被错误当作可原样迁移图片");
            TestSupport.Assert(plan.Changes.Any(change => change.TableDisplayName == "ExtendData.dll" && change.Kind == MigrationChangeKind.Incompatible),
                "扩展插件未验证接口却没有报告阻断");
            TestSupport.Assert(plan.Changes.Single(change => change.TableDisplayName == "Itemicon.dll").TargetRelativePath == Path.Combine("E5", "Item.e5"),
                "root Item扩展数据挡住真实图标配对");
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success && File.ReadAllBytes(Path.Combine(target, "E5", "Item.e5")).SequenceEqual(new byte[] { 7, 8 }),
                "扩展物品数据覆盖了目标图片包");
        });
    }

    private static void TestAudioAndArchiveConfiguration(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Write(source, "WAV/攻击.wav", [10, 11]);
            Write(source, "SoundTrk/曲目.mp3", [12, 13]);
            Write(source, "E5/SETTINGE5", [14]);
            Write(source, "SV/SV000.E5S", [15]);
            Write(source, "SV/SV000.EXS", [16]);
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            TestSupport.Assert(plan.WritableChanges.Count(change => change.TableKey == "__resource__") == 3, "音频/E5配置未完整识别或错误迁入存档");
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success && File.ReadAllBytes(Path.Combine(target, "WAV", "攻击.wav")).SequenceEqual(new byte[] { 10, 11 }) &&
                File.Exists(Path.Combine(target, "SoundTrk", "曲目.mp3")) && File.Exists(Path.Combine(target, "E5", "SETTINGE5")) && !Directory.Exists(Path.Combine(target, "SV")),
                "音频/配置迁移或存档排除失败");
            TestSupport.Assert(!Directory.Exists(Path.Combine(target, ".data管理器迁移临时")), "迁移未清理目标盘临时项目");
        });
    }

    private static void TestExtendedItemSegmentIgnoredWithoutTargetConsumer(string basePath)
    {
        // Reuse the checked-in 18-state executable only as an evidence fixture;
        // all writes are confined to disposable core copies.  Removing its two
        // consumer signatures from the target models a 6.6 target that has no
        // verified ID>255 extension reader.
        var real = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底", "18州", "十八州演义0.2版本");
        if (!File.Exists(Path.Combine(real, "Item.e5")) || !File.Exists(Path.Combine(real, "Itemmsg.e5")) ||
            !File.Exists(Path.Combine(real, "Ekd5.exe"))) return;
        var source = TestSupport.CreateCoreCopy(real, restoreKnown65Snapshot: false);
        var target = TestSupport.CreateCoreCopy(real, restoreKnown65Snapshot: false);
        try
        {
            File.Copy(Path.Combine(real, "Item.e5"), Path.Combine(source, "Item.e5"));
            File.Copy(Path.Combine(real, "Itemmsg.e5"), Path.Combine(source, "Itemmsg.e5"));
            var removed = RemoveExtendedItemConsumers(Path.Combine(target, "Ekd5.exe"));
            TestSupport.Assert(removed >= 2, "扩展物品消费者证据 fixture 未找到两段 PE 指令");

            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var ignored = plan.Changes.Where(change => change.TableKey == "extended-items" && change.Kind == MigrationChangeKind.Ignored).ToArray();
            TestSupport.Assert(ignored.Length == 1 && ignored[0].Id < 0 && ignored[0].Reason!.Contains("整段", StringComparison.Ordinal),
                "目标无扩展物品消费者时没有生成单条整段忽略诊断");
            TestSupport.Assert(!plan.Changes.Any(change => change.TableKey == "extended-items" && change.Id >= 256),
                "目标无扩展物品消费者时仍生成了逐条扩展物品记录");
            TestSupport.Assert(!plan.WritableChanges.Any(change => change.TableKey == "extended-items"),
                "被整段忽略的扩展物品不应进入可写迁移选择");
            Console.WriteLine($"EXTENDED_ITEM_SEGMENT_IGNORED changes={plan.Changes.Count} ignored={ignored.Length}");

            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(result.Success, "整段忽略迁移不应失败：" + string.Join("；", result.Messages));
            TestSupport.Assert(!File.Exists(Path.Combine(target, "Item.e5")) && !File.Exists(Path.Combine(target, "Itemmsg.e5")),
                "整段忽略仍在目标创建扩展物品文件");
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, true);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    private static int RemoveExtendedItemConsumers(string path)
    {
        var bytes = File.ReadAllBytes(path);
        byte[][] patterns =
        [
            [0x81, 0xE9, 0x00, 0x01, 0x00, 0x00, 0x6B, 0xC9, 0x19, 0x03, 0x0D, 0x1C, 0x00, 0x4F, 0x00],
            [0x25, 0xFF, 0x00, 0x00, 0x00, 0xB9, 0xC8, 0x00, 0x00, 0x00, 0xF7, 0xE1, 0x8B, 0x0D, 0x24, 0x00, 0x4F, 0x00]
        ];
        var removed = 0;
        foreach (var pattern in patterns)
        {
            var offset = 0;
            while (offset <= bytes.Length - pattern.Length)
            {
                var relative = bytes.AsSpan(offset).IndexOf(pattern);
                if (relative < 0) break;
                var start = offset + relative;
                Array.Clear(bytes, start, pattern.Length);
                removed++;
                offset = start + pattern.Length;
            }
        }
        File.WriteAllBytes(path, bytes);
        return removed;
    }

    private static void WithProjects(string basePath, Action<string, string> test)
    {
        var source = TestSupport.CreateCoreCopy(basePath);
        var target = TestSupport.CreateCoreCopy(basePath);
        try { test(source, target); }
        finally
        {
            Directory.Delete(source, true);
            Directory.Delete(target, true);
        }
    }
}
