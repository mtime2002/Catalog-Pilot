namespace CatalogPilot.Models;

public sealed class UserSubscriptionRecord
{
    public Guid UserId { get; set; }

    public string PlanCode { get; set; } = "free";

    public string Status { get; set; } = "inactive";

    public string? StripeSubscriptionId { get; set; }

    public string? StripeCustomerId { get; set; }

    public DateTimeOffset? CurrentPeriodEndUtc { get; set; }

    public bool CancelAtPeriodEnd { get; set; }

    public DateTimeOffset? TrialEndUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
