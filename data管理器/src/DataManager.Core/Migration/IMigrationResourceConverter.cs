namespace DataManager.Core.Migration;

/// <summary>Optional platform adapter. Conversion is performed only inside the disposable staging project.</summary>
public interface IMigrationResourceConverter
{
    bool CanConvert(string sourceRelativePath, string targetRelativePath);
    byte[] Convert(string sourcePath, string targetTemplatePath,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default);
}
