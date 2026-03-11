namespace CatalogPilot.Options;

public sealed class AuthStoreOptions
{
    public const string SectionName = "AuthStore";

    public string DatabasePath { get; set; } = "Data/auth-store.db";

    public string CookieName { get; set; } = "CatalogPilot.Auth";

    public List<string> AdminEmails { get; set; } = [];
}
