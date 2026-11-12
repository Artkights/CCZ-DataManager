using System.Text.Json;

namespace DataManager.Core.Binary;

/// <summary>
/// An explicitly owned scratch directory. Only a successful run may be removed;
/// failed runs and directories whose identity changed remain available for review.
/// </summary>
public sealed class OwnedTemporaryWorkspace : IDisposable
{
    public const string MarkerFileName = ".ccz-owned-temporary-workspace.json";
    private const string Protocol = "DataManager.OwnedTemporaryWorkspace/v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Marker _identity;
    private bool _completed, _disposed;

    private sealed record Marker(string Protocol, string Owner, string RunId, string Source,
        DateTimeOffset CreatedUtc, string Parent, string Root, string Status);

    private OwnedTemporaryWorkspace(Marker identity) => _identity = identity;

    public string RootPath => _identity.Root;
    public string MarkerPath => Path.Combine(RootPath, MarkerFileName);
    public string RunId => _identity.RunId;
    public string Owner => _identity.Owner;
    public string Source => _identity.Source;
    public DateTimeOffset CreatedUtc => _identity.CreatedUtc;
    public bool IsCompleted => _completed;
    public bool CleanupSucceeded { get; private set; }
    public string? RetentionReason { get; private set; }

    public static OwnedTemporaryWorkspace Create(string parentAbsolutePath, string owner, string sourceAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128) throw new ArgumentException("A nonempty owner of at most 128 characters is required.", nameof(owner));
        var parent = AbsoluteDirectory(parentAbsolutePath);
        var source = AbsoluteDirectory(sourceAbsolutePath);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        AssertNoLinksInAncestors(parent);
        var runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(parent, "ccz-owned-" + runId);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The new workspace path already exists.");
        Directory.CreateDirectory(root);
        var marker = new Marker(Protocol, owner, runId, source, DateTimeOffset.UtcNow, parent, root, "running");
        var result = new OwnedTemporaryWorkspace(marker);
        // CreateNew also refuses an unexpected pre-existing marker. A failed
        // create is deliberately retained instead of deleting an unknown path.
        using (var stream = new FileStream(result.MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, marker, JsonOptions);
        return result;
    }

    /// <summary>Marks success; disposal performs cleanup after the caller's finally work.</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIdentity();
        WriteStatus("complete");
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_completed)
        {
            RetentionReason = "Run did not call Complete; all evidence was retained.";
            try { ValidateIdentity(); WriteStatus("failed"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { RetentionReason += " Marker could not be updated: " + error.Message; }
            return;
        }
        try
        {
            ValidateIdentity();
            var (files, directories) = InventoryWithoutLinks();
            // Recheck the complete ownership preflight before the first delete.
            ValidateIdentity();
            foreach (var file in files.Where(path => !PathEquals(path, MarkerPath)))
            {
                AssertContained(file);
                AssertNoLinksInAncestors(Path.GetDirectoryName(file)!);
                AssertNotLink(file);
                File.Delete(file);
            }
            foreach (var directory in directories.OrderByDescending(path => path.Length))
            {
                AssertContained(directory);
                AssertNoLinksInAncestors(directory);
                Directory.Delete(directory, recursive: false);
            }
            ValidateIdentity();
            File.Delete(MarkerPath);
            Directory.Delete(RootPath, recursive: false);
            CleanupSucceeded = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { RetentionReason = "Cleanup refused or failed; the remaining workspace was retained: " + error.Message; }
    }

    private void WriteStatus(string status)
    {
        AssertNotLink(MarkerPath);
        using var stream = new FileStream(MarkerPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, _identity with { Status = status }, JsonOptions);
    }

    private void ValidateIdentity()
    {
        var root = AbsoluteDirectory(RootPath);
        if (!PathEquals(Path.GetDirectoryName(root)!, _identity.Parent) ||
            !string.Equals(Path.GetFileName(root), "ccz-owned-" + RunId, StringComparison.Ordinal))
            throw new InvalidDataException("Workspace is not the recorded direct child of its parent.");
        AssertNoLinksInAncestors(root);
        AssertNotLink(MarkerPath);
        using var stream = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16384) throw new InvalidDataException("Workspace marker is unexpectedly large.");
        var actual = JsonSerializer.Deserialize<Marker>(stream) ?? throw new InvalidDataException("Workspace marker is empty.");
        var expectedStatus = _completed ? "complete" : "running";
        if (actual != _identity with { Status = expectedStatus })
            throw new InvalidDataException("Workspace owner, run ID, source, path, creation time or state does not match this run.");
    }

    private (List<string> Files, List<string> Directories) InventoryWithoutLinks()
    {
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(RootPath);
        while (pending.TryPop(out var directory))
        {
            AssertNoLinksInAncestors(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                AssertContained(path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A link or reparse point is present: " + path);
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(path); pending.Push(path); }
                else files.Add(path);
            }
        }
        return (files, directories);
    }

    private void AssertContained(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(RootPath + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("Cleanup entry is outside the owned workspace: " + path);
    }

    private static string AbsoluteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An explicit absolute path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void AssertNoLinksInAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
        {
            if (!directory.Exists) throw new DirectoryNotFoundException(directory.FullName);
            AssertNotLink(directory.FullName);
        }
    }

    private static void AssertNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Links and reparse points are not owned temporary directories: " + path);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool PathEquals(string first, string second) => string.Equals(first, second, PathComparison);
}
