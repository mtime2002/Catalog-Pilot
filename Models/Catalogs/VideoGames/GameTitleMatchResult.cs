namespace CatalogPilot.Models;

public sealed class GameTitleMatchResult
{
    public GameTitleBankEntry Entry { get; init; } = new();

    public decimal Score { get; init; }
}
