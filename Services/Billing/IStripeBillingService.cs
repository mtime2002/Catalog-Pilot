using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IStripeBillingService
{
    Task<(bool Success, string Url, string ErrorMessage)> CreateCheckoutSessionUrlAsync(
        AppUserRecord user,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Url, string ErrorMessage)> CreatePortalSessionUrlAsync(
        AppUserRecord user,
        string returnUrl,
        CancellationToken cancellationToken = default);

    Task<StripeWebhookProcessResult> ProcessWebhookAsync(
        string payload,
        string? signatureHeader,
        CancellationToken cancellationToken = default);
}
