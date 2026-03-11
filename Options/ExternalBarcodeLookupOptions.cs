namespace CatalogPilot.Options;

public sealed class ExternalBarcodeLookupOptions
{
    public const string SectionName = "ExternalBarcodeLookup";

    public bool Enabled { get; set; } = true;

    public int CacheMinutes { get; set; } = 60;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public string UserAgent { get; set; } = "CatalogPilot/1.0";

    public MobyGamesBarcodeOptions MobyGames { get; set; } = new();

    public UpcItemDbOptions UpcItemDb { get; set; } = new();

    public GoUpcOptions GoUpc { get; set; } = new();

    public EanDbOptions EanDb { get; set; } = new();
}

public sealed class MobyGamesBarcodeOptions
{
    public bool Enabled { get; set; } = true;

    public string ApiBaseUrl { get; set; } = "https://api.mobygames.com/v1";

    public string ApiKey { get; set; } = string.Empty;

    public int MaxResults { get; set; } = 6;
}

public sealed class UpcItemDbOptions
{
    public bool Enabled { get; set; } = true;

    public string ApiBaseUrl { get; set; } = "https://api.upcitemdb.com";

    public bool UseTrialWithoutKey { get; set; } = true;

    public string UserKey { get; set; } = string.Empty;

    public string KeyType { get; set; } = "3scale";
}

public sealed class GoUpcOptions
{
    public bool Enabled { get; set; } = true;

    public string ApiBaseUrl { get; set; } = "https://go-upc.com/api/v1";

    public string ApiKey { get; set; } = string.Empty;

    public bool UseBearerAuth { get; set; } = true;
}

public sealed class EanDbOptions
{
    public bool Enabled { get; set; } = true;

    public string ApiBaseUrl { get; set; } = "https://ean-db.com/api/v2";

    public string JwtToken { get; set; } = string.Empty;
}
