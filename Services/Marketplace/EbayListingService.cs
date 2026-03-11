using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class EbayListingService : IEbayListingService
{
    private readonly HttpClient _httpClient;
    private readonly EbayOptions _options;
    private readonly ILogger<EbayListingService> _logger;

    public EbayListingService(HttpClient httpClient, IOptions<EbayOptions> options, ILogger<EbayListingService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PublishListingResult> CreateListingAsync(
        ListingInput input,
        ClassificationResult? classification,
        PriceSuggestionResult? pricing,
        CancellationToken cancellationToken = default)
    {
        var sku = $"vg-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..26];
        var price = input.UserPriceOverride ?? pricing?.SuggestedPrice ?? 19.99m;
        var title = classification?.SuggestedTitle ?? input.ItemName;
        var categoryId = classification?.CategoryId ?? "139973";
        var condition = classification?.SuggestedCondition ?? input.Condition;

        var payload = BuildPayload(input, classification, sku, title, condition, categoryId, price);
        var payloadPreviewJson = JsonSerializer.Serialize(payload, JsonOptions);
        var missing = MissingLivePublishFields();
        if (missing.Count > 0)
        {
            return new PublishListingResult
            {
                Success = false,
                DraftOnly = true,
                Message = $"Draft generated. Set {string.Join(", ", missing)} to publish live on eBay.",
                PayloadPreviewJson = payloadPreviewJson
            };
        }

        try
        {
            var inventoryResponse = await PutInventoryItemAsync(payload, cancellationToken);
            if (!inventoryResponse.success)
            {
                return new PublishListingResult
                {
                    Success = false,
                    DraftOnly = true,
                    Message = inventoryResponse.message,
                    PayloadPreviewJson = payloadPreviewJson
                };
            }

            var offerId = await CreateOfferAsync(payload, cancellationToken);
            if (string.IsNullOrWhiteSpace(offerId))
            {
                return new PublishListingResult
                {
                    Success = false,
                    DraftOnly = true,
                    Message = "Draft uploaded to inventory, but failed to create offer.",
                    PayloadPreviewJson = payloadPreviewJson
                };
            }

            var publishedListingId = await PublishOfferAsync(offerId, cancellationToken);
            if (string.IsNullOrWhiteSpace(publishedListingId))
            {
                return new PublishListingResult
                {
                    Success = false,
                    DraftOnly = true,
                    Message = $"Offer {offerId} created, but publish failed.",
                    PayloadPreviewJson = payloadPreviewJson
                };
            }

            return new PublishListingResult
            {
                Success = true,
                DraftOnly = false,
                ListingId = publishedListingId,
                Message = $"Listing published to eBay. Listing ID: {publishedListingId}",
                PayloadPreviewJson = payloadPreviewJson
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create eBay listing");
            return new PublishListingResult
            {
                Success = false,
                DraftOnly = true,
                Message = $"Publish attempt failed: {ex.Message}",
                PayloadPreviewJson = payloadPreviewJson
            };
        }
    }

    private ListingPayload BuildPayload(
        ListingInput input,
        ClassificationResult? classification,
        string sku,
        string title,
        string condition,
        string categoryId,
        decimal price)
    {
        var imageUrls = input.Photos
            .Select(p => ToPublicImageUrl(p.RelativeUrl))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var specifics = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in classification?.ItemSpecifics ?? [])
        {
            specifics[key] = [value];
        }

        if (!specifics.ContainsKey("Platform") && !string.IsNullOrWhiteSpace(input.Platform))
        {
            specifics["Platform"] = [input.Platform];
        }

        return new ListingPayload
        {
            Sku = sku,
            Title = title,
            CategoryId = categoryId,
            Condition = condition,
            Quantity = input.Quantity,
            Price = new Money
            {
                Value = price,
                Currency = _options.Currency
            },
            ListingDescription = input.Description,
            ImageUrls = imageUrls,
            ItemSpecifics = specifics
        };
    }

    private async Task<(bool success, string message)> PutInventoryItemAsync(ListingPayload payload, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            availability = new
            {
                shipToLocationAvailability = new
                {
                    quantity = payload.Quantity
                }
            },
            condition = payload.Condition,
            product = new
            {
                title = payload.Title,
                description = payload.ListingDescription,
                imageUrls = payload.ImageUrls,
                aspects = payload.ItemSpecifics
            }
        };

        var url = $"{_options.SellApiBaseUrl}/sell/inventory/v1/inventory_item/{Uri.EscapeDataString(payload.Sku)}";
        using var request = CreateAuthorizedRequest(HttpMethod.Put, url, requestBody);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return (true, "Inventory item created.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (false, $"Inventory item create failed ({(int)response.StatusCode}): {Truncate(body, 320)}");
    }

    private async Task<string> CreateOfferAsync(ListingPayload payload, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            sku = payload.Sku,
            marketplaceId = _options.MarketplaceId,
            format = "FIXED_PRICE",
            availableQuantity = payload.Quantity,
            categoryId = payload.CategoryId,
            listingDescription = payload.ListingDescription,
            listingPolicies = new
            {
                fulfillmentPolicyId = _options.FulfillmentPolicyId,
                paymentPolicyId = _options.PaymentPolicyId,
                returnPolicyId = _options.ReturnPolicyId
            },
            merchantLocationKey = _options.MerchantLocationKey,
            pricingSummary = new
            {
                price = payload.Price
            }
        };

        var url = $"{_options.SellApiBaseUrl}/sell/inventory/v1/offer";
        using var request = CreateAuthorizedRequest(HttpMethod.Post, url, requestBody);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Offer creation failed with status {StatusCode}: {Body}", response.StatusCode, body);
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("offerId", out var offerId) ? offerId.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> PublishOfferAsync(string offerId, CancellationToken cancellationToken)
    {
        var url = $"{_options.SellApiBaseUrl}/sell/inventory/v1/offer/{Uri.EscapeDataString(offerId)}/publish";
        using var request = CreateAuthorizedRequest(HttpMethod.Post, url, new { });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Offer publish failed with status {StatusCode}: {Body}", response.StatusCode, body);
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("listingId", out var listingId))
            {
                return listingId.GetString() ?? string.Empty;
            }

            if (doc.RootElement.TryGetProperty("listing", out var listingElement) &&
                listingElement.TryGetProperty("listingId", out var nestedListingId))
            {
                return nestedListingId.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // If response is not JSON, treat as failure and return empty.
        }

        return string.Empty;
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, object body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.OAuthAccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private List<string> MissingLivePublishFields()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_options.OAuthAccessToken))
        {
            missing.Add($"{EbayOptions.SectionName}:OAuthAccessToken");
        }

        if (string.IsNullOrWhiteSpace(_options.FulfillmentPolicyId))
        {
            missing.Add($"{EbayOptions.SectionName}:FulfillmentPolicyId");
        }

        if (string.IsNullOrWhiteSpace(_options.PaymentPolicyId))
        {
            missing.Add($"{EbayOptions.SectionName}:PaymentPolicyId");
        }

        if (string.IsNullOrWhiteSpace(_options.ReturnPolicyId))
        {
            missing.Add($"{EbayOptions.SectionName}:ReturnPolicyId");
        }

        if (string.IsNullOrWhiteSpace(_options.MerchantLocationKey))
        {
            missing.Add($"{EbayOptions.SectionName}:MerchantLocationKey");
        }

        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            missing.Add($"{EbayOptions.SectionName}:PublicBaseUrl");
        }

        return missing;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars];
    }

    private string ToPublicImageUrl(string relativeOrAbsoluteUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsoluteUrl))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(relativeOrAbsoluteUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl) ||
            !Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return string.Empty;
        }

        return new Uri(baseUri, relativeOrAbsoluteUrl.TrimStart('/')).ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class ListingPayload
    {
        public string Sku { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string CategoryId { get; init; } = string.Empty;

        public string Condition { get; init; } = string.Empty;

        public int Quantity { get; init; }

        public Money Price { get; init; } = new();

        public string ListingDescription { get; init; } = string.Empty;

        public string[] ImageUrls { get; init; } = [];

        public Dictionary<string, string[]> ItemSpecifics { get; init; } = [];
    }

    private sealed class Money
    {
        public decimal Value { get; init; }

        public string Currency { get; init; } = "USD";
    }
}
