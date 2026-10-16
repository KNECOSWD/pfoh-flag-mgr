using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public enum HonoreeImageProbeResult
{
    NoPhoto,
    Found,
    Missing,
    Failed
}

public class HonoreeFileStorage(IConfiguration configuration)
{
    public const string ImageContainerName = "honoreeimages";
    public const string PdfContainerName = "honoreepdfs";
    public const string ServiceLogosContainerName = "servicelogos";

    public string GetHonoreePdfFileName(int honoreeId) => $"{honoreeId}_HonoreeReport.pdf";

    public string GetPdfContainerName() => PdfContainerName;

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

    public virtual async Task<string?> UploadPendingImageAsync(IFormFile? photo, int changeRequestId, CancellationToken ct)
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

    public virtual async Task DeletePendingImageAsync(string? photoFileName, CancellationToken ct)
    {
        var name = NormalizePendingName(photoFileName);
        if (name is null)
        {
            return;
        }

        try
        {
            var container = await GetExistingContainerAsync(ImageContainerName, ct);
            if (container is null)
            {
                return;
            }

            await container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch
        {
            // Withdrawing or superseding a submission still commits when storage is down.
        }
    }

    public static bool IsPendingPhotoName(string? photoFileName) =>
        NormalizePendingName(photoFileName) is not null;

    public static string? NormalizePendingName(string? photoFileName)
    {
        if (string.IsNullOrWhiteSpace(photoFileName))
        {
            return null;
        }

        var name = photoFileName.Trim().Replace('\\', '/');
        if (!name.StartsWith("pending/", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return name;
    }

    public virtual async Task<string?> PromoteImageAsync(string? sourceFileName, int honoreeId, CancellationToken ct)
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

        await target.DeleteIfExistsAsync(cancellationToken: ct);

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
        // All report graphics should live in the servicelogos container.
        // DonorCardBlankLrg.png is the production honoree-card background.
        var background =
            await DownloadServiceLogoAsync("DonorCardBlankLrg.png", ct) ??
            await DownloadServiceLogoAsync("DonorCardBlank.png", ct);

        var rotary =
            await DownloadServiceLogoAsync("Rotary.png", ct) ??
            await DownloadServiceLogoAsync("Rotary.jpg", ct);

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

        var silhouette = await LoadSilhouetteAsync(honoree.ServiceBranch?.ServiceBranchName ?? searchResult?.ServiceBranchName, ct);

        return new HonoreeReportAssets(background, rotary, serviceLogo, silhouette);
    }

    private async Task<byte[]?> LoadSilhouetteAsync(string? branchName, CancellationToken ct)
    {
        foreach (var candidate in SilhouetteCandidates(branchName))
        {
            // Silhouettes are treated like photo substitutes, so check honoreeimages first.
            var silhouette = await DownloadFromExistingContainerAsync(ImageContainerName, candidate, ct)
                ?? await DownloadServiceLogoAsync(candidate, ct);

            if (silhouette is not null && silhouette.Length > 0)
            {
                return silhouette;
            }
        }

        return null;
    }

    private static IEnumerable<string> SilhouetteCandidates(string? branchName)
    {
        var value = branchName?.ToLowerInvariant() ?? string.Empty;

        if (value.Contains("navy"))
        {
            yield return "NavySilhouette.png";
            yield return "SailorSilhouette.png";
        }

        if (value.Contains("marine"))
        {
            yield return "MarineCorpsSilhouette.png";
            yield return "MarineSilhouette.png";
        }

        if (value.Contains("air force"))
        {
            yield return "AirForceSilhouette.png";
            yield return "AirmanSilhouette.png";
        }

        if (value.Contains("space"))
        {
            yield return "SpaceForceSilhouette.png";
        }

        if (value.Contains("coast guard"))
        {
            yield return "CoastGuardSilhouette.png";
        }

        if (value.Contains("fire"))
        {
            yield return "FirefighterSilhouette.png";
            yield return "FireAndRescueSilhouette.png";
        }

        if (value.Contains("police") || value.Contains("law enforcement"))
        {
            yield return "PoliceSilhouette.png";
            yield return "LawEnforcementSilhouette.png";
        }

        if (value.Contains("first responder") || value.Contains("ems") || value.Contains("medical"))
        {
            yield return "FirstResponderSilhouette.png";
            yield return "EMSSilhouette.png";
        }

        if (value.Contains("army") || value.Contains("cavalry") || value.Contains("signal") || value.Contains("national guard"))
        {
            yield return "ArmySilhouette.png";
            yield return "SoldierSilhouette.png";
        }

        // Generic final fallback for any missing/unknown branch.
        yield return "GenericSoldierSilhouette.png";
        yield return "SoldierSilhouette.png";
        yield return "MilitarySilhouette.png";
    }

    public virtual async Task<byte[]?> DownloadServiceLogoAsync(string? fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var cleanFileName = fileName.Replace("\r", "").Replace("\n", "").Trim();

        foreach (var candidate in RasterFileCandidates(cleanFileName))
        {
            var bytes = await DownloadFromExistingContainerAsync(ServiceLogosContainerName, candidate, ct);

            if (bytes is not null && bytes.Length > 0)
            {
                return bytes;
            }
        }

        return null;
    }

    public virtual async Task<HonoreeImageProbeResult> ProbeHonoreeImageAsync(string? photoFileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(photoFileName))
        {
            return HonoreeImageProbeResult.NoPhoto;
        }

        try
        {
            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return HonoreeImageProbeResult.Failed;
            }

            var container = new BlobContainerClient(connectionString, ImageContainerName);
            var blob = container.GetBlobClient(photoFileName);
            return await blob.ExistsAsync(ct)
                ? HonoreeImageProbeResult.Found
                : HonoreeImageProbeResult.Missing;
        }
        catch (Exception)
        {
            return HonoreeImageProbeResult.Failed;
        }
    }

    public virtual async Task<HonoreeImageContent?> OpenHonoreeImageAsync(string? photoFileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(photoFileName))
        {
            return null;
        }

        var container = GetReadContainer(ImageContainerName);
        var blob = container.GetBlobClient(photoFileName);

        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        var properties = await blob.GetPropertiesAsync(cancellationToken: ct);
        var contentType = properties.Value.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) ||
            contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            contentType = GuessContentType(photoFileName);
        }

        var stream = await blob.OpenReadAsync(cancellationToken: ct);
        return new HonoreeImageContent(stream, contentType);
    }

    public virtual async Task<byte[]?> DownloadImageAsync(string? photoFileName, string? fallbackImageUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(photoFileName))
        {
            var container = GetReadContainer(ImageContainerName);
            var blob = container.GetBlobClient(photoFileName);

            if (await blob.ExistsAsync(ct))
            {
                await using var stream = await blob.OpenReadAsync(cancellationToken: ct);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, ct);
                return memory.ToArray();
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackImageUrl) &&
            Uri.TryCreate(fallbackImageUrl, UriKind.Absolute, out var uri))
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

        // Always overwrite. The PDF is a rendered snapshot of the current honoree record.
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

    private async Task<byte[]?> DownloadFromExistingContainerAsync(
        string containerName,
        string fileName,
        CancellationToken ct)
    {
        try
        {
            var container = await GetExistingContainerAsync(containerName, ct);
            if (container is null)
            {
                return null;
            }

            var blob = container.GetBlobClient(fileName);

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

    private BlobContainerClient GetReadContainer(string containerName)
    {
        var connectionString = GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Missing BlobStorage__ConnectionString. Set this App Service environment variable to the connection string for the storage account that contains honoreepdfs, honoreeimages, and servicelogos.");
        }

        // Reads must not call CreateIfNotExists. That PUT returns 409 for a
        // container that already exists and was showing up on every photo request.
        return new BlobContainerClient(connectionString, containerName);
    }

    private async Task<BlobContainerClient> GetContainerAsync(string containerName, CancellationToken ct)
    {
        var container = GetReadContainer(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        return container;
    }

    private string? GetConnectionString()
    {
        // Use only the dedicated Plano Flags storage connection string.
        // Do not silently fall back to AzureWebJobsStorage because that can write to the wrong account.
        return configuration["BlobStorage:ConnectionString"];
    }

    private static IEnumerable<string> RasterFileCandidates(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            yield break;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension is ".png" or ".jpg" or ".jpeg")
        {
            yield return fileName;
            yield break;
        }

        var withoutExtension = Path.Combine(
            Path.GetDirectoryName(fileName) ?? string.Empty,
            Path.GetFileNameWithoutExtension(fileName));

        yield return $"{withoutExtension}.png";
        yield return $"{withoutExtension}.jpg";
        yield return $"{withoutExtension}.jpeg";
    }

    public static bool IsPublicLogoFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = fileName.Trim();
        if (name.Length is < 1 or > 120)
        {
            return false;
        }

        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            name,
            @"^[A-Za-z0-9][A-Za-z0-9._-]*\.(png|jpg|jpeg|gif|webp)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public static readonly HashSet<string> FallbackLogoFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AirCorps.png",
        "ArmyAirCorps.png",
        "NationalGuard.png",
        "CoastGuard.png",
        "FireAndRescue.png",
        "LawEnforcement.png",
        "FirstResponder.png",
        "MarineCorps.png",
        "MerchantMarine.png",
        "SpaceForce.png",
        "AirForce.png",
        "Navy.png",
        "Army.png",
        "SignalCorps.png",
        "Cavalry.png",
        "MilitaryService.png"
    };

    public static IEnumerable<string> ServiceLogoCandidates(string? branchName)
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

public sealed record HonoreeImageContent(Stream Stream, string ContentType);
