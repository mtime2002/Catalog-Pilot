namespace CatalogPilot.Models;

public sealed class EntitlementSnapshot
{
    public string PlanCode { get; set; } = "free";

    public string SubscriptionStatus { get; set; } = "inactive";

    public int MonthlyListingLimit { get; set; }

    public int MonthlyListingsUsed { get; set; }

    public int MonthlyListingsRemaining { get; set; }

    public bool CanCreateListing { get; set; }

    public bool IsPaidPlan { get; set; }
}
