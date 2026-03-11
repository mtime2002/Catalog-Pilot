namespace CatalogPilot.Models;

public sealed class EntitlementConsumeResult
{
    public bool Allowed { get; set; }

    public string Message { get; set; } = string.Empty;

    public EntitlementSnapshot Snapshot { get; set; } = new();
}
