using CatalogPilot.Models;

namespace CatalogPilot.Services;

public sealed class CatalogBackedGameTitleBankService : IGameTitleBankService
{
    private readonly IGameCatalogStore _catalogStore;

    public CatalogBackedGameTitleBankService(IGameCatalogStore catalogStore)
    {
        _catalogStore = catalogStore;
    }

    public async Task<IReadOnlyList<GameTitleMatchResult>> SearchAsync(
        string query,
        string? platformHint = null,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await _catalogStore.InitializeAsync(cancellationToken);
        return await _catalogStore.SearchSimilarTitlesAsync(
            query,
            platformHint,
            Math.Clamp(maxResults, 1, 20),
            cancellationToken);
    }

    public async Task<GameTitleMatchResult?> FindBestMatchAsync(
        string query,
        string? platformHint = null,
        CancellationToken cancellationToken = default)
    {
        var results = await SearchAsync(query, platformHint, 1, cancellationToken);
        return results.Count > 0 ? results[0] : null;
    }
}
