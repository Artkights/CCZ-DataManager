using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class RealAtlasCompatibilityTests
{
    public static void Run()
    {
        var workspace = FindWorkspace();
        var evidence = Path.Combine(workspace, "data管理器", "验证", "参考图灵活回导-20261026");
        Directory.CreateDirectory(evidence);
        var reportPath = Path.Combine(evidence, "真实基底兼容.txt");
        var report = new List<string> { "真实基底 S_242 参考图兼容与三文件副本事务", DateTimeOffset.Now.ToString("O"),
            "HighDpiMode=" + Application.HighDpiMode };
        var temporary = Path.Combine(Path.GetTempPath(), "RealAtlasCompatibility_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var sample in new[]
            {
                (Name: "洗隋", Version: ProjectVersion.Ccz64),
                (Name: "加强版6.5未加密版", Version: ProjectVersion.Ccz65),
                (Name: "复刻版6.6山贼", Version: ProjectVersion.Ccz66)
            })
            {
                var original = new ProjectProfileService().Detect(Path.Combine(workspace, "基底", sample.Name));
                Require(original.Version == sample.Version && !original.IsReadOnly, "真实基底版本识别或读写能力不符：" + sample.Name);
                report.Add($"基底 {sample.Name} 版本={original.VersionText}");
                var copyRoot = Path.Combine(temporary, sample.Name);
                Directory.CreateDirectory(copyRoot);
                var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in new[] { "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5", "Spalet.e5" })
                {
                    var source = original.TryGetResource(key);
                    Require(source != null && File.Exists(source), sample.Name + " 缺少真实资源 " + key);
                    hashes[source!] = Hash(source!);
                    var target = Path.Combine(copyRoot, key);
                    File.Copy(source!, target);
                    paths[key] = target;
                    report.Add($"  只读源 {key}: {source}; SHA256={hashes[source!]}");
                }
                var copy = original with { RootPath = copyRoot, ResourcePaths = paths, DisplayName = sample.Name + "验收副本" };
                var catalog = new ImageCatalogService();
                var resource = catalog.BuildCatalog(copy).Single(item => item.Kind == ResourceKind.SComposite);
                Require(resource.CanMutate, sample.Name + " 副本 S 资源未开放写入");
                var asset = catalog.ReadAssets(copy, resource).Single(item => item.PhysicalImageNumber == 242);
                var referenceFrames = catalog.DecodeFrames(copy, asset);
                try
                {
                    Require(referenceFrames.Count == 28 && referenceFrames.All(frame => frame.Descriptor.ImageNumber == 242),
                        sample.Name + " 真实物理S_242没有解出完整28帧");
                    var context = ReferenceAtlasContext.Create(asset, referenceFrames);
                    var targetKeys = context.Frames.Select(frame => AiAtlasNormalizer.FrameKey(frame.Descriptor)).ToHashSet();
                    foreach (var key in paths.Keys.Where(key => key.StartsWith("Unit_", StringComparison.Ordinal)))
                    {
                        var archive = E5Archive.OpenFresh(paths[key]);
                        report.Add($"  源编码 {key}: count={archive.Entries.Count}, #242={EncodingOf(archive.ReadDecoded(242))}");
                    }
                    foreach (var image in new[] { "暗紫背景-2048.png", "白底边框-663.png" })
                    {
                        var source = Path.Combine(workspace, "形象管理器", "src", "ImageManager.Tests", "Fixtures", "Atlas", image);
                        using var normalized = new ReferenceAtlasService().Normalize(source, context, new());
                        report.Add($"  回图 {image}: background={normalized.BackgroundKind}, roi={normalized.SourceMapping?.EffectiveBounds}, errors={normalized.FrameDiagnostics.Count(issue => issue.Severity == AtlasIssueSeverity.Error)}");
                        report.AddRange(normalized.Diagnostics.Select(line => "    " + line));
                        report.AddRange(normalized.FrameDiagnostics.Select(issue => $"    {issue.FrameKey} {issue.SourceBounds} {issue.Severity}: {issue.Message}"));
                        File.WriteAllLines(reportPath, report, new UTF8Encoding(false));
                        Require(!normalized.HasBlockingIssues, sample.Name + " / " + image + " 自动识别出现阻断，见真实基底兼容.txt");
                        Require(normalized.Frames.Count == 28 && normalized.Frames.All(frame => frame.Descriptor.ImageNumber == 242) &&
                            targetKeys.SetEquals(normalized.Frames.Select(frame => AiAtlasNormalizer.FrameKey(frame.Descriptor))),
                            sample.Name + " / " + image + " 输出未严格绑定真实S_242当前帧键");
                        ValidateTransaction(copy, asset, normalized.Frames, report);
                        normalized.NormalizedAtlas.Save(Path.Combine(evidence, sample.Name + "-" + image + ".原生结果.png"), ImageFormat.Png);
                        report.Add("  PASS " + sample.Name + " / " + image + " 28帧映射、三文件提交、邻近隔离、复读、撤回、重做");
                        Console.WriteLine("REAL_BASE_ATLAS_OK " + sample.Name + " / " + image);
                    }
                }
                finally
                {
                    foreach (var frame in referenceFrames) frame.Dispose();
                    foreach (var pair in hashes) Require(Hash(pair.Key) == pair.Value, "原始基底资源发生改变：" + pair.Key);
                }
                report.Add("  原始四文件 SHA256 复核一致。");
            }
            report.Add("全部通过：3个真实基底 × 2张回图；所有写入仅在独立副本。");
        }
        catch (Exception error)
        {
            report.Add("FAIL " + error);
            throw;
        }
        finally
        {
            File.WriteAllLines(reportPath, report, new UTF8Encoding(false));
            var resolved = Path.GetFullPath(temporary);
            Require(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("RealAtlasCompatibility_", StringComparison.Ordinal), "临时副本清理路径不符");
            Directory.Delete(resolved, true);
        }
    }

    private static void ValidateTransaction(ProjectProfile project, LogicalImageAsset asset, IReadOnlyList<DecodedFrame> frames, List<string> report)
    {
        var before = asset.Frames.Select(frame => frame.SourcePath).Distinct().ToDictionary(path => path, File.ReadAllBytes);
        var history = new HistoryService(Path.Combine(project.RootPath, "history-" + Guid.NewGuid().ToString("N")));
        var mutations = new ImageMutationService(history);
        var plan = new RsImportService().PlanImport(project, asset, frames);
        Require(plan.Files.Count == 3, "真实S回图没有生成三文件事务");
        foreach (var file in plan.Files)
        {
            Require(before.ContainsKey(file.TargetPath) && Path.GetFullPath(file.TargetPath).StartsWith(Path.GetFullPath(project.RootPath) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "计划目标不在独立副本内");
            var original = E5Archive.Parse(file.TargetPath, before[file.TargetPath]);
            var candidate = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
            Require(original.Entries.Count == candidate.Entries.Count && original.ReadStored(241).SequenceEqual(candidate.ReadStored(241)) &&
                original.ReadStored(243).SequenceEqual(candidate.ReadStored(243)), "候选误改物理241/243或条目数");
            report.Add($"    候选 {Path.GetFileName(file.TargetPath)} #242={EncodingOf(candidate.ReadDecoded(242))}");
        }
        var committed = mutations.Commit(project, plan);
        Require(committed.Success && committed.HistoryId != null, "副本三文件提交失败：" + committed.Summary);
        foreach (var file in plan.Files)
        {
            Require(File.ReadAllBytes(file.TargetPath).SequenceEqual(file.CandidateBytes), "写后整文件与候选不一致");
            var disk = E5Archive.OpenFresh(file.TargetPath);
            var old = E5Archive.Parse(file.TargetPath, before[file.TargetPath]);
            Require(disk.ReadStored(241).SequenceEqual(old.ReadStored(241)) && disk.ReadStored(243).SequenceEqual(old.ReadStored(243)),
                "写后邻近物理条目发生改变");
        }
        Require(history.Undo(project, committed.HistoryId!).Success, "真实三文件导入撤回失败");
        foreach (var pair in before) Require(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value), "撤回未恢复完整原文件");
        Require(history.Redo(project, committed.HistoryId!).Success, "真实三文件导入重做失败");
        foreach (var file in plan.Files) Require(File.ReadAllBytes(file.TargetPath).SequenceEqual(file.CandidateBytes), "重做未恢复候选文件");
        Require(history.Undo(project, committed.HistoryId!).Success, "重做后再次撤回失败");
    }

    private static string EncodingOf(byte[] payload)
        => payload.Length > 8 && payload[0] == 137 && payload[1] == 80 ? $"PNG ({payload.Length} bytes)" :
           payload.Length > 2 && payload[0] == 'B' && payload[1] == 'M' ? $"BMP ({payload.Length} bytes)" :
           $"RAW/原生载荷 ({payload.Length} bytes; header={Convert.ToHexString(payload.AsSpan(0, Math.Min(8, payload.Length)))})";

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "形象管理器", "src"))) return directory.FullName;
        throw new DirectoryNotFoundException("请从当前项目目录运行真实基底参考图验收。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
