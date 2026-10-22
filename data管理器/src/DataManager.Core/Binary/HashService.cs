using System.Security.Cryptography;

namespace DataManager.Core.Binary;

public static class HashService
{
    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
