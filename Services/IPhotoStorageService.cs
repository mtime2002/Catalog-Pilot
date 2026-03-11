using CatalogPilot.Models;
using Microsoft.AspNetCore.Components.Forms;

namespace CatalogPilot.Services;

public interface IPhotoStorageService
{
    Task<IReadOnlyList<UploadedPhoto>> SaveAsync(IReadOnlyList<IBrowserFile> files, CancellationToken cancellationToken = default);
}
