using CatalogPilot.Models;
using Microsoft.AspNetCore.Components.Forms;

namespace CatalogPilot.Services;

public sealed class LocalPhotoStorageService : IPhotoStorageService
{
    private const long MaxUploadSize = 12 * 1024 * 1024;
    private readonly IWebHostEnvironment _hostEnvironment;

    public LocalPhotoStorageService(IWebHostEnvironment hostEnvironment)
    {
        _hostEnvironment = hostEnvironment;
    }

    public async Task<IReadOnlyList<UploadedPhoto>> SaveAsync(
        IReadOnlyList<IBrowserFile> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var uploadRoot = Path.Combine(_hostEnvironment.WebRootPath, "uploads");
        Directory.CreateDirectory(uploadRoot);

        var results = new List<UploadedPhoto>(files.Count);
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            var safeExtension = extension.ToLowerInvariant();
            var fileName = $"{Guid.NewGuid():N}{safeExtension}";
            var filePath = Path.Combine(uploadRoot, fileName);
            await using var sourceStream = file.OpenReadStream(MaxUploadSize, cancellationToken);
            await using var targetStream = File.Create(filePath);
            await sourceStream.CopyToAsync(targetStream, cancellationToken);

            results.Add(new UploadedPhoto
            {
                FileName = file.Name,
                ContentType = file.ContentType,
                SizeBytes = file.Size,
                RelativeUrl = $"/uploads/{fileName}"
            });
        }

        return results;
    }
}
