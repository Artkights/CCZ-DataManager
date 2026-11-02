using ImageManager.Core;

namespace ImageManager.App;

/// <summary>Checks the current project's selected target before expensive atlas processing.</summary>
public static class SReferenceTargetGuard
{
    public static void Validate(ProjectProfile project, LogicalImageAsset asset)
    {
        if (project.IsReadOnly) throw new InvalidOperationException("当前项目只读，不能导入参考图。");
        if (asset.Kind != ResourceKind.SComposite) return;
        SResourceExchangeNaming.ValidateFrameSet(asset);
        var physical = SResourceExchangeNaming.PhysicalId(asset);
        if (asset.PhysicalImageNumber is { } declared && declared != physical)
            throw new InvalidDataException("当前 S 物理编号与动作帧绑定不一致，请重新加载图库。");
        var counts = new List<int>();
        foreach (var name in new[] { "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" })
        {
            if (File.Exists(Path.Combine(project.RootPath, name)) && File.Exists(Path.Combine(project.RootPath, "E5", name)))
                throw new InvalidDataException($"根目录与 E5 目录同时存在 {name}，请先确认并保留正确的资源来源。");
            var active = project.TryGetResource(name);
            var frames = asset.Frames.Where(frame => frame.SourceKey.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (active == null || !File.Exists(active) || frames.Length == 0 || frames.Any(frame =>
                !Path.GetFullPath(frame.SourcePath).Equals(Path.GetFullPath(active), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Unit #{physical} 的 {name} 不属于当前项目有效资源，请重新加载图库。");
            var archive = E5Archive.OpenFresh(active);
            counts.Add(archive.Entries.Count);
            if (physical > archive.Entries.Count)
                throw new InvalidDataException($"{name} 只有 {archive.Entries.Count} 张，找不到 Unit #{physical}。");
            _ = archive.ReadDecoded(physical);
        }
        if (counts.Distinct().Count() != 1)
            throw new InvalidDataException("Unit_mov/atk/spc 条目数量不一致，请先修复资源组，再导入参考图。" +
                $"当前数量：mov={counts[0]}，atk={counts[1]}，spc={counts[2]}。");
    }
}
