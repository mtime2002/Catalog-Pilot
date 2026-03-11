using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IEbayPricingService
{
    Task<PriceSuggestionResult> SuggestPriceAsync(
        ListingInput input,
        ClassificationResult? classification = null,
        CancellationToken cancellationToken = default);
}
