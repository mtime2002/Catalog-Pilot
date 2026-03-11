using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IGameCatalogStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<int> CountTitlesAsync(CancellationToken cancellationToken = default);

    Task<int> CountCuratedTitlesAsync(CancellationToken cancellationToken = default);

    Task<CuratedCatalogRefreshResult> RebuildCuratedCatalogAsync(
        int maxPerPlatform = 2000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CuratedPlatformSummary>> GetCuratedPlatformSummaryAsync(
        int maxPlatforms = 100,
        CancellationToken cancellationToken = default);

    Task UpsertTitlesAsync(IEnumerable<GameTitleBankEntry> entries, string source, CancellationToken cancellationToken = default);

    Task UpsertBarcodeAsync(string code, GameTitleBankEntry entry, string source, decimal confidence, CancellationToken cancellationToken = default);

    Task PromoteTitleToCuratedAsync(
        GameTitleBankEntry entry,
        string source,
        CancellationToken cancellationToken = default);

    Task<CatalogBarcodeMatchResult?> FindByBarcodeAsync(string code, string? platformHint = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameTitleMatchResult>> SearchSimilarTitlesAsync(
        string query,
        string? platformHint = null,
        int maxResults = 8,
        CancellationToken cancellationToken = default);
}
