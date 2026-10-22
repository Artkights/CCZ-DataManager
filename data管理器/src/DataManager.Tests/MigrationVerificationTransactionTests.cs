using DataManager.Core.Migration;

namespace DataManager.Tests;

internal static class MigrationVerificationTransactionTests
{
    public static void Run(string basePath)
    {
        var source = TestSupport.CreateCoreCopy(basePath);
        var target = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var sourcePath = Path.Combine(source, "Logo.avi"); var targetPath = Path.Combine(target, "Logo.avi");
            File.WriteAllBytes(sourcePath, [1, 2, 3]); File.WriteAllBytes(targetPath, [4, 5, 6]);
            var verifier = new Verifier();
            var service = new MigrationService(verifier);
            var plan = service.Preview(source, target);
            var result = service.Apply(plan, includeAllWritable: true);
            TestSupport.Assert(!result.Success && verifier.Calls == 2 && File.ReadAllBytes(targetPath).SequenceEqual(new byte[] { 4, 5, 6 }),
                "迁移目标语义复读失败未恢复真实目标");
            verifier = new Verifier { FailOnCall = 1 }; service = new MigrationService(verifier);
            result = service.Apply(service.Preview(source, target), includeAllWritable: true);
            TestSupport.Assert(!result.Success && verifier.Calls == 1 && File.ReadAllBytes(targetPath).SequenceEqual(new byte[] { 4, 5, 6 }),
                "迁移候选语义失败修改了目标");
            verifier = new Verifier { FailOnCall = int.MaxValue }; service = new MigrationService(verifier);
            result = service.Apply(service.Preview(source, target), includeAllWritable: true);
            TestSupport.Assert(result.Success && verifier.Calls == 2 && result.Messages.Any(message => message.Contains("验证边界")),
                "迁移未知类别未明确语义验证边界");
        }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }

    private sealed class Verifier : IMigrationResourceConverter, IMigrationResourceVerifier
    {
        public int Calls { get; private set; }
        public int FailOnCall { get; init; } = 2;
        public bool CanConvert(string sourceRelativePath, string targetRelativePath) => false;
        public byte[] Convert(string sourcePath, string targetTemplatePath, IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public MigrationResourceVerificationResult ValidateFile(string stagingRoot, string targetRoot, string relativePath, byte[] candidate, CancellationToken cancellationToken = default)
        {
            if (++Calls == FailOnCall) throw new InvalidDataException("测试迁移资源语义失败");
            return new(false, "测试未知资源：仅核对字节快照");
        }
    }
}
