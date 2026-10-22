using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class PersonQuoteTests
{
    public static void Run(string basePath)
    {
        TestPeReferenceGate();
        TestUnverifiedProjectGate(basePath);
        TestQuoteTransactions(basePath, "ccz-6.5", "65-standard-v1", 440200);
        TestModeSwitchTransactions(basePath, "ccz-6.5", "65-standard-v1");
        var standard66 = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底", "曹操传6.6-V7.16 解压密码：66");
        if (Directory.Exists(standard66))
        {
            TestQuoteTransactions(standard66, "ccz-6.6", "66-standard-v1", 491600);
            TestModeSwitchTransactions(standard66, "ccz-6.6", "66-standard-v1");
        }
    }

    private static void TestUnverifiedProjectGate(string source)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            var path = Path.Combine(root, "Ekd5.exe");
            var bytes = File.ReadAllBytes(path);
            bytes[0] = 0;
            File.WriteAllBytes(path, bytes);
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(!workspace.Documents.ContainsKey("person-critical-special-map"), "缺少 PE 证据时仍加载了特殊人物映射");
            var rejected = false;
            try { new PersonQuoteService(workspace).Apply(4, 0, ["甲", "乙", "丙"], null); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("PE", StringComparison.Ordinal); }
            TestSupport.Assert(rejected && !workspace.IsDirty, "缺少 PE 证据时人物台词事务必须拒绝且不得产生部分草稿");
            rejected = false;
            try { new PersonQuoteService(workspace).PreviewAssignment(4, PersonCriticalQuoteMode.Special, 0, 0); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("PE", StringComparison.Ordinal); }
            TestSupport.Assert(rejected && !workspace.IsDirty, "缺少 PE 证据时不得预览可写的台词模式切换");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestModeSwitchTransactions(string source, string profile, string variant)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root, profile, variant);
            var service = new PersonQuoteService(workspace);
            var originalSlots = service.GetSpecialSlots();
            var occupiedIds = originalSlots.Where(slot => !slot.IsEmpty).Select(slot => slot.PersonId).ToHashSet();
            var personId = Enumerable.Range(0, 49).First(id => !occupiedIds.Contains(id));
            var target = originalSlots.First(slot => !slot.IsEmpty);
            var person = workspace.GetDocument("persons").Data.Rows.Cast<DataRow>().Single(row => Convert.ToInt32(row["ID"]) == personId);
            var originalName = Convert.ToString(person["名称"]);
            var original = service.Resolve(personId);
            var beforeExe = File.ReadAllBytes(Path.Combine(root, "Ekd5.exe"));
            var beforeImsg = File.ReadAllBytes(Path.Combine(root, "Imsg.e5"));
            var beforeData = File.ReadAllBytes(Path.Combine(root, "Data.e5"));

            var preview = service.PreviewAssignment(personId, PersonCriticalQuoteMode.Special, 25, target.Id);
            TestSupport.Assert(preview.RequiresOverwrite && preview.ConflictingPersonId == target.PersonId &&
                preview.Critical.Single().Id == target.Id && !workspace.IsDirty,
                "特殊槽预览应报告原绑定人物和 Imsg 槽位，且不得修改草稿");
            var rejected = false;
            try { service.ApplyAssignment(personId, PersonCriticalQuoteMode.Special, 25, target.Id, ["冲突不得写"], null); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("占用", StringComparison.Ordinal); }
            TestSupport.Assert(rejected && !workspace.IsDirty && service.GetSpecialSlots().SequenceEqual(originalSlots),
                "未显式覆盖的特殊槽冲突必须无副作用地拒绝");
            rejected = false;
            try { service.ApplyAssignment(personId, PersonCriticalQuoteMode.Special, 25, target.Id, [new string('汉', 101)], "不应写入", true); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            TestSupport.Assert(rejected && !workspace.IsDirty && service.GetSpecialSlots().SequenceEqual(originalSlots),
                "特殊模式文本校验失败时不得提前改写人物绑定或撤退台词");

            var transaction = new BusinessDataService(workspace).BeginTransaction(BusinessPageKind.Persons, "人物详情与台词单事务");
            transaction.SetById("persons", personId, "名称", "台词事务");
            service.StageAssignment(transaction, personId, PersonCriticalQuoteMode.Special, 25, target.Id, ["新特殊台词"], "模式切换撤退", true);
            TestSupport.Assert(!workspace.IsDirty, "StageAssignment 不应提前修改工作区");
            TestSupport.Assert(transaction.Apply().Applied && service.Resolve(personId).IsSpecial &&
                service.Resolve(personId).CriticalType == original.CriticalType && service.Resolve(personId).Critical.Single().Text == "新特殊台词",
                "特殊模式应保持普通类型兼容值，并将槽号映射至正确单行文本");
            workspace.Commands.Undo();
            TestSupport.Assert(!workspace.IsDirty && service.GetSpecialSlots().SequenceEqual(originalSlots) && Convert.ToString(person["名称"]) == originalName,
                "人物详情与台词模式必须一次撤回");
            workspace.Commands.Redo();
            TestSupport.Assert(workspace.Save().Saved, "特殊槽覆盖事务保存失败");
            var afterExe = File.ReadAllBytes(Path.Combine(root, "Ekd5.exe"));
            var mapStart = (int)ProfileAugmentationService.SpecialQuoteOffset + target.Id * 4;
            TestSupport.Assert(Differences(beforeExe, afterExe).All(offset => offset >= mapStart && offset < mapStart + 4),
                "特殊绑定写回不得覆盖槽位以外的 EXE 数据");
            var afterImsg = File.ReadAllBytes(Path.Combine(root, "Imsg.e5"));
            var retreat = workspace.GetDocument("person-retreat-quotes").Definition;
            var retreatStart = (int)retreat.DataOffset + personId * retreat.RowSize;
            var quoteStart = 140000 + target.Id * 200;
            TestSupport.Assert(Differences(beforeImsg, afterImsg).All(offset =>
                offset >= quoteStart && offset < quoteStart + 200 || offset >= retreatStart && offset < retreatStart + retreat.RowSize),
                "特殊绑定写回只能修改目标暴击/撤退文本行");
            var nameField = workspace.GetDocument("persons").Definition.Fields.Single(field => field.Name == "名称");
            var nameStart = 396 + personId * 32 + nameField.Offset;
            TestSupport.Assert(Differences(beforeData, File.ReadAllBytes(Path.Combine(root, "Data.e5")))
                .All(offset => offset >= nameStart && offset < nameStart + nameField.Size),
                "切到特殊模式不应改写普通暴击类型字段");

            workspace.Open(root, profile, variant);
            service = new PersonQuoteService(workspace);
            TestSupport.Assert(service.Resolve(personId).IsSpecial && service.Resolve(personId).Critical.Single().Id == target.Id,
                "特殊绑定保存后复读失败");
            var nextTarget = service.GetSpecialSlots().First(slot => slot.Id != target.Id);
            service.ApplyAssignment(personId, PersonCriticalQuoteMode.Special, 0, nextTarget.Id, ["切换特殊槽"], null, true);
            TestSupport.Assert(service.GetSpecialSlots().Single(slot => slot.Id == target.Id).IsEmpty &&
                service.Resolve(personId).Critical.Single().Id == nextTarget.Id,
                "切换特殊槽必须清除原槽到 1024 并建立目标槽绑定");
            var type = (original.CriticalType + 1) % 26;
            service.ApplyAssignment(personId, PersonCriticalQuoteMode.Ordinary, type, null, ["普通甲", "普通乙", "普通丙"], null);
            TestSupport.Assert(!service.Resolve(personId).IsSpecial && service.Resolve(personId).CriticalType == type &&
                service.GetSpecialSlots().All(slot => slot.PersonId != personId),
                "切回普通模式必须清除该人物全部特殊槽并改写三句普通台词");
            workspace.Commands.Undo();
            TestSupport.Assert(service.Resolve(personId).IsSpecial && service.Resolve(personId).Critical.Single().Id == nextTarget.Id,
                "普通模式切换不能整体撤回到特殊绑定");
            workspace.Commands.Redo();
            TestSupport.Assert(workspace.Save().Saved, "普通模式切换保存失败");
            workspace.Open(root, profile, variant);
            var reread = new PersonQuoteService(workspace).Resolve(personId);
            TestSupport.Assert(!reread.IsSpecial && reread.CriticalType == type && reread.Critical.Select(row => row.Text).SequenceEqual(["普通甲", "普通乙", "普通丙"]),
                "普通模式切换保存复读失败");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestPeReferenceGate()
    {
        var bytes = BuildPeFixture();
        TestSupport.Assert(ProfileAugmentationService.HasExecutableReference(bytes, ProfileAugmentationService.SpecialQuoteOffset, out var evidence) &&
            evidence.Contains("PE", StringComparison.Ordinal), "有效 PE 可执行节台词映射引用未通过证据检查");
        var missingReference = (byte[])bytes.Clone();
        Array.Clear(missingReference, 0x410, 4);
        TestSupport.Assert(!ProfileAugmentationService.HasExecutableReference(missingReference, ProfileAugmentationService.SpecialQuoteOffset, out _),
            "没有可执行节引用时不应开放特殊人物映射");
        var invalidId = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidId.AsSpan((int)ProfileAugmentationService.SpecialQuoteOffset, 4), 1025);
        TestSupport.Assert(!ProfileAugmentationService.HasExecutableReference(invalidId, ProfileAugmentationService.SpecialQuoteOffset, out _),
            "特殊人物槽越界值不应通过验证");
        var dataOnly = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(dataOnly.AsSpan(0x178 + 36, 4), 0x40000040);
        TestSupport.Assert(!ProfileAugmentationService.HasExecutableReference(dataOnly, ProfileAugmentationService.SpecialQuoteOffset, out _),
            "仅非执行节包含指针时不应开放特殊人物映射");
        TestSupport.Assert(!ProfileAugmentationService.HasExecutableReference(new byte[64], ProfileAugmentationService.SpecialQuoteOffset, out _),
            "无效 PE 不应通过特殊台词映射验证");
    }

    private static byte[] BuildPeFixture()
    {
        var bytes = new byte[0x8A000];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80, 4), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94, 2), 0xE0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98, 2), 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 28, 4), 0x400000);
        // First section is executable code, second section owns the table.
        WriteSection(bytes, 0x178, 0x1000, 0x200, 0x400, 0x60000020);
        WriteSection(bytes, 0x1A0, 0x2000, 0x89800, 0x800, 0x40000040);
        var targetVa = 0x400000u + 0x2000u + (uint)ProfileAugmentationService.SpecialQuoteOffset - 0x800u;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x410, 4), targetVa);
        for (var slot = 0; slot < 21; slot++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)ProfileAugmentationService.SpecialQuoteOffset + slot * 4, 4), (uint)slot);
        return bytes;
    }

    private static void WriteSection(byte[] bytes, int position, uint rva, uint size, uint raw, uint flags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 12, 4), rva);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 16, 4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 20, 4), raw);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 36, 4), flags);
    }

    private static void TestQuoteTransactions(string source, string profile, string variant, int expectedRetreatOffset)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root, profile, variant);
            TestSupport.Assert(workspace.Documents.ContainsKey("person-critical-special-map"), $"{variant} 本地已验证特殊人物映射没有加载");
            var service = new PersonQuoteService(workspace);
            var specials = workspace.GetDocument("person-critical-special-map").Data.Rows.Cast<DataRow>()
                .Select(row => Convert.ToInt32(row["人物ID"], CultureInfo.InvariantCulture)).ToHashSet();
            var ordinaryId = Enumerable.Range(0, 49).First(id => !specials.Contains(id));
            var ordinary = service.Resolve(ordinaryId);
            TestSupport.Assert(!ordinary.IsSpecial && ordinary.Critical.Count == 3 && ordinary.Retreat?.Id == ordinaryId,
                "普通人物应映射三句暴击台词和按人物 ID 的撤退台词");
            var retreatDefinition = workspace.GetDocument("person-retreat-quotes").Definition;
            TestSupport.Assert(retreatDefinition.DataOffset == expectedRetreatOffset, $"{variant} 撤退台词偏移错误");
            var imsgPath = Path.Combine(root, "Imsg.e5");
            var dataPath = Path.Combine(root, "Data.e5");
            var exePath = Path.Combine(root, "Ekd5.exe");
            var imsgBefore = File.ReadAllBytes(imsgPath);
            var dataBefore = File.ReadAllBytes(dataPath);
            var exeHash = HashService.Sha256(exePath);
            var nextType = (ordinary.CriticalType + 1) % 26;
            var texts = new[] { "普通暴击甲", "普通暴击乙", "普通暴击丙" };
            var rejectedOversized = false;
            try { service.Apply(ordinaryId, nextType, [new string('汉', 101), "乙", "丙"], "不应部分写入"); }
            catch (ArgumentOutOfRangeException) { rejectedOversized = true; }
            TestSupport.Assert(rejectedOversized && !workspace.IsDirty && service.Resolve(ordinaryId).CriticalType == ordinary.CriticalType,
                "超长台词失败时人物字段和其他文本不应部分修改");
            var applied = service.Apply(ordinaryId, nextType, texts, "撤退事务烟测");
            TestSupport.Assert(applied.Applied && applied.ChangedCells >= 4 && workspace.IsDirty, "普通人物台词没有形成跨表事务");
            workspace.Commands.Undo();
            TestSupport.Assert(!workspace.IsDirty && service.Resolve(ordinaryId).CriticalType == ordinary.CriticalType,
                "人物台词跨表事务不能整体撤回");
            workspace.Commands.Redo();
            TestSupport.Assert(workspace.Save().Saved, $"{variant} 人物台词保存失败");
            var imsgAfter = File.ReadAllBytes(imsgPath);
            var dataAfter = File.ReadAllBytes(dataPath);
            var expectedQuoteIds = Enumerable.Range(21 + nextType * 3, 3).ToArray();
            var allowed = expectedQuoteIds.Select(id => (Start: 140000 + id * 200, End: 140000 + (id + 1) * 200))
                .Append((Start: expectedRetreatOffset + ordinaryId * 200, End: expectedRetreatOffset + (ordinaryId + 1) * 200)).ToArray();
            TestSupport.Assert(Differences(imsgBefore, imsgAfter).All(offset => allowed.Any(range => offset >= range.Start && offset < range.End)),
                "人物台词写入越过目标 Imsg 行");
            var selectorOffset = 396 + ordinaryId * 32 + 16;
            TestSupport.Assert(Differences(dataBefore, dataAfter).SequenceEqual([selectorOffset]), "普通台词修改只能维护人物暴击类型字段");
            TestSupport.Assert(HashService.Sha256(exePath) == exeHash, "人物台词修改不应覆盖特殊映射或其他 EXE 数据");
            if (profile == "ccz-6.6")
                TestSupport.Assert(imsgBefore.AsSpan(440200, 49 * 200).SequenceEqual(imsgAfter.AsSpan(440200, 49 * 200)),
                    "6.6 撤退台词错误地覆盖了 6.5 旧区域");

            workspace.Open(root, profile, variant);
            var reread = new PersonQuoteService(workspace).Resolve(ordinaryId);
            TestSupport.Assert(reread.CriticalType == nextType && reread.Critical.Select(item => item.Text).SequenceEqual(texts) &&
                reread.Retreat?.Text == "撤退事务烟测", "普通人物台词写后复读不一致");
            service = new PersonQuoteService(workspace);
            var specialId = specials.First(id => id < 1024);
            var special = service.Resolve(specialId);
            TestSupport.Assert(special.IsSpecial && special.Critical.Count == 1, "特殊人物必须映射单句优先台词");
            var specialBefore = File.ReadAllBytes(imsgPath);
            service.Apply(specialId, special.CriticalType, ["特殊暴击烟测"], null);
            TestSupport.Assert(workspace.Save().Saved, "特殊人物台词事务保存失败");
            var specialAfter = File.ReadAllBytes(imsgPath);
            var specialStart = 140000 + special.Critical[0].Id * 200;
            TestSupport.Assert(Differences(specialBefore, specialAfter).All(offset => offset >= specialStart && offset < specialStart + 200),
                "特殊人物单句台词写入越界");
            workspace.Open(root, profile, variant);
            TestSupport.Assert(new PersonQuoteService(workspace).Resolve(specialId).Critical.Single().Text == "特殊暴击烟测",
                "特殊人物台词写后复读不一致");
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<int> Differences(byte[] before, byte[] after)
        => Enumerable.Range(0, before.Length).Where(index => before[index] != after[index]);
}
