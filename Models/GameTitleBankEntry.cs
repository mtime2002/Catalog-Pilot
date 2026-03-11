namespace CatalogPilot.Models;

public sealed class GameTitleBankEntry
{
    public string Title { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Franchise { get; init; } = string.Empty;

    public string[] Aliases { get; init; } = [];
}
