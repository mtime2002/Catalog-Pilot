namespace CatalogPilot.Models;

public sealed class CuratedCatalogRefreshResult
{
    public bool Success { get; init; }

    public int MaxPerPlatform { get; init; }

    public int PhysicalCandidates { get; init; }

    public int EligibleCandidates { get; init; }

    public int CuratedTitles { get; init; }

    public int PlatformsIncluded { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset FinishedUtc { get; init; }

    public string Message { get; init; } = string.Empty;
}
