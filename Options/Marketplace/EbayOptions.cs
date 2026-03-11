namespace CatalogPilot.Options;

public sealed class EbayOptions
{
    public const string SectionName = "Ebay";

    public string FindingApiBaseUrl { get; set; } = "https://svcs.ebay.com/services/search/FindingService/v1";

    public string SellApiBaseUrl { get; set; } = "https://api.ebay.com";

    public string MarketplaceId { get; set; } = "EBAY_US";

    public string Currency { get; set; } = "USD";

    public string PublicBaseUrl { get; set; } = string.Empty;

    public string FindingAppId { get; set; } = string.Empty;

    public string OAuthAccessToken { get; set; } = string.Empty;

    public string FulfillmentPolicyId { get; set; } = string.Empty;

    public string PaymentPolicyId { get; set; } = string.Empty;

    public string ReturnPolicyId { get; set; } = string.Empty;

    public string MerchantLocationKey { get; set; } = string.Empty;
}
