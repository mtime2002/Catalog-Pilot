namespace CatalogPilot.Models;

public sealed class ComparableSale
{
    public string ItemId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public decimal SoldPrice { get; init; }

    public decimal ShippingPrice { get; init; }

    public string Currency { get; init; } = "USD";

    public DateTimeOffset? SoldDate { get; init; }

    public string Condition { get; init; } = string.Empty;

    public string ListingUrl { get; init; } = string.Empty;
}
