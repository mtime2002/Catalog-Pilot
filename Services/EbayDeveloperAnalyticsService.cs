using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class EbayDeveloperAnalyticsService : IEbayDeveloperAnalyticsService
{
    private readonly HttpClient _httpClient;
    private readonly EbayDeveloperAnalyticsOptions _options;
    private readonly EbayOptions _ebayOptions;
    private readonly ILogger<EbayDeveloperAnalyticsService> _logger;

    public EbayDeveloperAnalyticsService(
        HttpClient httpClient,
        IOptions<EbayDeveloperAnalyticsOptions> options,
        IOptions<EbayOptions> ebayOptions,
        ILogger<EbayDeveloperAnalyticsService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _ebayOptions = ebayOptions.Value;
        _logger = logger;
    }

    public async Task<EbayRateLimitResponse> GetAppRateLimitsAsync(
        string? apiName,
        string? apiContext,
        CancellationToken cancellationToken = default)
    {
        var request = ResolveRequest(apiName, apiContext);
        if (!_options.Enabled)
        {
            return CreateFailure(
                scope: "app",
                request.ApiName,
                request.ApiContext,
                "EbayDeveloperAnalytics is disabled.");
        }

        var clientId = _options.ClientId.Trim();
        var clientSecret = _options.ClientSecret.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return CreateFailure(
                scope: "app",
                request.ApiName,
                request.ApiContext,
                "Set EbayDeveloperAnalytics:ClientId and EbayDeveloperAnalytics:ClientSecret.");
        }

        var tokenResult = await TryGetAppAccessTokenAsync(clientId, clientSecret, cancellationToken);
        if (!tokenResult.Success || string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            return CreateFailure(
                scope: "app",
                request.ApiName,
                request.ApiContext,
                tokenResult.Message,
                tokenResult.HttpStatusCode,
                tokenResult.ErrorBody);
        }

        return await FetchRateLimitsAsync(
            scope: "app",
            route: "/developer/analytics/v1_beta/rate_limit/",
            bearerToken: tokenResult.AccessToken,
            request.ApiName,
            request.ApiContext,
            cancellationToken);
    }

    public async Task<EbayRateLimitResponse> GetUserRateLimitsAsync(
        string? apiName,
        string? apiContext,
        CancellationToken cancellationToken = default)
    {
        var request = ResolveRequest(apiName, apiContext);
        if (!_options.Enabled)
        {
            return CreateFailure(
                scope: "user",
                request.ApiName,
                request.ApiContext,
                "EbayDeveloperAnalytics is disabled.");
        }

        var token = _options.UserOAuthAccessToken.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            token = _ebayOptions.OAuthAccessToken.Trim();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return CreateFailure(
                scope: "user",
                request.ApiName,
                request.ApiContext,
                "Set EbayDeveloperAnalytics:UserOAuthAccessToken (or Ebay:OAuthAccessToken) to query user_rate_limit.");
        }

        return await FetchRateLimitsAsync(
            scope: "user",
            route: "/developer/analytics/v1_beta/user_rate_limit/",
            bearerToken: token,
            request.ApiName,
            request.ApiContext,
            cancellationToken);
    }

    private async Task<EbayRateLimitResponse> FetchRateLimitsAsync(
        string scope,
        string route,
        string bearerToken,
        string apiName,
        string apiContext,
        CancellationToken cancellationToken)
    {
        var baseUrl = ResolveAnalyticsApiBaseUrl();
        var endpoint =
            $"{baseUrl}{route}?api_name={Uri.EscapeDataString(apiName)}&api_context={Uri.EscapeDataString(apiContext)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "eBay analytics {Scope} rate-limit call failed with status {StatusCode}: {Body}",
                    scope,
                    response.StatusCode,
                    Truncate(body, 500));
                return CreateFailure(
                    scope,
                    apiName,
                    apiContext,
                    $"eBay analytics returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    statusCode,
                    body);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(body))
            {
                return new EbayRateLimitResponse
                {
                    Success = true,
                    Scope = scope,
                    ApiName = apiName,
                    ApiContext = apiContext,
                    HttpStatusCode = statusCode,
                    Message = "No rate-limit data available for the requested api_name/api_context yet.",
                    RawResponse = string.Empty,
                    Entries = [],
                    RetrievedUtc = DateTimeOffset.UtcNow
                };
            }

            using var document = JsonDocument.Parse(body);
            var entries = ParseRateLimitEntries(scope, apiName, apiContext, document.RootElement);
            return new EbayRateLimitResponse
            {
                Success = true,
                Scope = scope,
                ApiName = apiName,
                ApiContext = apiContext,
                HttpStatusCode = statusCode,
                Message = $"Fetched {entries.Count} rate-limit metric(s).",
                RawResponse = body,
                Entries = entries,
                RetrievedUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch eBay analytics {Scope} rate limits.", scope);
            return CreateFailure(
                scope,
                apiName,
                apiContext,
                $"Failed to call eBay analytics endpoint: {ex.Message}");
        }
    }

    private async Task<TokenResult> TryGetAppAccessTokenAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var baseUrl = ResolveIdentityApiBaseUrl();
        var endpoint = $"{baseUrl}/identity/v1/oauth2/token";
        var scope = string.IsNullOrWhiteSpace(_options.OAuthScope)
            ? "https://api.ebay.com/oauth/api_scope"
            : _options.OAuthScope.Trim();
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
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to fetch eBay app token with status {StatusCode}: {Body}",
                    response.StatusCode,
                    Truncate(body, 500));
                return TokenResult.Failed(
                    $"Token request failed with {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    statusCode,
                    body);
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                return TokenResult.Failed("Token response did not include access_token.", statusCode, body);
            }

            var token = tokenElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                return TokenResult.Failed("Token response included an empty access_token.", statusCode, body);
            }

            return TokenResult.Succeeded(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch eBay app token.");
            return TokenResult.Failed($"Failed to request app token: {ex.Message}");
        }
    }

    private (string ApiName, string ApiContext) ResolveRequest(string? apiName, string? apiContext)
    {
        var resolvedApiName = string.IsNullOrWhiteSpace(apiName)
            ? _options.DefaultApiName.Trim()
            : apiName.Trim();
        var resolvedApiContext = string.IsNullOrWhiteSpace(apiContext)
            ? _options.DefaultApiContext.Trim()
            : apiContext.Trim();

        if (string.IsNullOrWhiteSpace(resolvedApiName))
        {
            resolvedApiName = "inventory";
        }

        if (string.IsNullOrWhiteSpace(resolvedApiContext))
        {
            resolvedApiContext = "sell";
        }

        return (resolvedApiName, resolvedApiContext);
    }

    private string ResolveIdentityApiBaseUrl()
    {
        var configured = _options.IdentityApiBaseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return "https://api.ebay.com";
    }

    private string ResolveAnalyticsApiBaseUrl()
    {
        var configured = _options.AnalyticsApiBaseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return "https://api.ebay.com";
    }

    private static IReadOnlyList<EbayRateLimitMetric> ParseRateLimitEntries(
        string scope,
        string defaultApiName,
        string defaultApiContext,
        JsonElement root)
    {
        if (!root.TryGetProperty("rateLimits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<EbayRateLimitMetric>();
        foreach (var rateLimit in rateLimits.EnumerateArray())
        {
            var apiName = ReadString(rateLimit, "apiName");
            if (string.IsNullOrWhiteSpace(apiName))
            {
                apiName = defaultApiName;
            }

            var apiContext = ReadString(rateLimit, "apiContext");
            if (string.IsNullOrWhiteSpace(apiContext))
            {
                apiContext = defaultApiContext;
            }

            if (!rateLimit.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var resource in resources.EnumerateArray())
            {
                var resourceName = ReadString(resource, "name");
                if (!resource.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var rate in rates.EnumerateArray())
                {
                    var limit = ReadLong(rate, "limit");
                    var remaining = ReadLong(rate, "remaining");

                    entries.Add(new EbayRateLimitMetric
                    {
                        Scope = scope,
                        ApiName = apiName,
                        ApiContext = apiContext,
                        Resource = resourceName,
                        RateName = ReadString(rate, "name"),
                        Limit = limit,
                        Remaining = remaining,
                        Used = limit.HasValue && remaining.HasValue
                            ? Math.Max(0, limit.Value - remaining.Value)
                            : null,
                        Count = ReadLong(rate, "count"),
                        TimeWindow = ReadString(rate, "timeWindow"),
                        Reset = ReadDateTimeOffset(rate, "reset")
                    });
                }
            }
        }

        return entries;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epochSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static EbayRateLimitResponse CreateFailure(
        string scope,
        string apiName,
        string apiContext,
        string message,
        int? httpStatusCode = null,
        string errorBody = "")
    {
        return new EbayRateLimitResponse
        {
            Success = false,
            Scope = scope,
            ApiName = apiName,
            ApiContext = apiContext,
            Message = message,
            HttpStatusCode = httpStatusCode,
            ErrorBody = Truncate(errorBody, 2000),
            RetrievedUtc = DateTimeOffset.UtcNow
        };
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars];
    }

    private sealed class TokenResult
    {
        public bool Success { get; private init; }

        public string AccessToken { get; private init; } = string.Empty;

        public string Message { get; private init; } = string.Empty;

        public int? HttpStatusCode { get; private init; }

        public string ErrorBody { get; private init; } = string.Empty;

        public static TokenResult Succeeded(string token)
        {
            return new TokenResult
            {
                Success = true,
                AccessToken = token
            };
        }

        public static TokenResult Failed(string message, int? httpStatusCode = null, string errorBody = "")
        {
            return new TokenResult
            {
                Success = false,
                Message = message,
                HttpStatusCode = httpStatusCode,
                ErrorBody = Truncate(errorBody, 2000)
            };
        }
    }
}
