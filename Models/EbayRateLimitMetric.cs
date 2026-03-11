namespace CatalogPilot.Models;

public sealed class EbayRateLimitMetric
{
    public string Scope { get; init; } = string.Empty;

    public string ApiName { get; init; } = string.Empty;

    public string ApiContext { get; init; } = string.Empty;

    public string Resource { get; init; } = string.Empty;

    public string RateName { get; init; } = string.Empty;

    public long? Limit { get; init; }

    public long? Remaining { get; init; }

    public long? Used { get; init; }

    public long? Count { get; init; }

    public string TimeWindow { get; init; } = string.Empty;

    public DateTimeOffset? Reset { get; init; }
}
