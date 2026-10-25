using System.Drawing;

namespace ImageManager.Core;

public enum ImageImportOperation { Append, Replace }

public enum ImageImportOutputFormat { Bmp, Png, Preserve }

/// <summary>A source assigned to an explicit target/action, or to an ordered append group.</summary>
public sealed record ImageImportAssignment(LogicalImageAsset? Target, string Role, string SourcePath,
    int Group = 0, bool Included = true);

public sealed record ImageImportRequest(ProjectProfile Project, ImageResourceDescriptor Resource,
    ImageImportOperation Operation, IReadOnlyList<ImageImportAssignment> Assignments,
    ImageImportOutputFormat? OutputFormat = null);

/// <summary>After is decoded from the encoded candidate, including palette quantization.</summary>
public sealed record ImageImportPreviewRow(string TargetLabel, string Role, string SourcePath,
    Size SourceSize, Size OutputSize, string OriginalFormat, string OutputFormat, string Notes,
    Bitmap? Before, Bitmap? After, string? Error = null) : IDisposable
{
    public string SourceFormat { get; internal set; } = string.Empty;
    public Rectangle? SourceCrop { get; internal set; }

    public void Dispose()
    {
        Before?.Dispose();
        if (!ReferenceEquals(Before, After)) After?.Dispose();
    }
}

internal sealed record ImageImportBuildResult(IReadOnlyList<MutationFile> Files,
    IReadOnlyList<ImageImportPreviewRow> Rows, IReadOnlyList<string> Warnings);

public interface IImageImportPreparationService
{
    Task<PreparedImageImport> PrepareAsync(ImageImportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Owns preview bitmaps. Disposing previews does not invalidate the immutable candidate plan.</summary>
public sealed class PreparedImageImport : IDisposable
{
    private int _disposed;
    public PreparedImageImport(MutationPlan? plan, IReadOnlyList<ImageImportPreviewRow> rows)
    {
        Plan = plan;
        Rows = rows;
    }

    public MutationPlan? Plan { get; }
    public IReadOnlyList<ImageImportPreviewRow> Rows { get; }
    public bool CanCommit => Volatile.Read(ref _disposed) == 0 && Plan is { Files.Count: > 0 } &&
                             Rows.Count > 0 && Rows.All(row => row.Error == null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var row in Rows) row.Dispose();
    }
}
