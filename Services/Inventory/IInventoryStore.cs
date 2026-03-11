using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IInventoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<InventoryItemRecord> AddItemAsync(
        Guid userId,
        ListingInput input,
        ClassificationResult? suggestedClassification,
        PriceSuggestionResult? suggestedPricing,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InventoryItemRecord>> GetItemsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InventoryItemRecord>> GetInactiveItemsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<InventoryItemRecord?> GetItemAsync(
        Guid userId,
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateManualAttributesAsync(
        Guid userId,
        Guid itemId,
        ListingInput input,
        IReadOnlyDictionary<string, string> manualSpecifics,
        CancellationToken cancellationToken = default);

    Task<bool> RecordListingAttemptAsync(
        Guid userId,
        Guid itemId,
        PublishListingResult result,
        CancellationToken cancellationToken = default);
}
