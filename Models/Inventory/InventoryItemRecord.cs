namespace CatalogPilot.Models;

public static class InventoryItemStatuses
{
    public const string Inactive = "inactive";
    public const string Listed = "listed";
}

public sealed class InventoryItemRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Status { get; set; } = InventoryItemStatuses.Inactive;

    public ListingInput Input { get; set; } = new();

    public ClassificationResult? SuggestedClassification { get; set; }

    public PriceSuggestionResult? SuggestedPricing { get; set; }

    public Dictionary<string, string> ManualSpecifics { get; set; } = [];

    public string? ListingId { get; set; }

    public string ListingMessage { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public DateTimeOffset? ListedUtc { get; set; }
}
