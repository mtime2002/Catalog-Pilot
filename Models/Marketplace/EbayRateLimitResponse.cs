namespace CatalogPilot.Models;

public sealed class EbayRateLimitResponse
{
    public bool Success { get; init; }

    public string Scope { get; init; } = string.Empty;

    public string ApiName { get; init; } = string.Empty;

    public string ApiContext { get; init; } = string.Empty;

    public DateTimeOffset RetrievedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Message { get; init; } = string.Empty;

    public int? HttpStatusCode { get; init; }

    public string ErrorBody { get; init; } = string.Empty;

    public string RawResponse { get; init; } = string.Empty;

    public IReadOnlyList<EbayRateLimitMetric> Entries { get; init; } = [];
}
