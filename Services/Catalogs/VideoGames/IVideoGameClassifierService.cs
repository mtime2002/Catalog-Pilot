using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IVideoGameClassifierService
{
    Task<ClassificationResult> ClassifyAsync(ListingInput input, CancellationToken cancellationToken = default);
}
