namespace CatalogPilot.Options;

public sealed class EbayDeveloperAnalyticsOptions
{
    public const string SectionName = "EbayDeveloperAnalytics";

    public bool Enabled { get; set; } = true;

    public string IdentityApiBaseUrl { get; set; } = "https://api.ebay.com";

    public string AnalyticsApiBaseUrl { get; set; } = "https://api.ebay.com";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string OAuthScope { get; set; } = "https://api.ebay.com/oauth/api_scope";

    public string DefaultApiName { get; set; } = "inventory";

    public string DefaultApiContext { get; set; } = "sell";

    public string UserOAuthAccessToken { get; set; } = string.Empty;
}
