using System.Security.Cryptography;

namespace TransformManager.App;

/// <summary>Backup + staged replace + readback for Ekd5.exe. Only the exact file name is accepted.</summary>
public static class TransformSaveTransaction
{
    public static SaveOutcome Commit(string exePath, byte[] beforeBytes, byte[] afterBytes, string summary)
    {
        string path = Path.GetFullPath(exePath);
        if (!string.Equals(Path.GetFileName(path), "Ekd5.exe", StringComparison.OrdinalIgnoreCase))
            return new SaveOutcome(false, "只允许写入 Ekd5.exe。", null);
        if (!File.Exists(path)) return new SaveOutcome(false, "找不到 Ekd5.exe。", null);

        string beforeHash = Hash(beforeBytes);
        string currentHash = Hash(File.ReadAllBytes(path));
        if (!string.Equals(beforeHash, currentHash, StringComparison.OrdinalIgnoreCase))
            return new SaveOutcome(false, "文件在载入后被其他程序修改，请重新扫描。", null);

        string afterHash = Hash(afterBytes);
        if (string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
            return new SaveOutcome(true, "没有需要写入的修改。", null);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
        string backup = path + ".bak-transform-" + stamp;
        string staged = path + ".transform-stage-" + stamp;
        try
        {
            File.Copy(path, backup, overwrite: false);
            if (!string.Equals(Hash(File.ReadAllBytes(backup)), beforeHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("备份复读不一致。");

            File.WriteAllBytes(staged, afterBytes);
            if (!string.Equals(Hash(File.ReadAllBytes(staged)), afterHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("暂存文件复读不一致。");

            File.Move(staged, path, overwrite: true);
            if (!string.Equals(Hash(File.ReadAllBytes(path)), afterHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("写入后复读不一致。");

            return new SaveOutcome(true, $"{summary} 已写入，备份：{Path.GetFileName(backup)}", backup);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(backup)) File.Copy(backup, path, overwrite: true);
            }
            catch
            {
                // 保留原始异常信息，回滚失败另有备份文件可手工恢复。
            }
            return new SaveOutcome(false, "写入失败：" + ex.Message, File.Exists(backup) ? backup : null);
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
