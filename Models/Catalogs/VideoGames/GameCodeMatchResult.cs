namespace CatalogPilot.Models;

public sealed class GameCodeMatchResult
{
    public GameCodeBankEntry Entry { get; init; } = new();

    public string MatchedCode { get; init; } = string.Empty;

    public decimal Score { get; init; }
}
