using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IGameTitleBankService
{
    Task<IReadOnlyList<GameTitleMatchResult>> SearchAsync(string query, string? platformHint = null, int maxResults = 8, CancellationToken cancellationToken = default);

    Task<GameTitleMatchResult?> FindBestMatchAsync(string query, string? platformHint = null, CancellationToken cancellationToken = default);
}
