namespace CatalogPilot.Models;

public sealed class UploadedPhoto
{
    public string FileName { get; init; } = string.Empty;

    public string RelativeUrl { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string ContentType { get; init; } = string.Empty;
}
