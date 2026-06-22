using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;

namespace PFOH.Api.Services;

public class HonoreeFileStorage(IConfiguration configuration)
{
    public const string ImageContainerName = "honoreeimages";
    public const string PdfContainerName = "honoreepdfs";

    public string GetHonoreePdfFileName(int honoreeId) => $"{honoreeId}_HonoreeReport.pdf";

    public async Task<string?> UploadPendingImageAsync(IFormFile? photo, int changeRequestId, CancellationToken ct)
    {
        if (photo is null || photo.Length == 0)
        {
            return null;
        }

        var extension = GetSafeImageExtension(photo.FileName, photo.ContentType);
        var fileName = $"pending/{changeRequestId}{extension}";
        await UploadImageBlobAsync(fileName, photo, ct);
        return fileName;
    }

    public async Task<string?> PromoteImageAsync(string? sourceFileName, int honoreeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            return null;
        }

        var extension = Path.GetExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        extension = extension.ToLowerInvariant();
        var finalFileName = $"{honoreeId}{extension}";

        if (string.Equals(sourceFileName, finalFileName, StringComparison.OrdinalIgnoreCase))
        {
            return finalFileName;
        }

        var container = await GetContainerAsync(ImageContainerName, ct);
        var source = container.GetBlobClient(sourceFileName);

        if (!await source.ExistsAsync(ct))
        {
            return null;
        }

        var target = container.GetBlobClient(finalFileName);
        var sourceProperties = await source.GetPropertiesAsync(cancellationToken: ct);

        await using (var sourceStream = await source.OpenReadAsync(cancellationToken: ct))
        {
            await target.UploadAsync(
                sourceStream,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = sourceProperties.Value.ContentType ?? GuessContentType(finalFileName)
                    }
                },
                ct);
        }

        if (sourceFileName.StartsWith("pending/", StringComparison.OrdinalIgnoreCase))
        {
            await source.DeleteIfExistsAsync(cancellationToken: ct);
        }

        return finalFileName;
    }

    public async Task<byte[]?> DownloadImageAsync(string? photoFileName, string? fallbackImageUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(photoFileName))
        {
            var container = await GetContainerAsync(ImageContainerName, ct);
            var blob = container.GetBlobClient(photoFileName);

            if (await blob.ExistsAsync(ct))
            {
                await using var stream = await blob.OpenReadAsync(cancellationToken: ct);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, ct);
                return memory.ToArray();
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackImageUrl) && Uri.TryCreate(fallbackImageUrl, UriKind.Absolute, out var uri))
        {
            try
            {
                using var http = new HttpClient();
                return await http.GetByteArrayAsync(uri, ct);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public async Task<byte[]?> DownloadPdfAsync(int honoreeId, CancellationToken ct)
    {
        var container = await GetContainerAsync(PdfContainerName, ct);
        var blob = container.GetBlobClient(GetHonoreePdfFileName(honoreeId));

        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        await using var stream = await blob.OpenReadAsync(cancellationToken: ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }

    public async Task<string> UploadPdfAsync(int honoreeId, byte[] pdf, CancellationToken ct)
    {
        var fileName = GetHonoreePdfFileName(honoreeId);
        var container = await GetContainerAsync(PdfContainerName, ct);
        var blob = container.GetBlobClient(fileName);

        await using var stream = new MemoryStream(pdf);
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
            },
            ct);

        return fileName;
    }

    private async Task UploadImageBlobAsync(string fileName, IFormFile photo, CancellationToken ct)
    {
        var container = await GetContainerAsync(ImageContainerName, ct);
        var blob = container.GetBlobClient(fileName);

        await using var stream = photo.OpenReadStream();
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = GuessContentType(fileName, photo.ContentType) }
            },
            ct);
    }

    private async Task<BlobContainerClient> GetContainerAsync(string containerName, CancellationToken ct)
    {
        var connectionString = configuration["BlobStorage:ConnectionString"]
            ?? configuration["AzureWebJobsStorage"]
            ?? configuration["StorageConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Missing blob storage connection string. Set BlobStorage__ConnectionString or AzureWebJobsStorage in App Service environment variables.");
        }

        var container = new BlobContainerClient(connectionString, containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        return container;
    }

    private static string GetSafeImageExtension(string fileName, string? contentType)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp")
        {
            return extension;
        }

        return contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
    }

    private static string GuessContentType(string fileName, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => "image/jpeg"
        };
    }
}
