using System.Text.Json;
using System.Text.Json.Nodes;
using DataManager.Core.Binary;

namespace DataManager.Tests;

internal static class OwnedTemporaryWorkspaceTests
{
    public static void Run(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        using var suite = OwnedTemporaryWorkspace.Create(output, "DataManager.OwnedTemporaryWorkspaceTests", output);
        var root = suite.RootPath;
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "source-sentinel.txt");
        File.WriteAllText(sentinel, "untouched source");
        var sourceHash = HashService.Sha256(sentinel);
        try
        {
            var success = OwnedTemporaryWorkspace.Create(root, "success", outside);
            Directory.CreateDirectory(Path.Combine(success.RootPath, "nested"));
            File.WriteAllText(Path.Combine(success.RootPath, "nested", "file.txt"), "temporary");
            success.Complete();
            TestSupport.Assert(Directory.Exists(success.RootPath), "Complete提前删除了finally仍需使用的工作区");
            success.Dispose();
            success.Dispose();
            TestSupport.Assert(success.CleanupSucceeded && !Directory.Exists(success.RootPath), "成功工作区未安全清理");
            results.Add("Complete marks success; disposal cleans nested content and is idempotent.");

            var failed = OwnedTemporaryWorkspace.Create(root, "failed", outside);
            var failureLog = Path.Combine(failed.RootPath, "failure.log");
            File.WriteAllText(failureLog, "diagnostic evidence");
            failed.Dispose();
            TestSupport.Assert(File.Exists(failureLog) && !failed.CleanupSucceeded && failed.RetentionReason != null,
                "未Complete的工作区未保留失败证据");
            TestSupport.Assert(JsonNode.Parse(File.ReadAllText(failed.MarkerPath))!["Status"]!.GetValue<string>() == "failed", "失败状态标记未写入");
            results.Add("An incomplete run retains its files and records failed state.");

            var unknown = Path.Combine(root, "unknown-user-directory");
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "keep.txt"), "unknown");
            AssertThrows<ArgumentException>(() => OwnedTemporaryWorkspace.Create("relative", "owner", outside));
            AssertThrows<ArgumentException>(() => OwnedTemporaryWorkspace.Create(root, "", outside));
            AssertThrows<ArgumentException>(() => OwnedTemporaryWorkspace.Create(root, "owner", "relative-source"));
            TestSupport.Assert(File.Exists(Path.Combine(unknown, "keep.txt")), "未知目录被清理");
            results.Add("Relative paths and empty owners are refused; unknown directories are untouched.");

            var tampered = OwnedTemporaryWorkspace.Create(root, "tampered", outside);
            tampered.Complete();
            var marker = File.ReadAllText(tampered.MarkerPath);
            var altered = JsonNode.Parse(marker)!;
            altered["RunId"] = Guid.NewGuid().ToString("N");
            File.WriteAllText(tampered.MarkerPath, altered.ToJsonString());
            tampered.Dispose();
            TestSupport.Assert(Directory.Exists(tampered.RootPath) && !tampered.CleanupSucceeded,
                "所有权已变化的工作区仍被删除");
            results.Add("Changed run identity is refused before any cleanup.");

            var missing = OwnedTemporaryWorkspace.Create(root, "missing-marker", outside);
            missing.Complete();
            File.Delete(missing.MarkerPath);
            missing.Dispose();
            TestSupport.Assert(Directory.Exists(missing.RootPath) && !missing.CleanupSucceeded,
                "缺少标记的未知工作区被删除");
            results.Add("A missing ownership marker prevents cleanup.");

            var linked = OwnedTemporaryWorkspace.Create(root, "linked-child", outside);
            var link = Path.Combine(linked.RootPath, "source-link");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
                linked.Complete();
                linked.Dispose();
                TestSupport.Assert(Directory.Exists(linked.RootPath) && !linked.CleanupSucceeded && File.Exists(sentinel),
                    "带链接工作区未拒绝删除或破坏了链接目标");
                AssertThrows<InvalidDataException>(() => OwnedTemporaryWorkspace.Create(link, "linked-parent", outside));
                Directory.Delete(link, recursive: false);
                results.Add("A linked child prevents all cleanup; linked parents are refused and target bytes survive.");
            }
            catch (Exception error) when (error is UnauthorizedAccessException || error is IOException && !Directory.Exists(link))
            {
                linked.Dispose();
                results.Add("SKIP directory symbolic-link creation is unavailable on this host: " + error.GetType().Name);
            }

            TestSupport.Assert(HashService.Sha256(sentinel) == sourceHash, "安全测试修改了source哨兵文件");
            File.WriteAllText(Path.Combine(output, "owned-temporary-workspace-tests.json"), JsonSerializer.Serialize(new
            {
                Passed = true, SourceUnchanged = true, Checks = results, Workspace = suite.RootPath
            }, new JsonSerializerOptions { WriteIndented = true }));
            suite.Complete();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "owned-temporary-workspace-tests.json"), JsonSerializer.Serialize(new
            {
                Passed = false, SourceUnchanged = File.Exists(sentinel) && HashService.Sha256(sentinel) == sourceHash,
                Checks = results, Workspace = suite.RootPath, Error = error.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            suite.Dispose();
            if (suite.IsCompleted) TestSupport.Assert(suite.CleanupSucceeded, "安全测试自身临时目录未清理：" + suite.RetentionReason);
        }
        Console.WriteLine($"OWNED_TEMPORARY_WORKSPACE_TESTS_OK checks={results.Count}");
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
