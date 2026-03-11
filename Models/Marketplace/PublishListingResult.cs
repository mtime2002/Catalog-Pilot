namespace CatalogPilot.Models;

public sealed class PublishListingResult
{
    public bool Success { get; init; }

    public bool DraftOnly { get; init; }

    public string Message { get; init; } = string.Empty;

    public string ListingId { get; init; } = string.Empty;

    public string PayloadPreviewJson { get; init; } = string.Empty;
}
