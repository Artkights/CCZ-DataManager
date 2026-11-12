using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RangeManager.App;

/// <summary>
/// Headless verification used by the release gate. It never edits the real
/// bases: every write test runs on a private copy of Ekd5.exe and restores the
/// copy byte-for-byte afterwards. Data.e5 is not read or written.
/// </summary>
public static class SelfTest
{
    private sealed record Check(string Name, bool Pass, string Detail);
    private sealed record BaseResult(string Root, string Version, string ExeSha, bool Pass, List<Check> Checks);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static int Run(string[] args)
    {
        Attach();
        string mode = args[0].ToLowerInvariant();
        if (mode == "--launch-smoke" && args.Length >= 2)
            return LaunchSmoke(args[1]);
        var roots = new List<string>();
        if (mode == "--self-test-all")
        {
            string? baseDir = FindBaseDirectory();
            if (baseDir == null)
            {
                Console.WriteLine("FAIL 找不到 基底 目录");
                return 2;
            }
            // The release gate is scoped to the three verified engines; other
            // bases in the workspace may be different revisions and are not
            // expected to share these addresses.
            foreach (var name in new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" })
            {
                var dir = Path.Combine(baseDir, name);
                if (File.Exists(Path.Combine(dir, "Ekd5.exe")))
                    roots.Add(dir);
            }
            if (roots.Count == 0)
            {
                foreach (var dir in Directory.GetDirectories(baseDir))
                {
                    if (File.Exists(Path.Combine(dir, "Ekd5.exe")))
                        roots.Add(dir);
                }
            }
        }
        else if (args.Length >= 2)
        {
            roots.AddRange(args.Skip(1));
        }
        else
        {
            Console.WriteLine("用法：--self-test <基底目录> | --self-test-all");
            return 2;
        }

        var results = new List<BaseResult>();
        foreach (var root in roots)
        {
            Console.WriteLine($"== {root} ==");
            try
            {
                var result = TestBase(root);
                results.Add(result);
                foreach (var check in result.Checks)
                    Console.WriteLine($"{(check.Pass ? "PASS" : "FAIL")}  {check.Name}：{check.Detail}");
                Console.WriteLine(result.Pass ? "BASE PASS" : "BASE FAIL");
            }
            catch (Exception ex)
            {
                results.Add(new BaseResult(root, "?", "", false, [new Check("加载基底", false, ex.ToString())]));
                Console.WriteLine("FAIL  加载基底：" + ex);
            }
        }

        bool pass = results.All(r => r.Pass);
        WriteEvidence(results, pass);
        Console.WriteLine(pass ? "SELF_TEST_OK" : "SELF_TEST_FAILED");
        return pass ? 0 : 1;
    }

    private static BaseResult TestBase(string root)
    {
        string exePath = Path.Combine(root, "Ekd5.exe");
        string exeSha = Sha(exePath);
        var checks = new List<Check>();

        using var game = new GameBase(root);
        checks.Add(new Check("版本识别", true, game.VersionText));
        checks.Add(new Check("穿透读取指令", game.Exe.Match(game.PenetrationLoadVa, 0x8A, 0x80, 0x81, 0x78, 0x40, 0x00),
            $"0x{game.PenetrationLoadVa:X8} mov al,[eax+407881h]"));
        checks.Add(new Check("范围分派指令", game.Exe.Match(game.RangeDispatchVa, 0x8B, 0x14, 0x85, 0x00, 0x14, 0x4D, 0x00),
            $"0x{game.RangeDispatchVa:X8} mov edx,[eax*4+4D1400h]"));
        checks.Add(new Check("兵种名称", game.JobNames.Count(n => !n.StartsWith("兵种 ")) >= 60,
            $"{game.JobNames.Count(n => !n.StartsWith("兵种 "))}/80"));

        var shapes = new RangeShapeTable(game);
        checks.Add(new Check("形状库 36 项", shapes.Rows.Count == 36, $"{shapes.Rows.Count}"));
        checks.Add(new Check("形状段解析", shapes.Rows.All(r => game.Exe.TryVaToOffset(r.PointerVa, out _)), "全部指针可映射"));
        checks.Add(new Check("形状段终止符", shapes.Rows.All(r => ReadSegmentOk(game.Exe, r.PointerVa)), "每段都能读到 FFFF"));
        checks.Add(new Check("安全扩展区", shapes.ArenaTotalBytes > 0, shapes.ArenaText));

        var cross = ShapeGeometry.ExpandMirror([new CellOffset(1, 0)]);
        bool crossOk = cross.Count == 4 &&
            cross.All(c => (c.Dx == 0) != (c.Dy == 0)) &&
            cross.Contains(new CellOffset(0, -1)) && cross.Contains(new CellOffset(1, 0)) &&
            cross.Contains(new CellOffset(0, 1)) && cross.Contains(new CellOffset(-1, 0));
        checks.Add(new Check("镜像展开 1,0", crossOk, $"{cross.Count} 格：{string.Join(";", cross.Select(c => $"{c.Dx},{c.Dy}"))}"));

        var ring = ShapeGeometry.ExpandMirror([new CellOffset(1, 0), new CellOffset(1, 1)]);
        checks.Add(new Check("镜像展开 1,0;1,1", ring.Count == 8, $"{ring.Count} 格"));

        var reverse = ShapeGeometry.ToGenerator(new CellOffset(-2, 0));
        checks.Add(new Check("展开格反推生成元", reverse is { Dx: 0, Dy: 2 }, $"{reverse?.Dx},{reverse?.Dy}"));

        // Copy for destructive tests.
        string tempRoot = Path.Combine(Path.GetTempPath(), "range-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string tempExe = Path.Combine(tempRoot, "Ekd5.exe");
        File.Copy(exePath, tempExe);
        byte[] pristineExe = File.ReadAllBytes(tempExe);

        try
        {
            // 1) penetration byte
            byte penOriginal, penNew;
            {
                using var g = new GameBase(tempRoot);
                penOriginal = g.Exe.ReadByte(GameBase.PenetrationTableVa);
                penNew = penOriginal == 1 ? (byte)2 : (byte)1;
                if (penNew > g.MaxPhysicalShapeId) penNew = 0;
                new JobPenetrationTable(g).Set(0, penNew);
                g.SaveAll();
                using var verify = new GameBase(tempRoot);
                byte actual = verify.Exe.ReadByte(GameBase.PenetrationTableVa);
                checks.Add(new Check("兵种穿透写入/复读", actual == penNew, $"{penOriginal} → {actual}"));
                Restore(tempExe, pristineExe);
                using var restored = new GameBase(tempRoot);
                checks.Add(new Check("兵种穿透还原", restored.Exe.ReadByte(GameBase.PenetrationTableVa) == penOriginal &&
                    Sha(tempExe) == ShaOf(pristineExe), $"{penOriginal}"));
            }

            // 3) 穿透中心补丁：写入自身中心再还原
            {
                using var g = new GameBase(tempRoot);
                var table = new RangeShapeTable(g);
                var patch = new CenterPatch(g.Exe, table.ArenaBaseVa, table.ArenaEndVa);
                checks.Add(new Check("穿透中心入口", patch.CanApply,
                    $"入口 0x{patch.EntryVa:X8}，蹦床 0x{patch.CaveVa:X8}，{patch.Status}"));
                if (patch.CanApply)
                {
                    var before = new byte[13];
                    for (int i = 0; i < before.Length; i++) before[i] = g.Exe.ReadByte(patch.EntryVa + (uint)i);
                    patch.Apply(PenetrationCenter.Self);
                    bool patched = g.Exe.ReadByte(patch.EntryVa) == 0xE9;
                    patch.Apply(PenetrationCenter.Target);
                    bool restored = true;
                    for (int i = 0; i < before.Length; i++)
                        restored &= g.Exe.ReadByte(patch.EntryVa + (uint)i) == before[i];
                    checks.Add(new Check("穿透中心写入/还原", patched && restored,
                        $"自身中心 {(patched ? "已写入" : "失败")}，目标中心 {(restored ? "已还原" : "未还原")}"));
                }
            }

            // 4) E5 范围图标：读取、重绘、追加、还原
            {
                string sourceE5 = Path.Combine(root, "E5");
                string tempE5 = Path.Combine(tempRoot, "E5");
                if (Directory.Exists(sourceE5))
                {
                    Directory.CreateDirectory(tempE5);
                    foreach (var name in new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" })
                    {
                        string from = Path.Combine(sourceE5, name);
                        if (File.Exists(from)) File.Copy(from, Path.Combine(tempE5, name), overwrite: true);
                    }
                }
                using var images = new E5RangeImages(tempRoot);
                bool readable = images.CanReadAttack && images.CanReadPierce;
                var real = images.Get(true, 1);
                checks.Add(new Check("E5 范围图标读取", readable && real != null && real.Width == 64 && real.Height == 64,
                    $"{images.Summary}；图 1 = {(real == null ? "空" : $"{real.Width}×{real.Height}")}"));
                real?.Dispose();

                if (readable)
                {
                    string attackPath = images.AttackPath;
                    byte[] pristine = File.ReadAllBytes(attackPath);
                    int countBefore = images.AttackCount;
                    bool indexedBefore = images.AttackIsIndexed;
                    var unchangedFiles = new[] { tempExe, images.PiercePath, Path.Combine(tempE5, "Spalet.e5") }
                        .Where(File.Exists).ToDictionary(path => path, Sha);
                    using var fixedTiles = images.CreateFixedPixelTiles();
                    using (var generated = RangeBitmapRenderer.RenderIconFromTiles(RangeKind.Attack,
                        ShapeGeometry.ExpandMirror([new CellOffset(1, 0), new CellOffset(1, 1)]), null, fixedTiles))
                    {
                        images.SetIcon(true, 1, generated);
                        bool modified = images.IsIconModified(true, 1);
                        images.RestoreIcon(true, 1);
                        bool back = !images.IsIconModified(true, 1);
                        checks.Add(new Check("E5 图标改写/还原", modified && back,
                            $"改写 {(modified ? "生效" : "失败")}，还原 {(back ? "成功" : "失败")}"));

                        images.SetIcon(true, countBefore, generated);
                        images.RestoreIcon(true, countBefore);
                        bool removedAppend = images.AttackCount == countBefore;
                        images.SetIcon(true, countBefore, generated);
                        images.Save();
                        checks.Add(new Check("E5 保存与追加", !File.ReadAllBytes(attackPath).AsSpan()
                                .SequenceEqual(pristine) && images.AttackCount == countBefore + 1,
                            $"{countBefore} → {images.AttackCount} 张"));
                        checks.Add(new Check("E5 追加图还原", removedAppend,
                            $"追加后撤销：{countBefore + 1} → {countBefore} 张"));
                    }
                    using var reloaded = new E5RangeImages(tempRoot);
                    using var appended = reloaded.Get(true, countBefore);
                    bool reopened = reloaded.AttackCount == countBefore + 1 && appended is { Width: 64, Height: 64 };
                    checks.Add(new Check("E5 复读", reopened, reloaded.Summary));
                    int appendedBytes = reloaded.DecodedLength(true, countBefore);
                    checks.Add(new Check("E5 索引图格式", reloaded.AttackIsIndexed == indexedBefore &&
                        (!reloaded.AttackIsIndexed || appendedBytes == 64 * 64),
                        reloaded.AttackIsIndexed ? $"追加图 {countBefore + 1} = {appendedBytes} 字节（期望 4096）" : "PNG 条目"));
                    checks.Add(new Check("E5 保存保持其他文件不变", unchangedFiles.All(pair => Sha(pair.Key) == pair.Value),
                        "EXE、Effarea.e5、Spalet.e5 SHA-256 保持不变"));
                    if (indexedBefore)
                    {
                        checks.Add(new Check("E5 RAW近色写入/复读", reopened && reloaded.AttackIsIndexed && appendedBytes == 4096,
                            "固定范围模板按原调色板最近颜色写入，追加图可重新读取"));
                    }
                }
            }

            {
                using var g = new GameBase(tempRoot);
                var table = new RangeShapeTable(g);
                int before = table.ArenaRemainingBytes;
                var original = table.Find(1)!.Cells.ToList();
                uint originalPointer = table.Find(1)!.OriginalPointer;
                int capacity = table.InPlaceCapacity(1);
                var target = Enumerable.Range(1, 30).Select(i => new CellOffset(i, 0)).ToList();
                table.SetCells(1, target);
                int afterWrite = table.ArenaRemainingBytes;
                g.SaveAll();

                using var verify = new GameBase(tempRoot);
                var verifyTable = new RangeShapeTable(verify);
                var wrote = verifyTable.Find(1)!;
                bool same = wrote.Cells.Count == target.Count &&
                    wrote.Cells.Zip(target).All(p => p.First.Dx == p.Second.Dx && p.First.Dy == p.Second.Dy);
                checks.Add(new Check("形状库写入/复读", same,
                    $"{original.Count} 段 → {wrote.Cells.Count} 段，指针 0x{wrote.PointerVa:X8}，原地容量 {capacity}"));
                checks.Add(new Check("形状库指针回写", verify.Exe.ReadU32(GameBase.RangeShapeTableVa + 4) == wrote.PointerVa,
                    $"表项1 = 0x{verify.Exe.ReadU32(GameBase.RangeShapeTableVa + 4):X8}"));
                checks.Add(new Check("空白量单调消耗", afterWrite <= before, $"写入前 {before} → 写入后 {afterWrite} 字节"));
                checks.Add(new Check("空白量跨载入一致", verifyTable.ArenaRemainingBytes == afterWrite,
                    $"{afterWrite} → {verifyTable.ArenaRemainingBytes} 字节，base=0x{verifyTable.ArenaBaseVa:X8}，ptr1=0x{verifyTable.Find(1)!.PointerVa:X8}"));

                Restore(tempExe, pristineExe);
                using var restored = new GameBase(tempRoot);
                var restoredTable = new RangeShapeTable(restored);
                var back = restoredTable.Find(1)!;
                checks.Add(new Check("形状库还原", back.Cells.Count == original.Count && back.PointerVa == originalPointer &&
                    Sha(tempExe) == ShaOf(pristineExe), $"{back.Cells.Count} 段，指针 0x{back.PointerVa:X8}"));
                checks.Add(new Check("空白量还原", restoredTable.ArenaRemainingBytes == before,
                    $"{before} → {restoredTable.ArenaRemainingBytes} 字节"));
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        checks.Add(new Check("真实基底未被修改", Sha(exePath) == exeSha, $"EXE {exeSha[..12]}…"));
        return new BaseResult(root, game.VersionText, exeSha, checks.All(c => c.Pass), checks);
    }

    /// <summary>
    /// Copies a base with hard links, applies a benign range edit to the copy,
    /// then starts Ekd5.exe and checks that the process stays alive. This is a
    /// smoke test that the edited PE is still loadable, not a battle test.
    /// </summary>
    private static int LaunchSmoke(string root)
    {
        // Keep the copy on the base's volume so CreateHardLink succeeds and the
        // smoke test does not duplicate hundreds of megabytes.
        string? baseDir = FindBaseDirectory();
        string tempRoot = baseDir != null
            ? Path.Combine(Directory.GetParent(baseDir)!.FullName, "data管理器", ".tmp")
            : Path.GetTempPath();
        Directory.CreateDirectory(tempRoot);
        string temp = Path.Combine(tempRoot, "range-launch-" + Guid.NewGuid().ToString("N"));
        string progressPath = Path.Combine(tempRoot, "launch-progress.log");
        void Progress(string message) =>
            File.AppendAllText(progressPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n", new UTF8Encoding(false));
        var log = new StringBuilder();
        bool pass = false;
        try
        {
            Progress("start " + root);
            Directory.CreateDirectory(temp);
            int copied = 0;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var target = Path.Combine(temp, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Path.GetFileName(file) is "Ekd5.exe" or "Data.e5" || !CreateHardLink(target, file, IntPtr.Zero))
                    File.Copy(file, target, overwrite: true);
                copied++;
            }
            Progress($"copied {copied} files");

            byte penetrationTarget = 1;
            uint expectedPointer = 0;
            byte[] expectedCells = [];
            using (var game = new GameBase(temp))
            {
                Progress("gamebase loaded " + game.VersionText);
                int original = game.Exe.ReadByte(GameBase.PenetrationTableVa);
                penetrationTarget = (byte)(original == 1 ? 2 : 1);
                new JobPenetrationTable(game).Set(0, penetrationTarget);
                var shapes = new RangeShapeTable(game);
                var target = new List<CellOffset> { new(1, 0), new(2, 0), new(3, 0) };
                shapes.SetCells(1, target);
                expectedPointer = shapes.Find(1)!.PointerVa;
                expectedCells = RangeShapeTable.Serialize(target);
                game.SaveAll();
                Progress("saved modified copy");
            }

            Progress("starting Ekd5.exe");
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(temp, "Ekd5.exe"))
            {
                WorkingDirectory = temp,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(info);
            if (process == null) throw new InvalidOperationException("无法启动 Ekd5.exe");
            Progress("started pid=" + process.Id);
            // Give the loader time to map the image, then verify that the
            // modified bytes really are in the running process' image.
            bool memoryOk = false;
            string memoryDetail = "";
            try
            {
                Thread.Sleep(3000);
                process.Refresh();
                if (!process.HasExited && process.MainModule != null)
                {
                    IntPtr moduleBase = process.MainModule.BaseAddress;
                    IntPtr handle = OpenProcess(0x0010, false, process.Id); // PROCESS_VM_READ
                    if (handle != IntPtr.Zero)
                    {
                        try
                        {
                            byte[] pen = new byte[1];
                            ReadProcessMemory(handle, IntPtr.Add(moduleBase, 0x7881), pen, 1, out _);
                            byte[] ptr = new byte[4];
                            ReadProcessMemory(handle, IntPtr.Add(moduleBase, 0xD1400 + 4), ptr, 4, out _);
                            byte[] cells = new byte[expectedCells.Length];
                            ReadProcessMemory(handle, IntPtr.Add(moduleBase, (int)(expectedPointer - 0x00400000)), cells, cells.Length, out _);
                            uint pointer = (uint)(ptr[0] | ptr[1] << 8 | ptr[2] << 16 | ptr[3] << 24);
                            bool penOk = pen[0] == penetrationTarget;
                            bool ptrOk = pointer == expectedPointer;
                            bool cellOk = cells.SequenceEqual(expectedCells);
                            memoryOk = penOk && ptrOk && cellOk;
                            memoryDetail = $"穿透字节={pen[0]}（期望 {penetrationTarget}）、形状指针=0x{pointer:X8}（期望 0x{expectedPointer:X8}）、格子={Convert.ToHexString(cells)}";
                        }
                        finally { CloseHandle(handle); }
                    }
                    else
                    {
                        memoryDetail = "OpenProcess 失败：" + Marshal.GetLastWin32Error();
                    }
                }
            }
            catch (Exception ex)
            {
                memoryDetail = ex.Message;
            }
            log.AppendLine(memoryOk ? $"PASS  运行时内存校验：{memoryDetail}" : $"FAIL  运行时内存校验：{memoryDetail}");

            bool exited = process.WaitForExit(8000);
            Progress("wait finished, exited=" + exited);
            process.Refresh();
            if (exited)
            {
                log.AppendLine($"FAIL  Ekd5.exe 启动后退出，exit={process.ExitCode}");
            }
            else
            {
                pass = memoryOk;
                log.AppendLine($"{(memoryOk ? "PASS" : "FAIL")}  Ekd5.exe 保持运行，窗口标题=“{process.MainWindowTitle}”");
                try { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } catch { }
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("FAIL  " + ex);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
        log.AppendLine(pass ? "LAUNCH_SMOKE_OK" : "LAUNCH_SMOKE_FAILED");
        Console.WriteLine(log.ToString());
        string? evidenceBase = FindBaseDirectory();
        string output = evidenceBase != null
            ? Path.Combine(Directory.GetParent(evidenceBase)!.FullName, "data管理器", "验证", "2026-11-07-攻击穿透范围管理器")
            : Path.Combine(AppContext.BaseDirectory, "验证", "2026-11-07-攻击穿透范围管理器");
        Directory.CreateDirectory(output);
        File.AppendAllText(Path.Combine(output, "launch-smoke.log"),
            $"== {root} @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==\n{log}\n", new UTF8Encoding(false));
        return pass ? 0 : 1;
    }

    private static bool ReadSegmentOk(PeFile exe, uint va)
    {
        if (!exe.TryVaToOffset(va, out _)) return false;
        for (int i = 0; i < 512; i++)
        {
            uint at = va + (uint)(i * 2);
            if (!exe.TryVaToOffset(at, out _)) return false;
            if (exe.ReadU16(at) == 0xFFFF) return true;
        }
        return false;
    }

    private static void Restore(string tempExe, byte[] exe) => File.WriteAllBytes(tempExe, exe);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string? FindBaseDirectory()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "基底");
            if (Directory.Exists(candidate)) return candidate;
        }
        dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "基底");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void WriteEvidence(List<BaseResult> results, bool pass)
    {
        string? baseDir = FindBaseDirectory();
        string output = baseDir != null
            ? Path.Combine(Directory.GetParent(baseDir)!.FullName, "data管理器", "验证", "2026-11-07-攻击穿透范围管理器")
            : Path.Combine(AppContext.BaseDirectory, "验证", "2026-11-07-攻击穿透范围管理器");
        Directory.CreateDirectory(output);
        var payload = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            tool = "攻击/穿透范围管理器 " + (typeof(SelfTest).Assembly.GetName().Version?.ToString() ?? ""),
            pass,
            bases = results.Select(r => new
            {
                r.Root,
                r.Version,
                exeSha256 = r.ExeSha,
                pass = r.Pass,
                checks = r.Checks.Select(c => new { c.Name, c.Pass, c.Detail })
            })
        };
        File.WriteAllText(Path.Combine(output, "self-test.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        var log = new StringBuilder();
        log.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var r in results)
        {
            log.AppendLine($"== {r.Root} ({r.Version}) ==");
            log.AppendLine($"EXE SHA-256: {r.ExeSha}");
            foreach (var c in r.Checks) log.AppendLine($"{(c.Pass ? "PASS" : "FAIL")}  {c.Name}：{c.Detail}");
            log.AppendLine(r.Pass ? "BASE PASS" : "BASE FAIL");
            log.AppendLine();
        }
        log.AppendLine(pass ? "SELF_TEST_OK" : "SELF_TEST_FAILED");
        File.WriteAllText(Path.Combine(output, "self-test.log"), log.ToString(), new UTF8Encoding(false));
    }

    private static void Attach()
    {
        if (!AttachConsole(-1)) AllocConsole();
    }
}
