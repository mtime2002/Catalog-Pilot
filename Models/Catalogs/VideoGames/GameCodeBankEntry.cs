namespace CatalogPilot.Models;

public sealed class GameCodeBankEntry
{
    public string Title { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Franchise { get; init; } = string.Empty;

    public string[] Codes { get; init; } = [];

    public string[] Aliases { get; init; } = [];
}
