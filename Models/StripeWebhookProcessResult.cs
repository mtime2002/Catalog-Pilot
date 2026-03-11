namespace CatalogPilot.Models;

public sealed class StripeWebhookProcessResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool Authorized { get; set; } = true;

    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;
}
