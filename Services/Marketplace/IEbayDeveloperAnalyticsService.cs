using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IEbayDeveloperAnalyticsService
{
    Task<EbayRateLimitResponse> GetAppRateLimitsAsync(
        string? apiName,
        string? apiContext,
        CancellationToken cancellationToken = default);

    Task<EbayRateLimitResponse> GetUserRateLimitsAsync(
        string? apiName,
        string? apiContext,
        CancellationToken cancellationToken = default);
}
