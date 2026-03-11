using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IEntitlementService
{
    Task<EntitlementSnapshot> GetSnapshotAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<EntitlementConsumeResult> TryConsumeListingCreationAsync(Guid userId, CancellationToken cancellationToken = default);
}
