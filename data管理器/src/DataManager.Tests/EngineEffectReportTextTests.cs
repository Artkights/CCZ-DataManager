using System.Security.Cryptography;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineEffectReportTextTests
{
    public static void Run(string sourceRoot)
    {
        var fixture = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(fixture);
            var scan = new EngineEffectScanService();
            var source = scan.Scan(workspace);
            var sourceDigest = Digest(source);
            var imsg = File.ReadAllBytes(source.ImsgPath);
            var row = source.Effects.First(item => item.HasDescriptionSlot);
            var modified = (byte[])imsg.Clone();
            var offset = checked((int)row.DescriptionSlotOffset);
            modified.AsSpan(offset, 200).Clear();
            EncodingService.GameEncoding.GetBytes("说明缓存等价回归").CopyTo(modified, offset);

            File.WriteAllBytes(source.ImsgPath, modified);
            var changed = AssertEquivalent("修改说明", source);
            TestSupport.Assert(changed.Effects.Single(item => item.EffectId == row.EffectId).Description == "说明缓存等价回归",
                "修改的说明没有重读。");

            File.Delete(source.ImsgPath);
            var missing = AssertEquivalent("说明文件缺失", changed);
            TestSupport.Assert(missing.ImsgSha256.Length == 0 && missing.Effects.All(item => !item.HasDescriptionSlot),
                "缺失的说明文件仍保留旧槽位或哈希。");

            // Keep the beginning of the description pool and even several complete slots,
            // but truncate the overall pool. The full scan rejects the whole pool too.
            var tooShort = Math.Min(imsg.Length - 1, offset + 201);
            File.WriteAllBytes(source.ImsgPath, imsg.AsSpan(0, tooShort).ToArray());
            var shortened = AssertEquivalent("说明池过短", missing);
            TestSupport.Assert(shortened.Effects.All(item => !item.HasDescriptionSlot),
                "过短说明池错误地保留了部分说明槽。");

            File.WriteAllBytes(source.ImsgPath, imsg);
            var restored = AssertEquivalent("恢复说明", shortened);
            TestSupport.Assert(Digest(restored) == sourceDigest, "恢复原始Imsg后报告未恢复原值。");
            TestSupport.Assert(Digest(source) == sourceDigest, "说明刷新修改了旧报告。");

            var exe = File.ReadAllBytes(source.ExePath);
            exe[^1] ^= 1;
            File.WriteAllBytes(source.ExePath, exe);
            var rejected = false;
            try { EngineEffectReportTextService.RefreshDescriptions(workspace, restored); }
            catch (InvalidDataException) { rejected = true; }
            TestSupport.Assert(rejected, "EXE字节变化后仍允许仅说明刷新。");
            Console.WriteLine($"ENGINE_EFFECT_REPORT_TEXT_TESTS_OK {Path.GetFileName(sourceRoot)}");

            EngineEffectScanReport AssertEquivalent(string label, EngineEffectScanReport previous)
            {
                var before = Digest(previous);
                var refreshed = EngineEffectReportTextService.RefreshDescriptions(workspace, previous);
                TestSupport.Assert(!ReferenceEquals(refreshed, previous) && !ReferenceEquals(refreshed.Effects, previous.Effects),
                    label + "没有创建新的报告/行集合。");
                TestSupport.Assert(ReferenceEquals(previous.RootInfos, refreshed.RootInfos) &&
                    ReferenceEquals(previous.FunctionRoles, refreshed.FunctionRoles) &&
                    ReferenceEquals(previous.DerivedCalls, refreshed.DerivedCalls) &&
                    ReferenceEquals(previous.TableCalls, refreshed.TableCalls) &&
                    ReferenceEquals(previous.LoopCalls, refreshed.LoopCalls) &&
                    ReferenceEquals(previous.InternalCalls, refreshed.InternalCalls) &&
                    ReferenceEquals(previous.UnassignedCalls, refreshed.UnassignedCalls) &&
                    ReferenceEquals(previous.IndirectCalls, refreshed.IndirectCalls) &&
                    ReferenceEquals(previous.TriggerSummaries, refreshed.TriggerSummaries),
                    label + "重新构建了EXE结构证据集合。");
                foreach (var (oldRow, newRow) in previous.Effects.Zip(refreshed.Effects))
                {
                    TestSupport.Assert(ReferenceEquals(oldRow.CallSiteDetails, newRow.CallSiteDetails) &&
                        ReferenceEquals(oldRow.DerivedCallDetails, newRow.DerivedCallDetails) &&
                        ReferenceEquals(oldRow.TableCallDetails, newRow.TableCallDetails) &&
                        ReferenceEquals(oldRow.LoopCallDetails, newRow.LoopCallDetails),
                        label + $"重新构建了特效{oldRow.EffectId}的调用集合。");
                }
                var complete = scan.Scan(workspace);
                TestSupport.Assert(Digest(refreshed) == Digest(complete), label + "报告与完整扫描不完全一致。");
                TestSupport.Assert(Digest(previous) == before, label + "修改了输入报告。");
                return refreshed;
            }
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static string Digest(EngineEffectScanReport report)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new HashStream(hash);
        JsonSerializer.Serialize(stream, report);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Hash every byte of the stable JSON without allocating a 40–60 MB intermediate string.
    private sealed class HashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
