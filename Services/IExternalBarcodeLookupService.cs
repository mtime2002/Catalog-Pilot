using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IExternalBarcodeLookupService
{
    Task<ExternalBarcodeLookupResult?> LookupAsync(string code, string? platformHint = null, CancellationToken cancellationToken = default);

    Task<ExternalBarcodeLookupResult?> LookupByTitleAsync(string title, string? platformHint = null, CancellationToken cancellationToken = default);
}
