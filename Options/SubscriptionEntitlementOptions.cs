namespace CatalogPilot.Options;

public sealed class SubscriptionEntitlementOptions
{
    public const string SectionName = "SubscriptionEntitlements";

    public int FreeMonthlyListingLimit { get; set; } = 25;
}
