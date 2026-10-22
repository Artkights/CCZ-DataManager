using DataManager.Core.Binary;

namespace DataManager.Tests;

internal static class ProjectRootLocatorTests
{
    public static void Run()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "DataManagerRoots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var game = Path.Combine(temporary, "外层包装", "实际游戏");
            var resources = Path.Combine(game, "E5");
            Directory.CreateDirectory(resources);
            File.WriteAllBytes(Path.Combine(game, "Ekd5.exe"), [0]);
            File.WriteAllBytes(Path.Combine(resources, "Effect.e5"), new byte[1024]);
            TestSupport.Assert(ProjectRootLocator.Resolve(Path.GetDirectoryName(game)!) == game, "唯一外包装没有定位到实际游戏");
            TestSupport.Assert(ProjectRootLocator.Resolve(resources) == game, "选择E5没有定位到实际游戏");
            TestSupport.Assert(ProjectFileLocator.Resolve(game, "Effect.e5") == Path.Combine(resources, "Effect.e5"), "E5拓展文件回退失败");
            File.WriteAllBytes(Path.Combine(game, "Effect.e5"), new byte[1024]);
            var candidates = ProjectFileLocator.GetCandidates(game, "Effect.e5");
            TestSupport.Assert(candidates.Count == 2 && candidates[0] == Path.Combine(game, "Effect.e5"), "同名文件没有根目录优先且公开冲突");
            var other = Path.Combine(Path.GetDirectoryName(game)!, "另一基底");
            Directory.CreateDirectory(other); File.WriteAllBytes(Path.Combine(other, "Forge.e5"), new byte[64]);
            TestSupport.Assert(ProjectRootLocator.GetCandidates(Path.GetDirectoryName(game)!).Count == 2, "多个基底被静默挑选");
            var rejected = false;
            try { ProjectRootLocator.Resolve(Path.GetDirectoryName(game)!); } catch (InvalidOperationException) { rejected = true; }
            TestSupport.Assert(rejected, "核心解析没有拒绝歧义基底目录");
        }
        finally { Directory.Delete(temporary, true); }
        Console.WriteLine("PROJECT_ROOT_LOCATOR_TESTS_OK");
    }
}
