using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IUserAccountStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AppUserRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<AppUserRecord?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<AppUserRecord?> GetByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken cancellationToken = default);

    Task<(bool Success, string ErrorMessage, AppUserRecord? User)> CreateUserAsync(
        string email,
        string fullName,
        string passwordHash,
        CancellationToken cancellationToken = default);

    Task UpdateStripeCustomerIdAsync(
        Guid userId,
        string stripeCustomerId,
        CancellationToken cancellationToken = default);

    Task<UserSubscriptionRecord> GetOrCreateSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default);

    Task UpsertSubscriptionAsync(UserSubscriptionRecord subscription, CancellationToken cancellationToken = default);

    Task<bool> HasProcessedStripeEventAsync(string stripeEventId, CancellationToken cancellationToken = default);

    Task RecordStripeEventAsync(
        string stripeEventId,
        string eventType,
        string payloadJson,
        bool success,
        string message,
        CancellationToken cancellationToken = default);

    Task<int> CountUsageEventsAsync(
        Guid userId,
        string usageType,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken cancellationToken = default);

    Task<(bool Consumed, int UsedAfter)> TryConsumeUsageAsync(
        Guid userId,
        string usageType,
        int quantity,
        int limit,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken cancellationToken = default);
}
