using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class StripeBillingService : IStripeBillingService
{
    private const string FreePlanCode = "free";
    private static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active",
        "trialing"
    };

    private readonly HttpClient _httpClient;
    private readonly IUserAccountStore _userAccountStore;
    private readonly StripeBillingOptions _options;
    private readonly ILogger<StripeBillingService> _logger;

    public StripeBillingService(
        HttpClient httpClient,
        IUserAccountStore userAccountStore,
        IOptions<StripeBillingOptions> options,
        ILogger<StripeBillingService> logger)
    {
        _httpClient = httpClient;
        _userAccountStore = userAccountStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(bool Success, string Url, string ErrorMessage)> CreateCheckoutSessionUrlAsync(
        AppUserRecord user,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        if (!CanCallStripe(out var configurationError))
        {
            return (false, string.Empty, configurationError);
        }

        if (string.IsNullOrWhiteSpace(_options.MonthlyPriceId))
        {
            return (false, string.Empty, "StripeBilling:MonthlyPriceId is not configured.");
        }

        var customerId = await EnsureCustomerAsync(user, cancellationToken);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return (false, string.Empty, "Unable to provision Stripe customer.");
        }

        var form = new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["customer"] = customerId,
            ["success_url"] = successUrl,
            ["cancel_url"] = cancelUrl,
            ["line_items[0][price]"] = _options.MonthlyPriceId,
            ["line_items[0][quantity]"] = "1",
            ["allow_promotion_codes"] = "true",
            ["client_reference_id"] = user.Id.ToString("D"),
            ["metadata[user_id]"] = user.Id.ToString("D"),
            ["metadata[user_email]"] = user.Email
        };

        var response = await PostFormAsync("/v1/checkout/sessions", form, cancellationToken);
        if (!response.Success)
        {
            return (false, string.Empty, response.ErrorMessage);
        }

        using var json = JsonDocument.Parse(response.Body);
        if (!json.RootElement.TryGetProperty("url", out var urlElement))
        {
            return (false, string.Empty, "Stripe checkout session did not return a URL.");
        }

        var url = urlElement.GetString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(url)
            ? (false, string.Empty, "Stripe checkout session URL is empty.")
            : (true, url, string.Empty);
    }

    public async Task<(bool Success, string Url, string ErrorMessage)> CreatePortalSessionUrlAsync(
        AppUserRecord user,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {
        if (!CanCallStripe(out var configurationError))
        {
            return (false, string.Empty, configurationError);
        }

        var customerId = await EnsureCustomerAsync(user, cancellationToken);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return (false, string.Empty, "Unable to provision Stripe customer.");
        }

        var response = await PostFormAsync("/v1/billing_portal/sessions", new Dictionary<string, string>
        {
            ["customer"] = customerId,
            ["return_url"] = returnUrl
        }, cancellationToken);

        if (!response.Success)
        {
            return (false, string.Empty, response.ErrorMessage);
        }

        using var json = JsonDocument.Parse(response.Body);
        if (!json.RootElement.TryGetProperty("url", out var urlElement))
        {
            return (false, string.Empty, "Stripe billing portal did not return a URL.");
        }

        var url = urlElement.GetString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(url)
            ? (false, string.Empty, "Stripe billing portal URL is empty.")
            : (true, url, string.Empty);
    }

    public async Task<StripeWebhookProcessResult> ProcessWebhookAsync(
        string payload,
        string? signatureHeader,
        CancellationToken cancellationToken = default)
    {
        if (!CanCallStripe(out var configurationError))
        {
            return new StripeWebhookProcessResult
            {
                Success = false,
                Authorized = false,
                Message = configurationError
            };
        }

        if (!ValidateWebhookSignature(payload, signatureHeader, out var signatureError))
        {
            return new StripeWebhookProcessResult
            {
                Success = false,
                Authorized = false,
                Message = signatureError
            };
        }

        JsonDocument? json = null;
        try
        {
            json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            var eventId = GetString(root, "id");
            var eventType = GetString(root, "type");
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType))
            {
                return new StripeWebhookProcessResult
                {
                    Success = false,
                    Message = "Stripe event payload is missing id or type."
                };
            }

            if (await _userAccountStore.HasProcessedStripeEventAsync(eventId, cancellationToken))
            {
                return new StripeWebhookProcessResult
                {
                    Success = true,
                    EventId = eventId,
                    EventType = eventType,
                    Message = "Event already processed."
                };
            }

            string message;
            var success = eventType switch
            {
                "customer.subscription.created" => await HandleSubscriptionEventAsync(root, cancellationToken),
                "customer.subscription.updated" => await HandleSubscriptionEventAsync(root, cancellationToken),
                "customer.subscription.deleted" => await HandleSubscriptionEventAsync(root, cancellationToken),
                "checkout.session.completed" => await HandleCheckoutCompletedAsync(root, cancellationToken),
                _ => true
            };

            message = success
                ? "Processed."
                : "Webhook payload could not be mapped to a local user.";

            await _userAccountStore.RecordStripeEventAsync(
                eventId,
                eventType,
                payload,
                success,
                message,
                cancellationToken);

            return new StripeWebhookProcessResult
            {
                Success = success,
                EventId = eventId,
                EventType = eventType,
                Message = message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Stripe webhook payload.");
            return new StripeWebhookProcessResult
            {
                Success = false,
                Message = ex.Message
            };
        }
        finally
        {
            json?.Dispose();
        }
    }

    private async Task<bool> HandleSubscriptionEventAsync(JsonElement eventRoot, CancellationToken cancellationToken)
    {
        if (!TryGetDataObject(eventRoot, out var subscriptionObject))
        {
            return false;
        }

        var customerId = GetString(subscriptionObject, "customer");
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return false;
        }

        var user = await _userAccountStore.GetByStripeCustomerIdAsync(customerId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        var status = GetString(subscriptionObject, "status");
        var subscriptionId = GetString(subscriptionObject, "id");
        var periodEnd = GetUnixTimestamp(subscriptionObject, "current_period_end");
        var trialEnd = GetUnixTimestamp(subscriptionObject, "trial_end");
        var cancelAtPeriodEnd = GetBoolean(subscriptionObject, "cancel_at_period_end");

        var existing = await _userAccountStore.GetOrCreateSubscriptionAsync(user.Id, cancellationToken);
        existing.PlanCode = ActiveStatuses.Contains(status) ? _options.PaidPlanCode : FreePlanCode;
        existing.Status = string.IsNullOrWhiteSpace(status) ? existing.Status : status;
        existing.StripeSubscriptionId = string.IsNullOrWhiteSpace(subscriptionId) ? existing.StripeSubscriptionId : subscriptionId;
        existing.StripeCustomerId = customerId;
        existing.CurrentPeriodEndUtc = periodEnd ?? existing.CurrentPeriodEndUtc;
        existing.TrialEndUtc = trialEnd;
        existing.CancelAtPeriodEnd = cancelAtPeriodEnd ?? existing.CancelAtPeriodEnd;
        existing.UpdatedUtc = DateTimeOffset.UtcNow;

        await _userAccountStore.UpsertSubscriptionAsync(existing, cancellationToken);
        return true;
    }

    private async Task<bool> HandleCheckoutCompletedAsync(JsonElement eventRoot, CancellationToken cancellationToken)
    {
        if (!TryGetDataObject(eventRoot, out var sessionObject))
        {
            return false;
        }

        var mode = GetString(sessionObject, "mode");
        if (!mode.Equals("subscription", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var customerId = GetString(sessionObject, "customer");
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return false;
        }

        var user = await _userAccountStore.GetByStripeCustomerIdAsync(customerId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        var subscriptionId = GetString(sessionObject, "subscription");
        var existing = await _userAccountStore.GetOrCreateSubscriptionAsync(user.Id, cancellationToken);
        existing.StripeCustomerId = customerId;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            existing.StripeSubscriptionId = subscriptionId;
        }

        if (string.Equals(existing.Status, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            existing.Status = "pending_activation";
        }

        await _userAccountStore.UpsertSubscriptionAsync(existing, cancellationToken);
        return true;
    }

    private async Task<string> EnsureCustomerAsync(AppUserRecord user, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            return user.StripeCustomerId;
        }

        var response = await PostFormAsync("/v1/customers", new Dictionary<string, string>
        {
            ["email"] = user.Email,
            ["name"] = user.FullName,
            ["metadata[user_id]"] = user.Id.ToString("D")
        }, cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("Stripe customer creation failed for user {UserId}: {Error}", user.Id, response.ErrorMessage);
            return string.Empty;
        }

        using var json = JsonDocument.Parse(response.Body);
        var customerId = GetString(json.RootElement, "id");
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return string.Empty;
        }

        await _userAccountStore.UpdateStripeCustomerIdAsync(user.Id, customerId, cancellationToken);
        return customerId;
    }

    private async Task<(bool Success, string Body, string ErrorMessage)> PostFormAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{_options.ApiBaseUrl.TrimEnd('/')}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey.Trim());
        request.Content = new FormUrlEncodedContent(fields);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string? stripeError = null;
            if (!string.IsNullOrWhiteSpace(content))
            {
                using var json = JsonDocument.Parse(content);
                stripeError = TryExtractStripeError(json);
            }

            var error = stripeError ?? $"Stripe request failed with HTTP {(int)response.StatusCode}.";
            return (false, string.Empty, error);
        }

        return string.IsNullOrWhiteSpace(content)
            ? (false, string.Empty, "Stripe response payload was empty.")
            : (true, content, string.Empty);
    }

    private bool CanCallStripe(out string error)
    {
        if (!_options.Enabled)
        {
            error = "Stripe billing is disabled.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            error = "StripeBilling:SecretKey is not configured.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool ValidateWebhookSignature(string payload, string? signatureHeader, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(_options.WebhookSigningSecret))
        {
            error = "Stripe webhook signing secret is missing.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            error = "Missing Stripe-Signature header.";
            return false;
        }

        var tokens = signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long timestamp = 0;
        var signatures = new List<string>();
        foreach (var token in tokens)
        {
            var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0].Equals("t", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[1], out var parsedTimestamp))
            {
                timestamp = parsedTimestamp;
                continue;
            }

            if (parts[0].Equals("v1", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parts[1]))
            {
                signatures.Add(parts[1]);
            }
        }

        if (timestamp <= 0 || signatures.Count == 0)
        {
            error = "Stripe-Signature header format is invalid.";
            return false;
        }

        var eventTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var age = DateTimeOffset.UtcNow - eventTime;
        if (age.Duration() > TimeSpan.FromMinutes(5))
        {
            error = "Stripe webhook signature timestamp is outside the valid tolerance.";
            return false;
        }

        var signedPayload = $"{timestamp}.{payload}";
        var keyBytes = Encoding.UTF8.GetBytes(_options.WebhookSigningSecret.Trim());
        var payloadBytes = Encoding.UTF8.GetBytes(signedPayload);
        var expectedSignatureBytes = HMACSHA256.HashData(keyBytes, payloadBytes);

        foreach (var signature in signatures)
        {
            try
            {
                var actualSignatureBytes = Convert.FromHexString(signature);
                if (actualSignatureBytes.Length == expectedSignatureBytes.Length &&
                    CryptographicOperations.FixedTimeEquals(expectedSignatureBytes, actualSignatureBytes))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // Ignore malformed signature values and continue.
            }
        }

        error = "Stripe webhook signature validation failed.";
        return false;
    }

    private static bool TryGetDataObject(JsonElement root, out JsonElement objectElement)
    {
        objectElement = default;
        if (!root.TryGetProperty("data", out var dataElement))
        {
            return false;
        }

        if (!dataElement.TryGetProperty("object", out objectElement))
        {
            return false;
        }

        return objectElement.ValueKind == JsonValueKind.Object;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var valueElement))
        {
            return string.Empty;
        }

        return valueElement.ValueKind switch
        {
            JsonValueKind.String => valueElement.GetString() ?? string.Empty,
            JsonValueKind.Number => valueElement.GetRawText(),
            _ => string.Empty
        };
    }

    private static DateTimeOffset? GetUnixTimestamp(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var valueElement))
        {
            return null;
        }

        long unixSeconds;
        if (valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetInt64(out unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        if (valueElement.ValueKind == JsonValueKind.String &&
            long.TryParse(valueElement.GetString(), out unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return null;
    }

    private static bool? GetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var valueElement))
        {
            return null;
        }

        if (valueElement.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (valueElement.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (valueElement.ValueKind == JsonValueKind.String &&
            bool.TryParse(valueElement.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? TryExtractStripeError(JsonDocument? json)
    {
        if (json is null)
        {
            return null;
        }

        if (!json.RootElement.TryGetProperty("error", out var errorElement))
        {
            return null;
        }

        if (!errorElement.TryGetProperty("message", out var messageElement))
        {
            return null;
        }

        return messageElement.GetString();
    }
}
