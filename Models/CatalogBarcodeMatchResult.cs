namespace CatalogPilot.Models;

public sealed class CatalogBarcodeMatchResult
{
    public string Code { get; init; } = string.Empty;

    public GameTitleMatchResult Match { get; init; } = new();
}
