namespace CatalogPilot.Models;

public sealed class PriceSuggestionResult
{
    public decimal SuggestedPrice { get; init; }

    public string Currency { get; init; } = "USD";

    public string Strategy { get; init; } = string.Empty;

    public IReadOnlyList<ComparableSale> ComparableSales { get; init; } = [];
}
