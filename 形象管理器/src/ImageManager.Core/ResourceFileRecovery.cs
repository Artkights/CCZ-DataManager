namespace ImageManager.Core;

internal static class ResourceFileRecovery
{
    // Continue after a failed restore so one locked file cannot prevent other
    // members of the resource transaction from being recovered.
    public static IReadOnlyList<string> RestoreAll(IEnumerable<HistoryTarget> targets, bool useAfter)
    {
        var errors = new List<string>();
        foreach (var target in targets.Reverse())
        {
            try { Restore(target, useAfter); }
            catch (Exception ex) { errors.Add("恢复失败：" + target.TargetPath + "；" + ex.Message); }
        }
        return errors;
    }

    public static void Restore(HistoryTarget target, bool useAfter)
    {
        var shouldExist = useAfter ? target.AfterExists : target.BeforeExists;
        var hash = useAfter ? target.AfterSha256 : target.BeforeSha256;
        var length = useAfter ? target.AfterLength : target.BeforeLength;
        var attributes = useAfter ? target.AfterAttributes : target.BeforeAttributes;
        var current = MutationSourceSnapshot.Capture(target.TargetPath);
        if (current.Exists == shouldExist && (!shouldExist || current.Length == length && current.Sha256 == hash))
        {
            if (shouldExist && attributes.HasValue) File.SetAttributes(target.TargetPath, attributes.Value);
            E5Archive.Invalidate(target.TargetPath);
            return;
        }
        var temporary = target.TargetPath + ".imagemanager.restore." + Guid.NewGuid().ToString("N");
        try
        {
            if (shouldExist)
            {
                var source = useAfter ? target.AfterSnapshotPath : target.SnapshotPath;
                Verify(source, true, length, hash);
                File.Copy(source, temporary, false);
                Verify(temporary, true, length, hash);
            }
            if (File.Exists(target.TargetPath))
            {
                var currentAttributes = File.GetAttributes(target.TargetPath);
                if ((currentAttributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(target.TargetPath, currentAttributes & ~FileAttributes.ReadOnly);
            }
            if (shouldExist)
            {
                File.Move(temporary, target.TargetPath, true);
                if (attributes.HasValue) File.SetAttributes(target.TargetPath, attributes.Value);
            }
            else if (File.Exists(target.TargetPath)) File.Delete(target.TargetPath);
            Verify(target.TargetPath, shouldExist, length, hash);
            if (shouldExist && attributes.HasValue && File.GetAttributes(target.TargetPath) != attributes.Value)
                throw new IOException("恢复后文件属性校验失败：" + target.TargetPath);
        }
        finally
        {
            E5Archive.Invalidate(target.TargetPath);
            TryDelete(temporary);
        }
    }

    public static void Verify(string path, bool exists, long length, string hash)
    {
        var actual = MutationSourceSnapshot.Capture(path);
        if (actual.Exists != exists || exists && (actual.Length != length || actual.Sha256 != hash))
            throw new IOException("文件存在性、长度或 SHA-256 校验失败：" + path);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
