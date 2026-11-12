namespace DataManager.Core.Binary;

/// <summary>
/// A consistent, operation-local view of source files. Never retain this session
/// for saving: conflict detection must inspect the current on-disk files.
/// </summary>
public sealed class SourceFileReadSession
{
    private readonly Dictionary<string, string> _resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceFileMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceFileSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private int _fileReads, _hashComputations, _metadataReads, _directoryReads, _snapshotHits;
    private long _bytesRead;

    public SourceFileReadStatistics Statistics => new(_fileReads, _hashComputations, _metadataReads,
        _directoryReads, _snapshotHits, _bytesRead);

    internal string Resolve(string root, string name)
    {
        var key = Path.GetFullPath(Path.Combine(root, name));
        if (_resolvedPaths.TryGetValue(key, out var path)) return path;
        path = FindInDirectory(root, name) ?? FindInDirectory(Path.Combine(root, "E5"), name)
            ?? Path.Combine(root, name);
        return _resolvedPaths[key] = Path.GetFullPath(path);
    }

    internal string? FindInDirectory(string directory, string name)
    {
        var full = Path.GetFullPath(directory);
        var direct = Path.GetFullPath(Path.Combine(full, name));
        if (GetMetadata(direct).Exists) return direct;
        if (!_directories.TryGetValue(full, out var files))
        {
            _directoryReads++;
            files = Directory.Exists(full) ? Directory.EnumerateFiles(full).ToArray() : [];
            _directories[full] = files;
        }
        return files.FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    internal SourceFileMetadata GetMetadata(string path)
    {
        var full = Path.GetFullPath(path);
        if (_metadata.TryGetValue(full, out var metadata)) return metadata;
        _metadataReads++;
        var info = new FileInfo(full);
        return _metadata[full] = new(info.Exists, info.Exists ? info.Length : 0);
    }

    internal SourceFileSnapshot Read(string path)
    {
        var full = Path.GetFullPath(path);
        if (_snapshots.TryGetValue(full, out var snapshot)) { _snapshotHits++; return snapshot; }
        var bytes = File.ReadAllBytes(full);
        _fileReads++;
        _bytesRead += bytes.LongLength;
        _metadata[full] = new(true, bytes.LongLength);
        _hashComputations++;
        return _snapshots[full] = new(full, bytes, HashService.Sha256(bytes));
    }

    internal readonly record struct SourceFileMetadata(bool Exists, long Length);
    internal sealed record SourceFileSnapshot(string Path, byte[] Bytes, string Hash);
}

public sealed record SourceFileReadStatistics(int FileReads, int HashComputations, int MetadataReads,
    int DirectoryReads, int SnapshotHits, long BytesRead);
