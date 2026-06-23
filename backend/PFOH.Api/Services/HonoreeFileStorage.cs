using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public class HonoreeFileStorage(IConfiguration configuration)
{
    public const string ImageContainerName = "honoreeimages";
    public const string PdfContainerName = "honoreepdfs";
    public const string ServiceLogosContainerName = "servicelogos";

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

    public async Task<HonoreeReportAssets> LoadReportAssetsAsync(
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        CancellationToken ct)
    {
        var background =
            await DownloadServiceLogoAsync("DonorCardBlank.png", ct) ??
            await DownloadServiceLogoAsync("DonorCardBlankLrg.png", ct);

        var rotary = await DownloadServiceLogoAsync("Rotary.png", ct);

        var serviceLogo = await DownloadServiceLogoAsync(honoree.ServiceBranch?.LogoFileName, ct);

        if (serviceLogo is null)
        {
            foreach (var candidate in ServiceLogoCandidates(honoree.ServiceBranch?.ServiceBranchName ?? searchResult?.ServiceBranchName))
            {
                serviceLogo = await DownloadServiceLogoAsync(candidate, ct);

                if (serviceLogo is not null)
                {
                    break;
                }
            }
        }

        return new HonoreeReportAssets(background, rotary, serviceLogo);
    }

    public async Task<byte[]?> DownloadServiceLogoAsync(string? fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var cleanFileName = fileName.Replace("\r", "").Replace("\n", "").Trim();

        try
        {
            var container = await GetExistingContainerAsync(ServiceLogosContainerName, ct);
            if (container is null)
            {
                return null;
            }

            var blob = container.GetBlobClient(cleanFileName);

            if (!await blob.ExistsAsync(ct))
            {
                return null;
            }

            await using var stream = await blob.OpenReadAsync(cancellationToken: ct);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
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

        // The PDF is a rendered copy of the current honoree record.
        // When the record changes, replace the old stored PDF with the new one.
        await blob.DeleteIfExistsAsync(cancellationToken: ct);

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

    private async Task<BlobContainerClient?> GetExistingContainerAsync(string containerName, CancellationToken ct)
    {
        var connectionString = GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var container = new BlobContainerClient(connectionString, containerName);

        if (!await container.ExistsAsync(ct))
        {
            return null;
        }

        return container;
    }

    private static IEnumerable<string> ServiceLogoCandidates(string? branchName)
    {
        var value = branchName?.ToLowerInvariant() ?? string.Empty;

        if (value.Contains("air corps"))
        {
            yield return "AirCorps.png";
            yield return "ArmyAirCorps.png";
        }

        if (value.Contains("army air"))
        {
            yield return "ArmyAirCorps.png";
            yield return "AirCorps.png";
        }

        if (value.Contains("national guard")) yield return "NationalGuard.png";
        if (value.Contains("coast guard")) yield return "CoastGuard.png";
        if (value.Contains("fire")) yield return "FireAndRescue.png";
        if (value.Contains("police") || value.Contains("law enforcement")) yield return "LawEnforcement.png";
        if (value.Contains("first responder")) yield return "FirstResponder.png";
        if (value.Contains("marine corps")) yield return "MarineCorps.png";
        if (value.Contains("merchant")) yield return "MerchantMarine.png";
        if (value.Contains("space")) yield return "SpaceForce.png";
        if (value.Contains("air force")) yield return "AirForce.png";
        if (value.Contains("navy")) yield return "Navy.png";
        if (value.Contains("army")) yield return "Army.png";
        if (value.Contains("signal")) yield return "SignalCorps.png";
        if (value.Contains("cavalry")) yield return "Cavalry.png";

        yield return "MilitaryService.png";
    }

    private async Task<BlobContainerClient> GetContainerAsync(string containerName, CancellationToken ct)
    {
        var connectionString = GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Missing BlobStorage__ConnectionString. Set this App Service environment variable to the connection string for the storage account that contains honoreepdfs, honoreeimages, and servicelogos.");
        }

        var container = new BlobContainerClient(connectionString, containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        return container;
    }

    private string? GetConnectionString()
    {
        // Use only the dedicated project storage connection string.
        // Do not fall back to AzureWebJobsStorage, because that can silently
        // save PDFs to the wrong storage account while the UI reports success.
        return configuration["BlobStorage:ConnectionString"];
    }

    public string GetConfiguredStorageAccountName()
    {
        var connectionString = GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "BlobStorage__ConnectionString not configured";
        }

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 &&
                pieces[0].Equals("AccountName", StringComparison.OrdinalIgnoreCase))
            {
                return pieces[1];
            }

            if (pieces.Length == 2 &&
                pieces[0].Equals("BlobEndpoint", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(pieces[1], UriKind.Absolute, out var endpoint))
            {
                return endpoint.Host.Split('.')[0];
            }
        }

        return "configured storage account";
    }

    public string GetPdfContainerName() => PdfContainerName;

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
