using System.IO.Compression;
using System.Security.Cryptography;

namespace Ccz.HistoryStorage;

public sealed record BlobReference(string Hash, long Length, string Codec = "br-v1");

/// <summary>Immutable, verified, compressed payloads. Existing histories and orphan payloads are never deleted.</summary>
public sealed class BlobStore(string root) : IHistoryBlobStore
{
    public string Root { get; } = Path.GetFullPath(root);
    public BlobReference Put(byte[] bytes) { using var stream = new MemoryStream(bytes, false); return Put(stream); }
    public BlobReference PutFile(string path) { using var stream = File.OpenRead(path); return Put(stream); }
    public BlobReference Put(Stream input)
    {
        Directory.CreateDirectory(Root);
        var temporary = Path.Combine(Root, ".pending-" + Guid.NewGuid().ToString("N"));
        var rawTemporary = temporary + ".raw";
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var raw = new FileStream(rawTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                {
                    var buffer = new byte[65536];
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    { compressed.Write(buffer, 0, count); raw.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); length = checked(length + count); }
                }
                output.Flush(true);
                raw.Flush(true);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            // Reuse an existing valid representation even if its older codec differs.
            foreach (var codec in new[] { "br-v1", "raw-v1" })
            {
                var existing = new BlobReference(digest, length, codec);
                if (File.Exists(GetPath(existing))) { Verify(existing); return existing; }
            }
            var useCompressed = new FileInfo(temporary).Length < length;
            var reference = new BlobReference(digest, length, useCompressed ? "br-v1" : "raw-v1");
            var target = GetPath(reference);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.Move(useCompressed ? temporary : rawTemporary, target, false); }
            catch (IOException) when (File.Exists(target)) { Verify(reference); }
            // Verify the persisted representation, including after a successful rename.
            Verify(reference);
            return reference;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(rawTemporary)) File.Delete(rawTemporary);
        }
    }
    public byte[] Read(BlobReference reference)
    {
        if (reference.Length > int.MaxValue) throw new InvalidDataException("历史载荷过大，必须通过流恢复。");
        using var output = new MemoryStream((int)reference.Length);
        CopyVerified(reference, output);
        return output.ToArray();
    }
    public void Verify(BlobReference reference) => CopyVerified(reference, Stream.Null);
    public void CopyVerified(BlobReference reference, Stream output)
    {
        using var input = File.OpenRead(GetPath(reference));
        using Stream decoded = reference.Codec == "raw-v1" ? input : new BrotliStream(input, CompressionMode.Decompress);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long length = 0; int count;
        while ((count = decoded.Read(buffer)) > 0)
        {
            length = checked(length + count);
            if (length > reference.Length) throw new InvalidDataException("历史压缩载荷超出记录长度。");
            hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
        }
        if (length != reference.Length || Convert.ToHexString(hash.GetHashAndReset()) != reference.Hash)
            throw new InvalidDataException("历史压缩载荷长度或 SHA-256 不一致。");
    }
    public string GetPath(BlobReference reference)
    {
        if (reference.Codec is not ("br-v1" or "raw-v1") || reference.Length < 0 || reference.Hash.Length != 64 ||
            reference.Hash.Any(c => !char.IsAsciiHexDigit(c)) || reference.Hash != reference.Hash.ToUpperInvariant())
            throw new InvalidDataException("历史 blob 引用无效。");
        var path = Path.Combine(Root, reference.Hash[..2], reference.Hash + (reference.Codec == "br-v1" ? ".br" : ".raw"));
        StoragePaths.ValidateInside(path, Root);
        return path;
    }
}

public static class StoragePaths
{
    public static void ValidateInside(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new InvalidDataException("历史路径越界。");
        for (var current = path; !current.Equals(root, comparison); current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("历史路径包含链接。");
    }
}
