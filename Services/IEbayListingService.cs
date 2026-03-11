using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IEbayListingService
{
    Task<PublishListingResult> CreateListingAsync(
        ListingInput input,
        ClassificationResult? classification,
        PriceSuggestionResult? pricing,
        CancellationToken cancellationToken = default);
}
