using CatalogPilot.Models;
using CatalogPilot.Options;
using Microsoft.Extensions.Options;

namespace CatalogPilot.Services;

public sealed class EntitlementService : IEntitlementService
{
    private const string UsageTypeListingCreate = "listing_create";
    private const string FreePlanCode = "free";
    private static readonly HashSet<string> PaidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active",
        "trialing"
    };

    private readonly IUserAccountStore _userAccountStore;
    private readonly SubscriptionEntitlementOptions _options;
    private readonly StripeBillingOptions _stripeOptions;

    public EntitlementService(
        IUserAccountStore userAccountStore,
        IOptions<SubscriptionEntitlementOptions> options,
        IOptions<StripeBillingOptions> stripeOptions)
    {
        _userAccountStore = userAccountStore;
        _options = options.Value;
        _stripeOptions = stripeOptions.Value;
    }

    public async Task<EntitlementSnapshot> GetSnapshotAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var subscription = await _userAccountStore.GetOrCreateSubscriptionAsync(userId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEnd = monthStart.AddMonths(1);
        var used = await _userAccountStore.CountUsageEventsAsync(
            userId,
            UsageTypeListingCreate,
            monthStart,
            monthEnd,
            cancellationToken);

        return BuildSnapshot(subscription, used);
    }

    public async Task<EntitlementConsumeResult> TryConsumeListingCreationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var subscription = await _userAccountStore.GetOrCreateSubscriptionAsync(userId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEnd = monthStart.AddMonths(1);
        var isPaid = IsPaid(subscription);

        if (isPaid)
        {
            var paidUsage = await _userAccountStore.CountUsageEventsAsync(
                userId,
                UsageTypeListingCreate,
                monthStart,
                monthEnd,
                cancellationToken);
            return new EntitlementConsumeResult
            {
                Allowed = true,
                Message = "Listing creation allowed.",
                Snapshot = BuildSnapshot(subscription, paidUsage)
            };
        }

        var monthlyLimit = Math.Max(1, _options.FreeMonthlyListingLimit);
        var consumeResult = await _userAccountStore.TryConsumeUsageAsync(
            userId,
            UsageTypeListingCreate,
            quantity: 1,
            limit: monthlyLimit,
            monthStart,
            monthEnd,
            cancellationToken);
        var snapshot = BuildSnapshot(subscription, consumeResult.UsedAfter);

        return new EntitlementConsumeResult
        {
            Allowed = consumeResult.Consumed,
            Message = consumeResult.Consumed
                ? "Listing creation allowed."
                : $"Free plan monthly limit reached ({monthlyLimit}). Upgrade to continue.",
            Snapshot = snapshot
        };
    }

    private EntitlementSnapshot BuildSnapshot(UserSubscriptionRecord subscription, int used)
    {
        var isPaid = IsPaid(subscription);
        var freeLimit = Math.Max(1, _options.FreeMonthlyListingLimit);
        var limit = isPaid ? int.MaxValue : freeLimit;
        var remaining = isPaid ? int.MaxValue : Math.Max(0, limit - used);

        return new EntitlementSnapshot
        {
            PlanCode = string.IsNullOrWhiteSpace(subscription.PlanCode) ? FreePlanCode : subscription.PlanCode,
            SubscriptionStatus = string.IsNullOrWhiteSpace(subscription.Status) ? "inactive" : subscription.Status,
            MonthlyListingLimit = limit,
            MonthlyListingsUsed = Math.Max(0, used),
            MonthlyListingsRemaining = remaining,
            CanCreateListing = isPaid || remaining > 0,
            IsPaidPlan = isPaid
        };
    }

    private bool IsPaid(UserSubscriptionRecord subscription)
    {
        if (!string.IsNullOrWhiteSpace(subscription.PlanCode) &&
            !subscription.PlanCode.Equals(FreePlanCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_stripeOptions.PaidPlanCode) &&
            subscription.PlanCode.Equals(_stripeOptions.PaidPlanCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return PaidStatuses.Contains(subscription.Status);
    }
}
