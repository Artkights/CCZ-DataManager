using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Appearance;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Migration;
using DataManager.Core.Profiles;
using DataManager.Core.Workbooks;
using DataManager.Images;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.App;

/// <summary>Read-only acceptance checks for an executable copied away from the source checkout.</summary>
internal static class BundleVerification
{
    internal static int Run(string[] args, bool resourcesOnly = false)
    {
        if (args.Length != 3) return 2;
        var reportPath = Path.GetFullPath(args[1]);
        var selectedRoot = Path.GetFullPath(args[2]);
        // Verification artefacts must never be placed in the project being examined.
        if (IsWithin(reportPath, selectedRoot)) return 2;

        var stopwatch = Stopwatch.StartNew();
        var checks = new List<object>();
        var failures = 0;
        var report = new Dictionary<string, object?>
        {
            ["Schema"] = "ccz.bundle-verification.v1",
            ["Timestamp"] = DateTimeOffset.Now,
            ["BuildVersion"] = Application.ProductVersion,
            ["ReadOnly"] = true,
            ["Scope"] = resourcesOnly ? "BundleResourcesAndWorkbooks" : "FullBundle",
            ["Runtime"] = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                Version = Environment.Version.ToString(),
                OS = RuntimeInformation.OSDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Executable = Environment.ProcessPath,
                BaseDirectory = AppContext.BaseDirectory,
                WorkingDirectory = Environment.CurrentDirectory,
                DotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                MultilevelLookup = Environment.GetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP")
            },
            ["NativeRuntimeModules"] = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(module => module.ModuleName.Contains("coreclr", StringComparison.OrdinalIgnoreCase) ||
                                 module.ModuleName.Contains("hostpolicy", StringComparison.OrdinalIgnoreCase))
                .Select(module => new { module.ModuleName, module.FileName }).ToArray(),
            ["SelectedProjectRoot"] = selectedRoot,
            ["Checks"] = checks
        };

        void Check(string name, Func<object> action)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var result = action();
                checks.Add(new { Name = name, Success = true, ElapsedMs = clock.ElapsedMilliseconds, Result = result });
            }
            catch (Exception error)
            {
                failures++;
                checks.Add(new { Name = name, Success = false, ElapsedMs = clock.ElapsedMilliseconds, Error = error.ToString() });
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            Check("嵌入 Profile", () =>
            {
                var registry = new ProfileRegistry();
                var embedded = typeof(ProfileRegistry).Assembly.GetManifestResourceNames()
                    .Where(name => name.EndsWith("ccz-6.5.json", StringComparison.OrdinalIgnoreCase) ||
                                   name.EndsWith("ccz-6.4.json", StringComparison.OrdinalIgnoreCase) ||
                                   name.EndsWith("ccz-6.6.json", StringComparison.OrdinalIgnoreCase))
                    .Select(name => EmbeddedFile(typeof(ProfileRegistry).Assembly, name)).ToArray();
                if (embedded.Length != 3) throw new InvalidDataException("未找到三份内嵌 Profile。");
                var profiles = registry.Profiles.Select(profile => new
                {
                    profile.Id,
                    Variants = profile.Variants.Select(variant => new
                    {
                        variant.Id,
                        Tables = registry.Resolve(profile.Id, variant.Id).Tables.Count
                    }).ToArray()
                }).ToArray();
                return new { EmbeddedFiles = embedded, Profiles = profiles };
            });
            Check("嵌入使用说明", () =>
            {
                var help = BuiltInDocumentation.ReadText();
                if (help.Length < 100) throw new InvalidDataException("内嵌使用说明为空或不完整。");
                return new { Characters = help.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(help))) };
            });
            Check("嵌入旧版调色板", () =>
            {
                var palette = EmbeddedFile(typeof(PaletteService).Assembly, "ImageManager.Core.Palettes.tsb");
                if (palette.Bytes != 1024) throw new InvalidDataException("内嵌 tsb 必须为 256 项 BGRA 调色板。");
                return palette;
            });

            var root = MigrationService.ResolveProjectRoot(selectedRoot);
            if (IsWithin(reportPath, root)) throw new InvalidOperationException("验收报告不能写入样本项目。");
            report["ProjectRoot"] = root;
            var imageProject = new ProjectProfileService().Detect(root) with { IsReadOnly = true };
            var catalogService = new ImageCatalogService();
            IReadOnlyList<ImageResourceDescriptor> catalog = [];
            var monitoredPaths = new[] { "Data.e5", "Star.e5", "Imsg.e5", "Ekd5.exe" }
                .Select(name => ProjectFileLocator.Resolve(root, name)).Where(File.Exists)
                .Concat(Directory.Exists(Path.Combine(root, "SV")) ? Directory.EnumerateFiles(Path.Combine(root, "SV")) : [])
                .Concat(imageProject.ResourcePaths.Values.Where(File.Exists)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var before = monitoredPaths.ToDictionary(path => path, HashFile, StringComparer.OrdinalIgnoreCase);
            report["SourceFileCount"] = before.Count;
            try
            {
                Check("项目物理表只读打开", () =>
                {
                    var workspace = new ProjectWorkspace();
                    workspace.Open(root);
                    if (workspace.Documents.Count == 0 || workspace.IsDirty)
                        throw new InvalidDataException("样本没有可读表，或打开后意外产生修改草稿。");
                    return new
                    {
                        workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId,
                        workspace.ActiveProfile.LayoutRevision, Tables = workspace.Definitions.Count,
                        ReadableTables = workspace.Documents.Count, workspace.IsDirty
                    };
                });
                Check("人物形象原生存档布局与只读同步准备", () =>
                {
                    var workspace = new ProjectWorkspace(); workspace.Open(root);
                    var executable = File.ReadAllBytes(ProjectFileLocator.Resolve(root, "Ekd5.exe"));
                    var layout = SaveAppearanceLayoutProbe.Inspect(executable, workspace.ActiveProfile.ProfileId);
                    if (!layout.Verified) throw new InvalidDataException(layout.Evidence);
                    var updates = new PersonAppearanceUpdate(0,
                        RId: Convert.ToUInt16(workspace.GetDocument("person-r-assign").Data.Rows[0]["R形象ID"]),
                        SId: Convert.ToUInt16(workspace.GetDocument("person-s-assign").Data.Rows[0]["S形象ID"]));
                    var session = SaveAppearanceSyncService.Prepare(root, workspace.ActiveProfile.ProfileId, executable, [updates]);
                    if (session.AppliedFiles.Count != 0) throw new InvalidDataException("只读准备阶段意外出现已写入文件。");
                    return new
                    {
                        layout.Verified, layout.ROffset, layout.SOffset, layout.PrefixLength, layout.Evidence,
                        PreparedGroups = session.PreparedGroups.Select(group => new { group.SaveRelativePath,
                            Files = group.Files.Select(file => new { file.RelativePath, file.ExpectedFileHash }) }).ToArray(),
                        session.Report.UnchangedSaves, session.Report.SkippedSaves, session.Report.Diagnostics,
                        Committed = false
                    };
                });
                Check("精简模块与程序侧历史目录", () =>
                {
                    var workspace = new ProjectWorkspace();
                    workspace.Open(root);
                    if (workspace.ActiveProfile.Modules.Any(module => module.Key == "人物/套装特效") ||
                        workspace.Definitions.Any(table => table.Key is "class-effects" or "personal-effects" or "effect-descriptions"))
                        throw new InvalidDataException("发布程序仍包含已删除模块的入口或专用物理表。");
                    var storage = ProjectHistoryPaths.GetProjectStorageRoot(root);
                    var expectedParent = Path.Combine(AppContext.BaseDirectory,
                        new DirectoryInfo(ProjectHistoryPaths.NormalizeProjectRoot(root)).Name + "_历史记录");
                    if (!Path.GetDirectoryName(storage)!.Equals(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("历史目录没有使用 EXE 同级的项目名称目录。");
                    return new
                    {
                        Modules = workspace.ActiveProfile.Modules.Select(module => module.Key).ToArray(),
                        ProjectName = new DirectoryInfo(root).Name,
                        HistoryRoot = storage,
                        DataHistory = ProjectHistoryPaths.GetDataRoot(root),
                        ImageHistory = ProjectHistoryPaths.GetImageRoot(root),
                        MigrationBackups = ProjectHistoryPaths.GetMigrationRoot(root),
                        BaseDirectory = AppContext.BaseDirectory,
                        WorkingDirectory = Environment.CurrentDirectory
                    };
                });
                Check("业务工作簿依赖与只读往返", () =>
                {
                    var workspace = new ProjectWorkspace();
                    workspace.Open(root);
                    var workbooks = new PageWorkbookService(workspace);
                    var results = new List<object>();
                    foreach (var page in new[] { BusinessPageKind.Persons, BusinessPageKind.Items })
                    {
                        var output = Path.Combine(Path.GetDirectoryName(reportPath)!,
                            Path.GetFileNameWithoutExtension(reportPath) + $"-{page}.xlsx");
                        workbooks.Export(page, output);
                        var preview = workbooks.PreviewImport(page, output);
                        if (preview.ChangedCells != 0 || workspace.IsDirty)
                            throw new InvalidDataException($"{page} 的原样工作簿回导意外产生修改。");
                        results.Add(new { Page = page.ToString(), Path = output, Bytes = new FileInfo(output).Length,
                            preview.ChangedCells, Sha256 = HashFile(output) });
                    }
                    return new { Workbooks = results, workspace.IsDirty };
                });
                Check("图片资源目录", () =>
                {
                    catalog = catalogService.BuildCatalog(imageProject);
                    if (catalog.Count == 0) throw new InvalidDataException("没有发现图片资源。");
                    return new
                    {
                        Version = imageProject.Version.ToString(), Resources = catalog.Count,
                        PreviewableResources = catalog.Count(resource => resource.CanPreview),
                        Kinds = catalog.GroupBy(resource => resource.Kind).ToDictionary(group => group.Key.ToString(), group => group.Count())
                    };
                });
                Check("S 图库物理图号、人物紧凑编号与三阵营第0帧", () => VerifySImages(root, imageProject, catalogService, catalog));
                Check("兵种S标题按详细兵种ID与名称匹配", () =>
                {
                    var workspace = new ProjectWorkspace(); workspace.Open(root);
                    var jobs = workspace.GetDocument("detailed-jobs");
                    var resource = catalog.Single(item => item.Kind == ResourceKind.SComposite);
                    var names = JobSDisplayService.LoadNames(imageProject);
                    var assets = JobSDisplayService.ApplyNames(new SResourceCodec().ReadJobAssets(imageProject, resource), names);
                    foreach (var asset in assets)
                    {
                        var id = asset.JobId!.Value;
                        var name = Convert.ToString(jobs.Data.Rows[id]["名称"])?.Trim();
                        var faction = new[] { "我军", "友军", "敌军" }[asset.FactionIndex!.Value];
                        if (asset.DisplayName != $"{id}：{(string.IsNullOrWhiteSpace(name) ? "未命名" : name)} {faction}")
                            throw new InvalidDataException($"兵种S标题未匹配详细兵种{id}：{asset.DisplayName}");
                    }
                    return new { Jobs = names.Count, PhysicalEntries = assets.Count, FirstLabels = assets.Take(6).Select(asset => asset.DisplayName).ToArray(), LastLabel = assets.LastOrDefault()?.DisplayName };
                });
                Check("图片导入候选与物理242隔离（不提交）", () => VerifyFileImportCandidate(root, reportPath));
                Check("攻击与效果范围逐图解码", () => VerifyRangeImages(root, imageProject, catalogService, catalog));
                if (!resourcesOnly) Check("Weather 四帧纵向动画", () =>
                {
                    var resource = catalog.Single(item => item.Kind == ResourceKind.WeatherSpecial);
                    var asset = catalogService.ReadAssets(imageProject, resource).First();
                    var frames = catalogService.DecodeFrames(imageProject, asset);
                    try
                    {
                        if (frames.Count != 4 || frames.Any(frame => frame.Bitmap.Width != 216 || frame.Bitmap.Height != 50))
                            throw new InvalidDataException("Weather 未按 216×50 的四帧解析。");
                        return new
                        {
                            resource.Key, Path = Path.GetRelativePath(root, resource.Paths.Single()), Frames = frames.Count,
                            FrameSizes = frames.Select(frame => $"{frame.Bitmap.Width}×{frame.Bitmap.Height}").ToArray(),
                            FirstFrameSha256 = HashBitmap(frames[0].Bitmap)
                        };
                    }
                    finally { foreach (var frame in frames) frame.Dispose(); }
                });
                if (!resourcesOnly) Check("Mcall 内嵌真彩 BMP 动画", () =>
                {
                    foreach (var resource in catalog.Where(item => item.Kind == ResourceKind.McallAnimation && item.CanPreview))
                    {
                        using var animation = StrategyAnimationDecoder.Decode(imageProject, File.ReadAllBytes(resource.Paths.Single()));
                        if (animation.PayloadFormat != "BMP" || animation.EncodedImageBytes.Length < 30 ||
                            BitConverter.ToUInt16(animation.EncodedImageBytes, 28) < 24) continue;
                        if (animation.Frames.Count < 2) throw new InvalidDataException("真彩 BMP 动画没有多帧播放序列。");
                        return new
                        {
                            resource.Key, Path = Path.GetRelativePath(root, resource.Paths.Single()),
                            animation.PayloadFormat, Frames = animation.Frames.Count, Planes = animation.PlaneFrames.Count,
                            animation.IntervalMs, animation.Width, animation.Height, animation.Diagnostics,
                            FirstFrameSha256 = HashBitmap(animation.Frames[0])
                        };
                    }
                    throw new InvalidDataException("样本中未找到可验证的内嵌真彩 BMP Mcall 动画。");
                });
                Check("主窗口 1280×720 与 1920×1080", () =>
                {
                    using var form = new MainForm(openDefaultProject: false)
                    {
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-30000, -30000),
                        ShowInTaskbar = false
                    };
                    // The normal app opens projects after Shown. Create the
                    // window first so editors inherit its actual monitor DPI.
                    form.Show();
                    form.LoadProjectForSmokeTest(root);
                    form.SelectModuleForSmokeTest("人物");
                    var screenshots = new List<object>();
                    foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
                    {
                        form.Size = size;
                        if (form.Size != size)
                            SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
                        form.PerformLayout();
                        Application.DoEvents();
                        if (form.Size != size)
                            throw new InvalidDataException($"实际窗口 {form.Size} 未达到要求的 {size}。");
                        if (form.BusinessEditorForSmokeTest.DetailTabs.TabPages.Count == 0)
                            throw new InvalidDataException("人物详情未绑定，不能将空白窗口作为布局验收截图。");
                        var output = Path.Combine(Path.GetDirectoryName(reportPath)!,
                            Path.GetFileNameWithoutExtension(reportPath) + $"-{size.Width}x{size.Height}.png");
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(output, ImageFormat.Png);
                        screenshots.Add(new { Path = output, WindowWidth = form.Width, WindowHeight = form.Height,
                            ClientWidth = form.ClientSize.Width, ClientHeight = form.ClientSize.Height, Sha256 = HashFile(output) });
                    }
                    if (form.WorkspaceForSmokeTest.IsDirty) throw new InvalidDataException("窗口只读验收意外产生修改草稿。");
                    form.Close();
                    return new { Screenshots = screenshots.ToArray(), form.WorkspaceForSmokeTest.IsDirty,
                        Font = form.Font.Name, FontPoints = form.Font.SizeInPoints, form.DeviceDpi,
                        AutoScaleMode = form.AutoScaleMode.ToString() };
                });
            }
            finally
            {
                Check("样本文件 SHA 保持不变", () =>
                {
                    var changed = before.Where(pair => !File.Exists(pair.Key) || HashFile(pair.Key) != pair.Value)
                        .Select(pair => Path.GetRelativePath(root, pair.Key)).ToArray();
                    if (changed.Length > 0) throw new InvalidDataException("只读验收改变样本文件：" + string.Join("、", changed));
                    return new { Files = before.Select(pair => new { Path = Path.GetRelativePath(root, pair.Key), Sha256 = pair.Value }).ToArray(), Unchanged = true };
                });
            }
        }
        catch (Exception error)
        {
            failures++;
            report["FatalError"] = error.ToString();
        }
        report["Success"] = failures == 0;
        report["Failures"] = failures;
        report["ElapsedMs"] = stopwatch.ElapsedMilliseconds;
        try
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), new UTF8Encoding(false));
        }
        catch { return 2; }
        return failures == 0 ? 0 : 1;
    }

    private static object VerifyFileImportCandidate(string root, string reportPath)
    {
        // This exercises the bundled converter against actual resources without calling Commit.
        var project = new ProjectProfileService().Detect(root);
        var catalog = new ImageCatalogService();
        var resource = catalog.BuildCatalog(project).Single(item => item.Kind == ResourceKind.SComposite);
        var asset = catalog.ReadAssets(project, resource).Single(item => item.PhysicalImageNumber == 242);
        var directory = Path.Combine(Path.GetDirectoryName(reportPath)!, "import-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "任意名字.JPEG");
            using (var image = new Bitmap(48, 528, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.Magenta);
                    using var brush = new SolidBrush(Color.FromArgb(255, 180, 40, 25));
                    graphics.FillRectangle(brush, 4, 6, 16, 18);
                }
                image.Save(source, ImageFormat.Png); // Identify by content, not file extension.
            }
            using var prepared = new ImageImportPreparationService().PrepareAsync(new ImageImportRequest(project,
                resource, ImageImportOperation.Replace, [new ImageImportAssignment(asset, "mov", source)])).GetAwaiter().GetResult();
            if (!prepared.CanCommit || prepared.Plan == null)
                throw new InvalidDataException(string.Join("；", prepared.Rows.Select(row => row.Error)));
            var plan = prepared.Plan;
            if (plan.Files.Count != 1 || !Path.GetFileName(plan.Files[0].TargetPath).Equals("Unit_mov.e5", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("仅分配移动条却生成了其它动作档案的写入候选。");
            var before = E5Archive.OpenFresh(plan.Files[0].TargetPath);
            var after = E5Archive.Parse(plan.Files[0].TargetPath, plan.Files[0].CandidateBytes);
            if (before.Entries.Count != after.Entries.Count || !before.Entries.Where(entry => entry.ImageNumber != 242)
                    .All(entry => before.ReadStored(entry.ImageNumber).AsSpan().SequenceEqual(after.ReadStored(entry.ImageNumber))))
                throw new InvalidDataException("图片导入候选改变了物理242以外的条目。");
            var row = prepared.Rows.Single();
            if (row.After?.Size != new Size(48, 528) || !row.SourceFormat.StartsWith("PNG", StringComparison.Ordinal))
                throw new InvalidDataException("导入候选预览或按内容识别失败。");
            return new
            {
                PhysicalNumber = 242, Action = "mov", UnselectedEntriesUnchanged = true,
                row.SourceFormat, row.OriginalFormat, row.OutputFormat, row.Notes,
                CandidateFiles = plan.Files.Count, CandidateFrameSha256 = HashBitmap(row.After),
                Committed = false, Protocol = plan.Metadata?["ImportProtocol"]
            };
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool IsWithin(string file, string root)
        => file.Equals(root, StringComparison.OrdinalIgnoreCase) || file.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static object VerifySImages(string root, ProjectProfile project, ImageCatalogService service,
        IReadOnlyList<ImageResourceDescriptor> catalog)
    {
        var resource = catalog.Single(item => item.Kind == ResourceKind.SComposite);
        var physicalCounts = resource.Paths.ToDictionary(path => Path.GetFileName(path)!,
            path => E5Archive.Open(path).Entries.Count, StringComparer.OrdinalIgnoreCase);
        var commonCount = physicalCounts.Values.Min();
        var persons = service.ReadAssets(project, resource);
        var jobs = new SResourceCodec().ReadJobAssets(project, resource);
        var expectedPersons = Math.Max(0, commonCount - 240);
        if (persons.Count != expectedPersons || jobs.Count != Math.Min(240, commonCount))
            throw new InvalidDataException($"S 图库数量错误：将领 {persons.Count}/{expectedPersons}，兵种槽 {jobs.Count}/{Math.Min(240, commonCount)}。");
        for (var index = 0; index < persons.Count; index++)
            VerifySAsset(persons[index], 241 + index, 241 + index, SImageMode.Person);
        for (var index = 0; index < jobs.Count; index++)
            VerifySAsset(jobs[index], index + 1, index + 1, SImageMode.Job);

        using var adapter = new ResourceCatalogAdapter();
        var options = adapter.GetIdOptions(root, ResourceBindingKind.CharacterS);
        var expectedAssignments = Math.Min(32, Math.Max(0, (commonCount - 240) / 3)) + Math.Max(0, commonCount - 336);
        if (!options.Select(option => option.Id).SequenceEqual(Enumerable.Range(0, expectedAssignments + 1)))
            throw new InvalidDataException("人物 S 下拉没有按默认0及完整资源组的紧凑编号展示。");
        var mapping = new SPhysicalMappingService();
        for (var job = 0; job < 80; job++)
        for (var turn = 1; turn <= 3; turn++)
        {
            var resolved = mapping.Resolve(0, job, turn);
            if (resolved.ImageNumber != job * 3 + 1 || resolved.EffectiveTurn != (job < 60 ? job % 3 + 1 : 1))
                throw new InvalidDataException($"兵种 {job} 的 S 映射未指向自身转职的我军图号 {job * 3 + 1}。");
        }

        var characterFirstFrames = new List<object>();
        var movement = E5Archive.Open(project.TryGetResource("Unit_mov.e5")!);
        foreach (var (sId, imageNumber) in new[] { (1, 241), (2, 244), (32, 334), (33, 337) })
        {
            if (!options.Any(option => option.Id == sId)) continue;
            var bytes = movement.ReadDecoded(imageNumber);
            using var strip = ImageRendering.IsStandardImage(bytes) ? ImageRendering.DecodeStandardImage(bytes) :
                ImageRendering.DecodeIndexed(bytes, 48, 48 * 11, new PaletteService().Load(project));
            using var expected = ImageRendering.Crop(strip, new Rectangle(0, 0, 48, 48));
            for (var turn = 1; turn <= 3; turn++)
            {
                var info = adapter.ResolveBindingPreview(root, ResourceBindingKind.CharacterS, sId, 59, turn);
                using var preview = adapter.DecodeCharacterS(root, sId, 59, turn);
                if (!info.PhysicalImageNumbers.SequenceEqual([imageNumber]) || preview.Frames.Count != 1 ||
                    preview.Frames[0].Size != new Size(48, 48) ||
                    !BmpPayloadService.ReadPixels(preview.Frames[0]).AsSpan().SequenceEqual(BmpPayloadService.ReadPixels(expected)))
                    throw new InvalidDataException($"人物紧凑S{sId}未固定预览Unit_mov #{imageNumber}第一转第0帧。");
            }
            characterFirstFrames.Add(new { SId = sId, ImageNumber = imageNumber, FirstFrameSha256 = HashBitmap(expected) });
        }

        var boundaries = new List<object>();
        var boundaryNumbers = new[] { 1, 2, 3, 4, 7, 10, 178, 180, 181, 183, 238, 240, 241, 242, 243, commonCount }
            .Where(number => number >= 1 && number <= commonCount).Distinct().OrderBy(number => number);
        foreach (var imageNumber in boundaryNumbers)
        {
            var asset = imageNumber <= 240 ? jobs[imageNumber - 1] : persons[imageNumber - 241];
            var frames = service.DecodeFrames(project, asset);
            try
            {
                if (frames.Count != 28 || frames.Any(frame => frame.Bitmap.Width != frame.Descriptor.Width ||
                                                            frame.Bitmap.Height != frame.Descriptor.Height))
                    throw new InvalidDataException($"Unit #{imageNumber} 未完整解码为 mov/atk/spc 的 28 个标准尺寸帧。");
                boundaries.Add(new
                {
                    ImageNumber = imageNumber, asset.LogicalId, Mode = asset.SMode.ToString(), Frames = frames.Count,
                    FirstFrameSha256 = frames.Where(frame => frame.Descriptor.FrameIndex == 0)
                        .ToDictionary(frame => frame.Descriptor.Action, frame => HashBitmap(frame.Bitmap))
                });
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }

        var jobFirstFrames = new List<object>();
        foreach (var job in new[] { 0, 1, 2, 3, 59, 60, 79 })
        for (var faction = 0; faction < 3; faction++)
        {
            var imageNumber = job * 3 + faction + 1;
            if (imageNumber > commonCount) continue;
            using var preview = adapter.DecodeJobSFirstFrame(root, job, faction);
            if (preview.Frames.Count != 1 || preview.Frames[0].Size != new Size(48, 48))
                throw new InvalidDataException($"兵种 {job} 阵营{faction}的预览未仅解码一个48×48第0帧。");
            var bytes = movement.ReadDecoded(imageNumber);
            using var strip = ImageRendering.IsStandardImage(bytes) ? ImageRendering.DecodeStandardImage(bytes) :
                ImageRendering.DecodeIndexed(bytes, 48, 48 * 11, new PaletteService().Load(project));
            using var expected = ImageRendering.Crop(strip, new Rectangle(0, 0, 48, 48));
            if (!BmpPayloadService.ReadPixels(preview.Frames[0]).AsSpan().SequenceEqual(BmpPayloadService.ReadPixels(expected)))
                throw new InvalidDataException($"兵种 {job} 阵营{faction}的预览像素不等于Unit_mov #{imageNumber}第0帧。");
            jobFirstFrames.Add(new { JobId = job, FactionIndex = faction, ImageNumber = imageNumber, FirstFrameSha256 = HashBitmap(preview.Frames[0]) });
        }
        return new
        {
            ArchivePhysicalCounts = physicalCounts, CommonPhysicalCount = commonCount,
            UnpairedTailSlots = physicalCounts.ToDictionary(pair => pair.Key, pair => pair.Value - commonCount),
            JobImageSlots = jobs.Count, CharacterImages = persons.Count, CharacterPickerOptions = options.Count,
            CheckedJobMappings = 80, Boundaries = boundaries, CharacterFirstFrames = characterFirstFrames, JobFirstFrames = jobFirstFrames
        };
    }

    private static void VerifySAsset(LogicalImageAsset asset, int logicalId, int imageNumber, SImageMode mode)
    {
        if (asset.LogicalId != logicalId || asset.PhysicalImageNumber != imageNumber ||
            asset.DisplayName != $"S_{imageNumber:000}" || asset.SMode != mode || asset.IsFixedSlot != (mode == SImageMode.Job) ||
            asset.Frames.Count != 28 || asset.SMapping is not { IsThreeTurn: false, Turns.Count: 1 } mapping ||
            mapping.Turns[0].ImageNumber != imageNumber ||
            asset.Frames.Any(frame => frame.ImageNumber != imageNumber || frame.Turn != 1))
            throw new InvalidDataException($"{mode} S{logicalId} 未与 Unit #{imageNumber} 一一对应。");
        if (mode == SImageMode.Job && (asset.JobId != (imageNumber - 1) / 3 || asset.FactionIndex != (imageNumber - 1) % 3))
            throw new InvalidDataException($"兵种S_{imageNumber:000}的兵种ID或阵营错误。");
        foreach (var (action, key, width, count) in new[]
                 { ("mov", "Unit_mov.e5", 48, 11), ("atk", "Unit_atk.e5", 64, 12), ("spc", "Unit_spc.e5", 48, 5) })
        {
            var frames = asset.Frames.Where(frame => frame.Action == action).ToArray();
            if (!frames.Select(frame => frame.FrameIndex).SequenceEqual(Enumerable.Range(0, count)) ||
                frames.Any(frame => frame.SourceKey != key || frame.Width != width || frame.Height != width))
                throw new InvalidDataException($"Unit #{imageNumber} 的 {action} 帧描述或动作顺序错误。");
        }
    }

    private static object VerifyRangeImages(string root, ProjectProfile project, ImageCatalogService service,
        IReadOnlyList<ImageResourceDescriptor> catalog)
    {
        using var adapter = new ResourceCatalogAdapter();
        var results = new List<object>();
        foreach (var (key, attack) in new[] { ("Hitarea.e5", true), ("Effarea.e5", false) })
        {
            var path = project.TryGetResource(key);
            if (path == null)
            {
                results.Add(new { Resource = key, Skipped = true, Reason = "项目未包含此范围资源。" });
                continue;
            }
            var resource = catalog.SingleOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"存在 {key}，但图片目录没有对应项。");
            if (!resource.CanPreview) throw new InvalidDataException($"{key} 不可预览：{resource.Status}");
            var archive = E5Archive.Open(path);
            var assets = service.ReadAssets(project, resource);
            if (assets.Count != archive.Entries.Count || !assets.Select(asset => asset.LogicalId)
                    .SequenceEqual(Enumerable.Range(1, archive.Entries.Count)))
                throw new InvalidDataException($"{key} 的范围图库未完整按物理图号排序。");
            var boundaries = new List<object>();
            for (var index = 0; index < assets.Count; index++)
            {
                var info = adapter.ResolveRangePreview(root, attack, index);
                if (!info.CanPreview || info.PhysicalImageNumber != index + 1 || info.EntryCount != assets.Count)
                    throw new InvalidDataException($"{key} 字段 {index} 未映射到图号 {index + 1}。");
                var frames = service.DecodeFrames(project, assets[index]);
                try
                {
                    if (frames.Count != 1 || frames[0].Bitmap.Width <= 0 || frames[0].Bitmap.Height <= 0)
                        throw new InvalidDataException($"{key} 图号 {index + 1} 没有成功解码为一张范围图。");
                    using var preview = adapter.DecodeRangeFirstFrameAsync(root, attack, index, new Size(256, 256))
                        .GetAwaiter().GetResult();
                    if (preview == null || preview.Width <= 0 || preview.Height <= 0)
                        throw new InvalidDataException($"{key} 字段 {index} 的业务页范围预览为空。");
                    if (index == 0 || index == assets.Count - 1)
                        boundaries.Add(new
                        {
                            FieldValue = index, PhysicalImageNumber = index + 1,
                            Width = frames[0].Bitmap.Width, Height = frames[0].Bitmap.Height,
                            FirstFrameSha256 = HashBitmap(frames[0].Bitmap)
                        });
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
            if (adapter.ResolveRangePreview(root, attack, -1).CanPreview ||
                adapter.ResolveRangePreview(root, attack, assets.Count).CanPreview)
                throw new InvalidDataException($"{key} 越界字段仍被视为可预览。");
            results.Add(new { Resource = key, Skipped = false, Entries = assets.Count, DecodedEntries = assets.Count, Boundaries = boundaries });
        }
        return results;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string HashBitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static EmbeddedFileResult EmbeddedFile(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("找不到内嵌资源：" + name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new(name, copy.Length, Convert.ToHexString(SHA256.HashData(copy.ToArray())));
    }

    private sealed record EmbeddedFileResult(string Name, long Bytes, string Sha256);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
