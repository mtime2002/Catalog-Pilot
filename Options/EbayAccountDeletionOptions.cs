namespace CatalogPilot.Options;

public sealed class EbayAccountDeletionOptions
{
    public const string SectionName = "EbayAccountDeletion";

    public string EndpointPath { get; set; } = "/api/ebay/account-deletion";

    public string VerificationToken { get; set; } = string.Empty;

    public string PublicEndpointUrl { get; set; } = string.Empty;
}
