using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IBarcodeGameClassifierService
{
    Task<ClassificationResult?> TryClassifyAsync(ListingInput input, CancellationToken cancellationToken = default);
}
