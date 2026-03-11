namespace CatalogPilot.Options;

public sealed class StripeBillingOptions
{
    public const string SectionName = "StripeBilling";

    public bool Enabled { get; set; }

    public string ApiBaseUrl { get; set; } = "https://api.stripe.com";

    public string SecretKey { get; set; } = string.Empty;

    public string WebhookSigningSecret { get; set; } = string.Empty;

    public string MonthlyPriceId { get; set; } = string.Empty;

    public string PaidPlanCode { get; set; } = "pro_monthly";

    public string CheckoutSuccessPath { get; set; } = "/account?billing=success";

    public string CheckoutCancelPath { get; set; } = "/account?billing=cancel";

    public string PortalReturnPath { get; set; } = "/account";
}
