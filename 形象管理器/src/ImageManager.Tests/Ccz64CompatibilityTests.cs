using System.Buffers.Binary;
using System.Security.Cryptography;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class Ccz64CompatibilityTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        Check((int)ProjectVersion.Unknown == 0 && (int)ProjectVersion.Ccz65 == 1 &&
            (int)ProjectVersion.Ccz66 == 2 && (int)ProjectVersion.Ccz64 == 3, "旧版持久化枚举编号不得改变");
        var sample = Path.Combine(Directory.GetCurrentDirectory(), "基底", "洗隋");
        Check(Directory.Exists(sample), "缺少只读洗隋样本");
        var service = new ProjectProfileService();
        var project = service.Detect(sample);
        Check(project.Version == ProjectVersion.Ccz64 && project.VersionText == "6.4" && !project.IsReadOnly,
            "PE 私有版本4必须识别为6.4，不能因Itemicon/Mgcicon DLL归为6.5");
        var paths = project.ResourcePaths.Values.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hashes = paths.ToDictionary(path => path, Hash);
        var catalog = new ImageCatalogService();
        var resources = catalog.BuildCatalog(project);
        Check(resources.Any(resource => resource.Key == "Itemicon.dll" && resource.Kind == ResourceKind.DllBitmap),
            "6.4宝物图标使用实际DLL容器");
        Check(resources.Any(resource => resource.Key == "Mgcicon.dll" && resource.Kind == ResourceKind.DllBitmap),
            "6.4策略图标使用实际DLL容器");
        Check(resources.Any(resource => resource.Kind == ResourceKind.FaceComposite && resource.EntryCount == 514),
            "6.4重定向Face目录必须读取514个头像，缺Tou时按小头像模式");
        foreach (var resource in resources.Where(resource => resource.CanPreview))
        {
            var assets = catalog.ReadAssets(project, resource);
            Check(assets.Count > 0, resource.Key + " 已声明可预览但没有资源");
            foreach (var asset in assets.Take(1).Concat(assets.TakeLast(1)).Distinct())
            {
                var frames = catalog.DecodeFrames(project, asset);
                try
                {
                    Check(frames.Count > 0 && frames.All(frame => frame.Bitmap.Width > 0 && frame.Bitmap.Height > 0),
                        resource.Key + " 首末资源解码失败");
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
            Console.WriteLine($"6.4 容器 {resource.Key}: {resource.Kind}, {assets.Count} 项，首末项解码通过，写入={resource.CanMutate}");
        }
        var assignments = new CharacterAssignmentService();
        var layout = assignments.GetLayout(project);
        Check(layout.CanWrite && layout.ROffset == 0xE1000 && layout.SOffset == 0xD2800 && layout.RowCount == 1024,
            "洗隋完整消费者应证明1024×UInt16 R/S布局");
        var rows = assignments.Read(project);
        Check(rows.Count == 1024 && rows[0].Name == "拓跋骏", "人物名称与R/S指定表读取失配");
        Check(JobSDisplayService.LoadNames(project).GetValueOrDefault(0) == "群雄", "6.4兵种S名称必须来自当前EXE");
        Check(new StrategyRowService().Read(project).Count == 144, "6.4策略动画引用读取错误");

        var temporary = Path.Combine(Path.GetTempPath(), "Ccz64Images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "Ekd5.exe", "Data.e5", "Pmapobj.e5", "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5", "Itemicon.dll", "Face.e5" })
            {
                var target = Path.Combine(temporary, key);
                File.Copy(project.TryGetResource(key)!, target);
                map[key] = target;
            }
            var copy = new ProjectProfile(temporary, "6.4副本", ProjectVersion.Ccz64, false, map, []);
            Check(service.Detect(temporary).Version == ProjectVersion.Ccz64, "移除原路径提示后仍应以PE识别6.4");
            var original = File.ReadAllBytes(map["Ekd5.exe"]);
            var changedRows = assignments.Read(copy).ToArray();
            changedRows[0] = changedRows[0] with { RId = changedRows[0].RId == 0 ? 1 : 0 };
            var plan = assignments.PlanSave(copy, changedRows);
            var candidate = plan.Files.Single().CandidateBytes;
            var differences = Enumerable.Range(0, original.Length).Where(index => original[index] != candidate[index]).ToArray();
            Check(differences.Length > 0 && differences.All(index => index >= 0xE1000 && index < 0xE1002),
                "R指定仅允许修改目标UInt16字段");
            var history = new HistoryService(Path.Combine(temporary, "backup"));
            var mutations = new ImageMutationService(history);
            Check(mutations.Commit(copy, plan).Success, "6.4 R指定保存失败");
            Check(assignments.Read(copy)[0].RId == changedRows[0].RId && assignments.GetLayout(copy).CanWrite,
                "编辑后SHA变化不能丢失6.4布局身份");
            var record = history.List(copy).Last();
            Check(history.Undo(copy, record.Id).Success && File.ReadAllBytes(map["Ekd5.exe"]).SequenceEqual(original),
                "6.4 R指定撤回未逐字节恢复");
            Check(history.Redo(copy, record.Id).Success && File.ReadAllBytes(map["Ekd5.exe"]).SequenceEqual(candidate),
                "6.4 R指定前进未恢复候选");

            var faceBefore = File.ReadAllBytes(map["Face.e5"]);
            var face = E5Archive.OpenFresh(map["Face.e5"]);
            var portraitPayload = face.ReadDecoded(1);
            var rebuilt = E5Archive.Parse("Face.e5", face.Rebuild(new Dictionary<int, byte[]> { [1] = portraitPayload }));
            Check(rebuilt.Entries.Count == 514 && face.Entries.All(entry =>
                face.ReadDecoded(entry.ImageNumber).SequenceEqual(rebuilt.ReadDecoded(entry.ImageNumber))),
                "重定向Face重建必须保留全部514条有效头像载荷");
            var corruptFace = faceBefore.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(corruptFace.AsSpan(0x110 + 8, 4), uint.MaxValue);
            var faceRejected = false;
            try { E5Archive.Parse("Face.e5", corruptFace); } catch (Exception ex) when (ex is InvalidDataException or OverflowException) { faceRejected = true; }
            Check(faceRejected, "重定向Face损坏地址不可被恢复分支吞掉");

            var malformed = original.ToArray();
            malformed[0xB27D + 16] ^= 1;
            Check(!Ccz64AssignmentLayoutProbe.Inspect(malformed).CanWrite, "人物ID换算被修改时必须拒绝R/S读写");
            File.WriteAllBytes(map["Ekd5.exe"], malformed);
            Check(service.Detect(temporary).Version == ProjectVersion.Ccz64 && !assignments.GetLayout(copy).CanWrite,
                "6.4身份与指定能力分别裁决");
            var rejected = false;
            try { assignments.Read(copy); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "不安全6.4消费者必须禁止读取猜测偏移");
            malformed = original.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(0x8438 + 13, 4), 0xFFFFFFFF);
            Check(!Ccz64AssignmentLayoutProbe.Inspect(malformed).CanWrite, "越界表地址必须拒绝");
            Check(!Ccz64AssignmentLayoutProbe.Inspect(original[..0x90000]).CanWrite, "截断PE必须拒绝");
        }
        finally { Directory.Delete(temporary, true); }
        Check(paths.All(path => Hash(path) == hashes[path]), "原始洗隋图像/核心样本必须保持逐字节只读");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
