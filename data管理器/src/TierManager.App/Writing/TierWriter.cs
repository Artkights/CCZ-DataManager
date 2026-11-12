using EngineGrowth.Core;
namespace TierManager.App;
public sealed record TierSaveResult(bool Success, string Message, string? BackupPath);
public static class TierWriter
{
    public static TierSaveResult Apply(TierProfile profile, IReadOnlyList<TierEntry> rows)
    {
        try
        {
            var bytes = File.ReadAllBytes(profile.ExePath);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != profile.ExeSha256)
                throw new InvalidDataException("文件在载入后被修改，请重新扫描。");
            var plan = GrowthPatchCoordinator.Prepare(profile.RootPath, bytes, null, null, rows);
            return TierSaveTransaction.Commit(profile.ExePath, plan.BeforeBytes, plan.AfterBytes, plan.Summary);
        }
        catch (Exception ex) { return new TierSaveResult(false, ex.Message, null); }
    }
}
