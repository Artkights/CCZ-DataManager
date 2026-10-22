using System.Drawing;

namespace ImageManager.Core;

/// <summary>Prepares file imports without writing project files or history.</summary>
public sealed class ImageImportPreparationService : IImageImportPreparationService
{
    public static bool Supports(ImageResourceDescriptor resource)
        => resource.Kind is ResourceKind.RComposite or ResourceKind.SComposite or ResourceKind.FaceComposite or ResourceKind.DllBitmap
            or ResourceKind.WeatherSpecial or ResourceKind.PmapSpecial or ResourceKind.MapPair ||
           resource.Kind == ResourceKind.E5Indexed && resource.Category != ImageCategory.StrategyAnimation &&
           !resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase);

    public Task<PreparedImageImport> PrepareAsync(ImageImportRequest request, CancellationToken cancellationToken = default)
    {
        // A caller may keep editing its mapping list while an obsolete preparation is being cancelled.
        var frozen = request with { Assignments = request.Assignments.ToArray() };
        return Task.Run(() => Prepare(frozen, cancellationToken), cancellationToken);
    }

    private static PreparedImageImport Prepare(ImageImportRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ImageImportBuildResult? built = null;
        try
        {
            if (request.Project.IsReadOnly || !request.Resource.CanMutate)
                throw new InvalidOperationException("当前项目或资源为只读，不能导入图片。");
            if (!Supports(request.Resource))
                throw new InvalidOperationException("该资源具有专用结构，请使用对应的导入入口。");
            var included = request.Assignments.Where(item => item.Included).ToArray();
            if (included.Length == 0) throw new InvalidOperationException("没有勾选待导入素材。");
            if (request.Operation == ImageImportOperation.Replace && included.Any(item => item.Target == null))
                throw new InvalidOperationException("替换素材尚未分配目标，请先完成映射。");
            if (included.Any(item => item.Target != null && !item.Target.ResourceKey.Equals(request.Resource.Key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("素材目标与当前资源不一致，请重新选择目标。");
            var targetPaths = request.Resource.Paths.Concat(included.Where(item => item.Target != null)
                .SelectMany(item => item.Target!.Frames.Select(frame => frame.SourcePath)))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var sourcePaths = included.Select(item => item.SourcePath).Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (sourcePaths.Intersect(targetPaths, StringComparer.OrdinalIgnoreCase).Any())
                throw new InvalidOperationException("素材文件不能同时是本次写入的资源档案。");
            var specialDependencies = request.Resource.Kind == ResourceKind.MapPair && request.Project.TryGetResource("Hexzmap.e5") is { } hex
                ? new[] { hex } : Array.Empty<string>();
            if (sourcePaths.Intersect(specialDependencies, StringComparer.OrdinalIgnoreCase).Any())
                throw new InvalidOperationException("素材文件不能同时是本次导入依赖的地形档案。");
            var snapshot = MutationPlanningSnapshot.Capture(targetPaths.Concat(sourcePaths).Concat(specialDependencies).Concat(PaletteDependencies(request.Project)));
            token.ThrowIfCancellationRequested();
            request = request with { Assignments = included };
            built = request.Resource.Kind switch
            {
                ResourceKind.RComposite or ResourceKind.SComposite => RsImageFileImport.Build(request, token),
                ResourceKind.WeatherSpecial or ResourceKind.PmapSpecial or ResourceKind.MapPair => SpecialImageFileImport.Build(request, token),
                _ => RasterImageFileImport.Build(request, token)
            };
            token.ThrowIfCancellationRequested();
            var sourceDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in built.Rows.Where(row => !string.IsNullOrWhiteSpace(row.SourcePath)))
            {
                token.ThrowIfCancellationRequested();
                if (!sourceDescriptions.TryGetValue(row.SourcePath, out var description))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(row.SourcePath);
                        description = ImageImportSourceDecoder.Describe(bytes);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    { description = "无法解码"; }
                    sourceDescriptions[row.SourcePath] = description;
                }
                row.SourceFormat = description;
            }
            if (built.Rows.Count == 0) throw new InvalidDataException("未生成任何图片导入结果。");
            if (built.Rows.Any(row => row.Error != null)) return new PreparedImageImport(null, built.Rows);
            if (built.Files.Count == 0) throw new InvalidDataException("没有可写入的候选资源。");
            if (built.Files.Select(file => Path.GetFullPath(file.TargetPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != built.Files.Count)
                throw new InvalidDataException("候选计划包含重复目标档案。");
            if (built.Files.Any(file => !targetPaths.Contains(Path.GetFullPath(file.TargetPath), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("候选计划包含映射范围之外的目标档案。");
            var plan = new MutationPlan(Guid.NewGuid().ToString("N"),
                request.Operation == ImageImportOperation.Append ? MutationKind.Append : MutationKind.Replace,
                $"{(request.Operation == ImageImportOperation.Append ? "新增" : "替换")}图片：{built.Rows.Count} 项转换结果",
                built.Files, built.Warnings, false,
                new Dictionary<string, string>
                {
                    ["ImportProtocol"] = "raster-file-v1",
                    ["Targets"] = string.Join("；", built.Rows.Select(row => row.TargetLabel).Distinct()),
                    ["Sources"] = string.Join("；", sourcePaths),
                    ["Conversion"] = string.Join("；", built.Rows.Select(row => row.OutputFormat + ":" + row.Notes).Distinct())
                });
            // Sources and palettes join the normal commit-time dependency checks. The bytes displayed
            // in preview are exactly the bytes handed to the transaction, without another conversion.
            return new PreparedImageImport(snapshot.Complete(plan), built.Rows);
        }
        catch (OperationCanceledException)
        {
            if (built != null) foreach (var row in built.Rows) row.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (built != null) foreach (var row in built.Rows) row.Dispose();
            return new PreparedImageImport(null,
                [new ImageImportPreviewRow(request.Resource.DisplayName, "", "", Size.Empty, Size.Empty,
                    "", "", "", null, null, ex.Message)]);
        }
    }

    private static IEnumerable<string> PaletteDependencies(ProjectProfile project)
    {
        // Capture missing candidates as well, so introducing a higher priority palette invalidates
        // an already prepared RAW candidate just like editing an existing palette does.
        return new[]
        {
            project.TryGetResource("Spalet.e5"), Path.Combine(project.RootPath, "Spalet.e5"),
            Path.Combine(project.RootPath, "E5", "Spalet.e5"), Path.Combine(project.RootPath, "tsb"),
            Path.Combine(project.RootPath, "Assets", "Palettes", "tsb"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Palettes", "tsb")
        }.Where(path => path != null).Cast<string>().Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
