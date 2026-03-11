namespace CatalogPilot.Models;

public sealed record ExternalBarcodeLookupResult
{
    public string Code { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Franchise { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Brand { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public decimal Confidence { get; init; }
}
