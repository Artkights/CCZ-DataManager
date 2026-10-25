namespace DataManager.Core.Migration;

/// <summary>Platform codec verification after all staging resources have been assembled.</summary>
public interface IMigrationResourceVerifier
{
    MigrationResourceVerificationResult ValidateFile(string stagingRoot, string targetRoot, string relativePath,
        byte[] candidate, CancellationToken cancellationToken = default);

    /// <summary>Optional image-codec projection of changed resources onto final target person assignments.</summary>
    IReadOnlyList<DataManager.Core.Appearance.PersonAppearanceUpdate> GetAppearanceUpdates(
        string stagingRoot, string targetRoot, IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default) => [];
}

public sealed record MigrationResourceVerificationResult(bool Verified, string Detail,
    IReadOnlyDictionary<string, string>? DependencyHashes = null);
