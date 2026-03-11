namespace CatalogPilot.Models;

public sealed class ClassificationResult
{
    public string Source { get; init; } = "Rule-based";

    public string SuggestedTitle { get; init; } = string.Empty;

    public string SuggestedPlatform { get; init; } = string.Empty;

    public string BankMatchedTitle { get; init; } = string.Empty;

    public string BankMatchedPlatform { get; init; } = string.Empty;

    public decimal BankMatchScore { get; init; }

    public string SuggestedCondition { get; init; } = string.Empty;

    public string Franchise { get; init; } = string.Empty;

    public string Edition { get; init; } = string.Empty;

    public string CategoryId { get; init; } = "139973";

    public decimal Confidence { get; init; }

    public Dictionary<string, string> ItemSpecifics { get; init; } = [];
}
