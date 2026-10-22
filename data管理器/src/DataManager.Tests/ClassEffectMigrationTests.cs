using System.Buffers.Binary;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Migration;

namespace DataManager.Tests;

internal static class ClassEffectMigrationTests
{
    private const int AssignmentOffset = 0xD9000;

    public static void Run(string basePath)
    {
        TestEightToSixteen(basePath);
        TestSixteenToEight(basePath);
        TestSpecialStructureDiagnostics(basePath);
        NativeStructureMigrationTests.Run(basePath);
        ItemMigrationTests.Run(basePath);
    }

    private static void TestEightToSixteen(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Seed(source, 8, bytes => Write8(bytes, 0, [12, 1024, 13], 7, 42));
            Seed(target, 16, bytes => Write16(bytes, 0, [21, 65535, 22, 99], [11, 91, 12, 88], [8, 9], [13, 99]));
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.6", "66-standard-v1");
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "class-effects" && change.Id == 0 && change.FieldName == "武将1特效值" && Convert.ToInt32(change.SourceValue) == 42), "8→16未映射共享特效值");
            TestSupport.Assert(!plan.WritableChanges.Any(change => change.TableKey == "class-effects" && change.FieldName is "武将4" or "武将4特效值" or "兵种2" or "兵种2特效值"), "8→16不应清空目标额外槽");
            var result = service.Apply(plan, AssignmentChoices(plan));
            TestSupport.Assert(result.Success, "8→16兵种特效迁移失败：" + string.Join("；", result.Messages));
            var workspace = new ProjectWorkspace(); workspace.Open(target, "ccz-6.6", "66-standard-v1");
            var row = workspace.GetDocument("class-effects").Data.Rows[0];
            TestSupport.Assert(Convert.ToInt32(row["武将1"]) == 12 && Convert.ToInt32(row["武将3"]) == 13 && Convert.ToInt32(row["兵种1"]) == 7, "8→16的前三人物/首兵种ID映射错误");
            TestSupport.Assert(Convert.ToInt32(row["武将1特效值"]) == 42 && Convert.ToInt32(row["武将3特效值"]) == 42 && Convert.ToInt32(row["兵种1特效值"]) == 42, "8→16有效槽没有取得共享值");
            TestSupport.Assert(Convert.ToInt32(row["武将2"]) == 1024 && Convert.ToInt32(row["武将2特效值"]) == 91, "8→16空人物槽的无效特效值未保留");
            TestSupport.Assert(Convert.ToInt32(row["武将4"]) == 99 && Convert.ToInt32(row["武将4特效值"]) == 88 && Convert.ToInt32(row["兵种2"]) == 9 && Convert.ToInt32(row["兵种2特效值"]) == 99, "8→16修改了目标额外分配槽");
        });
    }

    private static void TestSixteenToEight(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            Seed(source, 16, bytes =>
            {
                Write16(bytes, 0, [12, 65535, 13, 1024], [30, 99, 30, 100], [7, 255], [30, 101]);
                Write16(bytes, 1, [10, 65535, 65535, 77], [20, 0, 0, 20], [255, 255], [0, 0]);
                Write16(bytes, 2, [10, 65535, 65535, 65535], [20, 0, 0, 0], [255, 4], [0, 20]);
                Write16(bytes, 3, [10, 11, 65535, 65535], [20, 21, 0, 0], [255, 255], [0, 0]);
            });
            Seed(target, 8, bytes => Write8(bytes, 0, [21, 65535, 22], 8, 15));
            var before = File.ReadAllBytes(Path.Combine(target, "Ekd5.exe"));
            var service = new MigrationService();
            var plan = service.Preview(source, target, "ccz-6.6", "66-standard-v1", "ccz-6.5", "65-standard-v1");
            foreach (var (id, reason) in new[] { (1, "第4武将槽"), (2, "第2兵种槽"), (3, "特效值不一致") })
            {
                TestSupport.Assert(plan.Changes.Any(change => change.TableKey == "class-effects" && change.Id == id && change.Kind == MigrationChangeKind.Incompatible && change.Reason!.Contains(reason, StringComparison.Ordinal)), $"16→8没有报告不可表达原因：{reason}");
                TestSupport.Assert(!plan.WritableChanges.Any(change => change.TableKey == "class-effects" && change.Id == id), "不可表达的16字节分配行不应产生部分可写映射");
            }
            var result = service.Apply(plan, AssignmentChoices(plan));
            TestSupport.Assert(result.Success, "16→8兵种特效迁移失败：" + string.Join("；", result.Messages));
            var workspace = new ProjectWorkspace(); workspace.Open(target, "ccz-6.5", "65-standard-v1");
            var row = workspace.GetDocument("class-effects").Data.Rows[0];
            TestSupport.Assert(Convert.ToInt32(row["一号武将"]) == 12 && Convert.ToInt32(row["二号武将"]) == 65535 && Convert.ToInt32(row["三号武将"]) == 13 && Convert.ToInt32(row["兵种"]) == 7 && Convert.ToInt32(row["特效值"]) == 30, "16→8可表达行逻辑映射错误");
            var after = File.ReadAllBytes(Path.Combine(target, "Ekd5.exe"));
            TestSupport.Assert(before.AsSpan(AssignmentOffset + 8, 3 * 8).SequenceEqual(after.AsSpan(AssignmentOffset + 8, 3 * 8)), "16→8不可表达行被部分修改");
        });
    }

    private static void TestSpecialStructureDiagnostics(string basePath)
    {
        WithProjects(basePath, (source, target) =>
        {
            var workspace = new ProjectWorkspace(); workspace.Open(source);
            new EffectNamePoolService().WriteItemName(workspace, 0x1A, "测试名称");
            new AccessoryJobGroupService().Write(workspace, [[0, 1], [2, 3]]);
            var path = Path.Combine(source, "Ekd5.exe");
            var bytes = File.ReadAllBytes(path);
            EncodingService.EncodeFixedString("专用名称", 16).CopyTo(bytes, checked((int)EffectNamePoolService.PersonalJobOffset));
            File.WriteAllBytes(path, bytes);
            var plan = new MigrationService().Preview(source, target);
            TestSupport.Assert(!plan.Changes.Any(change => change.TableKey is "effect-names" or "item-effect-name-pool"), "特效名称池差异不应阻塞或生成迁移项");
            TestSupport.Assert(plan.WritableChanges.Any(change => change.TableKey == "job-series-accessory-groups" && change.Kind == MigrationChangeKind.NativeStructureChanged && change.Reason!.Contains("PE", StringComparison.Ordinal)), "辅助分组没有接入目标安全区迁移");
        });
    }

    private static Dictionary<int, MigrationChoice> AssignmentChoices(MigrationPlan plan)
        => plan.Changes.Select((change, index) => (Change: change, Index: index))
            .Where(item => item.Change.TableKey == "class-effects" && item.Change.Kind == MigrationChangeKind.Changed)
            .ToDictionary(item => item.Index, _ => MigrationChoice.Overwrite);

    private static void Seed(string root, int rowSize, Action<byte[]> configure)
    {
        var path = Path.Combine(root, "Ekd5.exe");
        var bytes = File.ReadAllBytes(path);
        bytes.AsSpan(AssignmentOffset, 255 * rowSize).Fill(255);
        if (rowSize == 8) for (var row = 0; row < 255; row++) bytes[AssignmentOffset + row * rowSize + 7] = 0;
        configure(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static void Write8(byte[] bytes, int row, int[] persons, int job, int value)
    {
        var offset = AssignmentOffset + row * 8;
        for (var index = 0; index < 3; index++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + index * 2, 2), checked((ushort)persons[index]));
        bytes[offset + 6] = checked((byte)job); bytes[offset + 7] = checked((byte)value);
    }

    private static void Write16(byte[] bytes, int row, int[] persons, int[] values, int[] jobs, int[] jobValues)
    {
        var offset = AssignmentOffset + row * 16;
        for (var index = 0; index < 4; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + index * 3, 2), checked((ushort)persons[index]));
            bytes[offset + index * 3 + 2] = checked((byte)values[index]);
        }
        bytes[offset + 12] = checked((byte)jobs[0]); bytes[offset + 13] = checked((byte)jobValues[0]);
        bytes[offset + 14] = checked((byte)jobs[1]); bytes[offset + 15] = checked((byte)jobValues[1]);
    }

    private static void WithProjects(string basePath, Action<string, string> test)
    {
        var source = TestSupport.CreateCoreCopy(basePath); var target = TestSupport.CreateCoreCopy(basePath);
        try { test(source, target); }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }
}
