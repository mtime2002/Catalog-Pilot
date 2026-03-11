using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class EbayPricingService : IEbayPricingService
{
    private readonly HttpClient _httpClient;
    private readonly EbayOptions _options;
    private readonly EbayDeveloperAnalyticsOptions _analyticsOptions;
    private readonly ILogger<EbayPricingService> _logger;

    public EbayPricingService(
        HttpClient httpClient,
        IOptions<EbayOptions> options,
        IOptions<EbayDeveloperAnalyticsOptions> analyticsOptions,
        ILogger<EbayPricingService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _analyticsOptions = analyticsOptions.Value;
        _logger = logger;
    }

    public async Task<PriceSuggestionResult> SuggestPriceAsync(
        ListingInput input,
        ClassificationResult? classification = null,
        CancellationToken cancellationToken = default)
    {
        var queries = BuildSearchQueries(input, classification);
        var browseResult = await TryBrowseComparableListingsAsync(queries, cancellationToken);
        var liveSales = browseResult.Sales;
        var comparableSales = liveSales.Count > 0 ? liveSales : BuildDemoComparableSales(input);

        var adjustedSoldValues = comparableSales
            .Select(s => s.SoldPrice + s.ShippingPrice)
            .Where(v => v > 0)
            .OrderBy(v => v)
            .ToArray();

        var baseline = adjustedSoldValues.Length == 0 ? 19.99m : Median(adjustedSoldValues);
        var sealedMultiplier = input.IsSealed ? 1.08m : 1m;
        var suggestedPrice = SnapToNinetyNineCents(baseline * sealedMultiplier);
        if (input.UserPriceOverride is > 0)
        {
            suggestedPrice = input.UserPriceOverride.Value;
        }

        return new PriceSuggestionResult
        {
            SuggestedPrice = suggestedPrice,
            Currency = _options.Currency,
            Strategy = liveSales.Count > 0
                ? "Based on active eBay listings (Browse API)"
                : browseResult.Status is BrowseCallStatus.AuthUnavailable
                    ? "Heuristic fallback (set EbayDeveloperAnalytics:ClientId and ClientSecret for live Browse comps)"
                : browseResult.Status is BrowseCallStatus.NoResults
                    ? "Heuristic fallback (no active Browse comps found for this title)"
                    : "Heuristic fallback (Browse comps unavailable right now)",
            ComparableSales = comparableSales
        };
    }

    private async Task<BrowseQueryResult> TryBrowseComparableListingsAsync(
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        var token = await TryGetBrowseAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new BrowseQueryResult([], BrowseCallStatus.AuthUnavailable, string.Empty);
        }

        var hadTransportFailure = false;
        var lastQuery = string.Empty;
        foreach (var query in queries)
        {
            lastQuery = query;
            var attempt = await QueryBrowseComparableListingsAsync(token, query, cancellationToken);
            if (attempt.Sales.Count > 0)
            {
                return attempt;
            }

            if (attempt.Status == BrowseCallStatus.Failed)
            {
                hadTransportFailure = true;
            }
        }

        return hadTransportFailure
            ? new BrowseQueryResult([], BrowseCallStatus.Failed, lastQuery)
            : new BrowseQueryResult([], BrowseCallStatus.NoResults, lastQuery);
    }

    private async Task<BrowseQueryResult> QueryBrowseComparableListingsAsync(
        string token,
        string query,
        CancellationToken cancellationToken)
    {
        var baseUrl = ResolveBrowseApiBaseUrl();
        var requestUri = $"{baseUrl}/buy/browse/v1/item_summary/search?q={Uri.EscapeDataString(query)}&limit=25";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation(
                "X-EBAY-C-MARKETPLACE-ID",
                string.IsNullOrWhiteSpace(_options.MarketplaceId) ? "EBAY_US" : _options.MarketplaceId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Browse API request failed with status code {StatusCode}. Response: {Body}",
                    response.StatusCode,
                    Truncate(errorBody, 360));
                return new BrowseQueryResult([], BrowseCallStatus.Failed, query);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return new BrowseQueryResult(ParseBrowseComparableListings(document.RootElement), BrowseCallStatus.Success, query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query eBay Browse listings");
            return new BrowseQueryResult([], BrowseCallStatus.Failed, query);
        }
    }

    private async Task<string> TryGetBrowseAccessTokenAsync(CancellationToken cancellationToken)
    {
        var clientId = _analyticsOptions.ClientId.Trim();
        var clientSecret = _analyticsOptions.ClientSecret.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return string.Empty;
        }

        var identityBaseUrl = string.IsNullOrWhiteSpace(_analyticsOptions.IdentityApiBaseUrl)
            ? "https://api.ebay.com"
            : _analyticsOptions.IdentityApiBaseUrl.Trim().TrimEnd('/');
        var endpoint = $"{identityBaseUrl}/identity/v1/oauth2/token";
        var scope = string.IsNullOrWhiteSpace(_analyticsOptions.OAuthScope)
            ? "https://api.ebay.com/oauth/api_scope"
            : _analyticsOptions.OAuthScope.Trim();
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope
        });

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Browse token request failed with status {StatusCode}. Response: {Body}",
                    response.StatusCode,
                    Truncate(body, 360));
                return string.Empty;
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return tokenElement.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request Browse API access token.");
            return string.Empty;
        }
    }

    private string ResolveBrowseApiBaseUrl()
    {
        var configured = (_options.SellApiBaseUrl ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return "https://api.ebay.com";
    }

    private static IReadOnlyList<ComparableSale> ParseBrowseComparableListings(JsonElement root)
    {
        if (!root.TryGetProperty("itemSummaries", out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sales = new List<ComparableSale>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            var title = ReadString(item, "title");
            var itemId = ReadString(item, "itemId");
            var listingUrl = ReadString(item, "itemWebUrl");
            var soldPrice = ParseDecimal(ReadString(item, "price", "value"));
            var shipping = ParseDecimal(ReadString(item, "shippingOptions", "shippingCost", "value"));
            var currency = FirstNonEmpty(
                ReadString(item, "price", "currency"),
                ReadString(item, "shippingOptions", "shippingCost", "currency"),
                "USD");
            var condition = ReadString(item, "condition");

            if (soldPrice <= 0)
            {
                continue;
            }

            sales.Add(new ComparableSale
            {
                ItemId = itemId,
                Title = title,
                SoldPrice = soldPrice,
                ShippingPrice = shipping,
                Currency = currency,
                Condition = condition,
                ListingUrl = listingUrl
            });
        }

        return sales
            .OrderBy(s => s.SoldPrice + s.ShippingPrice)
            .Take(12)
            .ToArray();
    }

    private static IReadOnlyList<ComparableSale> BuildDemoComparableSales(ListingInput input)
    {
        var seed = input.ItemName.GetHashCode(StringComparison.OrdinalIgnoreCase);
        var random = new Random(seed);
        var basePrice = EstimateBasePrice(input);

        var sales = new List<ComparableSale>(6);
        for (var i = 0; i < 6; i++)
        {
            var variance = (decimal)(random.NextDouble() * 0.28 - 0.14);
            var soldPrice = Math.Round(basePrice * (1 + variance), 2, MidpointRounding.AwayFromZero);
            var shipping = Math.Round((decimal)(3.5 + random.NextDouble() * 2.5), 2, MidpointRounding.AwayFromZero);
            sales.Add(new ComparableSale
            {
                ItemId = $"demo-{i + 1}",
                Title = $"{input.ItemName} Comparable #{i + 1}",
                SoldPrice = soldPrice,
                ShippingPrice = shipping,
                Condition = input.Condition,
                Currency = "USD",
                SoldDate = DateTimeOffset.UtcNow.AddDays(-(i * 3 + 2))
            });
        }

        return sales;
    }

    private static decimal EstimateBasePrice(ListingInput input)
    {
        var searchable = $"{input.ItemName} {input.Description} {input.Platform}".ToLowerInvariant();
        var basePrice = 24m;

        if (searchable.Contains("sealed", StringComparison.OrdinalIgnoreCase) || input.IsSealed)
        {
            basePrice += 20m;
        }

        if (searchable.Contains("ps5", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("xbox series", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("switch", StringComparison.OrdinalIgnoreCase))
        {
            basePrice += 12m;
        }

        if (searchable.Contains("n64", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("snes", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("gamecube", StringComparison.OrdinalIgnoreCase))
        {
            basePrice += 18m;
        }

        if (searchable.Contains("collector", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("limited", StringComparison.OrdinalIgnoreCase))
        {
            basePrice += 15m;
        }

        return basePrice;
    }

    private static IReadOnlyList<string> BuildSearchQueries(ListingInput input, ClassificationResult? classification)
    {
        var preferredTitle = FirstNonEmpty(classification?.SuggestedTitle, input.ItemName);
        var preferredPlatform = FirstNonEmpty(classification?.SuggestedPlatform, input.Platform);

        var candidates = new List<string>
        {
            BuildSearchQueryVariant(preferredTitle, preferredPlatform, includeGenericTerm: true),
            BuildSearchQueryVariant(preferredTitle, preferredPlatform, includeGenericTerm: false),
            BuildSearchQueryVariant(preferredTitle, string.Empty, includeGenericTerm: false),
            BuildSearchQueryVariant(input.ItemName, input.Platform, includeGenericTerm: false)
        };

        return candidates
            .Where(q => !string.IsNullOrWhiteSpace(q) && q.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildSearchQueryVariant(string title, string platform, bool includeGenericTerm)
    {
        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            queryParts.Add(title);
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            queryParts.Add(platform);
        }

        if (includeGenericTerm)
        {
            queryParts.Add("video game");
        }

        return string.Join(' ', queryParts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars];
    }

    private static decimal ParseDecimal(string value)
    {
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private static decimal SnapToNinetyNineCents(decimal value)
    {
        if (value < 1m)
        {
            return Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        var roundedUp = Math.Ceiling(value);
        return roundedUp - 0.01m;
    }

    private static decimal Median(decimal[] values)
    {
        if (values.Length == 0)
        {
            return 0m;
        }

        var middle = values.Length / 2;
        if (values.Length % 2 == 0)
        {
            return (values[middle - 1] + values[middle]) / 2m;
        }

        return values[middle];
    }

    private static string ReadString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var part in path)
        {
            if (!TryGetPropertyOrFirstArrayElement(current, part, out var next))
            {
                return string.Empty;
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number => current.GetRawText(),
            _ => string.Empty
        };
    }

    private static bool TryGetFirstArrayElement(JsonElement root, string propertyName, out JsonElement element)
    {
        element = default;
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            return false;
        }

        element = array[0];
        return true;
    }

    private static bool TryGetPropertyOrFirstArrayElement(JsonElement root, string propertyName, out JsonElement element)
    {
        element = default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            if (property.GetArrayLength() == 0)
            {
                return false;
            }

            element = property[0];
            return true;
        }

        element = property;
        return true;
    }

    private readonly record struct BrowseQueryResult(
        IReadOnlyList<ComparableSale> Sales,
        BrowseCallStatus Status,
        string QueryUsed);

    private enum BrowseCallStatus
    {
        Success = 1,
        Failed = 2,
        AuthUnavailable = 3,
        NoResults = 4
    }
}
