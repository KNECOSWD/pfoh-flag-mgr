using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public sealed class PdfOpenResult
{
    public bool NotFound { get; init; }

    public bool Inactive { get; init; }

    public bool StorageFailed { get; init; }

    public bool RebuildFailed { get; init; }

    public string? Message { get; init; }

    public byte[]? Bytes { get; init; }

    public string? Stamp { get; init; }

    public bool Rebuilt { get; init; }

    public static PdfOpenResult Missing(bool inactive) => new()
    {
        NotFound = true,
        Inactive = inactive,
        Message = inactive
            ? "This honoree is not active, so the card PDF is not available."
            : "Honoree was not found."
    };
}

/// <summary>
/// One rebuild path for honoree card PDFs. A per-honoree lock keeps two
/// opens from writing the same blob at once. A failed rebuild leaves the
/// honoree row as it was. The next open tries again and does not return a
/// PDF whose stamp is out of date.
/// </summary>
public sealed class HonoreePdfService(
    PfohDbContext db,
    IWebHostEnvironment environment,
    HonoreeFileStorage fileStorage,
    IHonoreeCardRenderer renderer)
{
    public const string RebuildFailedMessage =
        "The card PDF could not be rebuilt. The change was saved, and the next open will try again.";

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Locks = new();

    public async Task<string?> TryRebuildAsync(int honoreeId, CancellationToken ct, bool includeInactive = false)
    {
        try
        {
            return await WithLock(honoreeId, () => RebuildUnlockedAsync(honoreeId, includeInactive, ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return RebuildFailedMessage;
        }
    }

    public Task<PdfOpenResult> OpenAsync(int honoreeId, CancellationToken ct) =>
        WithLock(honoreeId, () => OpenUnlockedAsync(honoreeId, ct), ct);

    public static void ApplyFreshnessHeaders(HttpResponse response, string? stamp)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        if (!string.IsNullOrWhiteSpace(stamp))
        {
            response.Headers.ETag = $"\"{stamp}\"";
        }
    }

    public static string? CacheBust(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{separator}v={DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private async Task<string?> RebuildUnlockedAsync(int honoreeId, bool includeInactive, CancellationToken ct)
    {
        var loaded = await LoadAsync(honoreeId, ct);
        if (loaded is null)
        {
            return null;
        }

        if ((!loaded.Honoree.IsActive || loaded.Honoree.DeletedDate != null) && !includeInactive)
        {
            return null;
        }

        var stamp = await StampAsync(loaded, ct);
        StoredPdf? stored;
        try
        {
            stored = await fileStorage.ReadStoredPdfAsync(honoreeId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            stored = null;
        }

        if (stored is { Bytes.Length: > 0 } && string.Equals(stored.Stamp, stamp, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var pdf = await RenderAsync(loaded, ct);
            await fileStorage.SaveStoredPdfAsync(honoreeId, pdf, stamp, ct);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return RebuildFailedMessage;
        }
    }

    private async Task<PdfOpenResult> OpenUnlockedAsync(int honoreeId, CancellationToken ct)
    {
        var loaded = await LoadAsync(honoreeId, ct);
        if (loaded is null)
        {
            return PdfOpenResult.Missing(inactive: false);
        }

        if (!loaded.Honoree.IsActive || loaded.Honoree.DeletedDate != null)
        {
            return PdfOpenResult.Missing(inactive: true);
        }

        var stamp = await StampAsync(loaded, ct);
        StoredPdf? stored;
        try
        {
            stored = await fileStorage.ReadStoredPdfAsync(honoreeId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PdfOpenResult
            {
                StorageFailed = true,
                Message = ex.Message
            };
        }

        if (stored is { Bytes.Length: > 0 } && string.Equals(stored.Stamp, stamp, StringComparison.Ordinal))
        {
            return new PdfOpenResult
            {
                Bytes = stored.Bytes,
                Stamp = stamp
            };
        }

        try
        {
            var pdf = await RenderAsync(loaded, ct);
            await fileStorage.SaveStoredPdfAsync(honoreeId, pdf, stamp, ct);
            return new PdfOpenResult
            {
                Bytes = pdf,
                Stamp = stamp,
                Rebuilt = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PdfOpenResult
            {
                RebuildFailed = true,
                Stamp = stamp,
                Message = ex.Message
            };
        }
    }

    private async Task<byte[]> RenderAsync(LoadedHonoree loaded, CancellationToken ct)
    {
        var photo = await fileStorage.DownloadImageAsync(loaded.Honoree.PhotoFileName, loaded.Search?.ImageUrl, ct);
        var assets = await fileStorage.LoadReportAssetsAsync(loaded.Honoree, loaded.Search, ct);
        return renderer.Render(environment, loaded.Honoree, loaded.Search, photo, assets);
    }

    private async Task<string> StampAsync(LoadedHonoree loaded, CancellationToken ct)
    {
        var etag = await fileStorage.GetPhotoEtagAsync(loaded.Honoree.PhotoFileName, ct);
        var sponsor = loaded.Honoree.Sponsor is null
            ? null
            : string.Join(
                " ",
                new[] { loaded.Honoree.Sponsor.FirstName, loaded.Honoree.Sponsor.LastName }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
        return HonoreePdfStamp.Compute(
            loaded.Honoree,
            loaded.Honoree.FlagGrid?.FlagGridName,
            loaded.Honoree.ServiceBranch?.ServiceBranchName,
            sponsor,
            etag);
    }

    private async Task<LoadedHonoree?> LoadAsync(int honoreeId, CancellationToken ct)
    {
        var honoree = await db.Honorees
            .AsNoTracking()
            .Include(item => item.FlagGrid)
            .Include(item => item.ServiceBranch)
            .Include(item => item.Sponsor)
            .FirstOrDefaultAsync(item => item.Id == honoreeId, ct);
        if (honoree is null)
        {
            return null;
        }

        var search = await db.HonoreeSearchResults
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == honoreeId, ct);
        return new LoadedHonoree(honoree, search);
    }

    private static async Task<T> WithLock<T>(int honoreeId, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(honoreeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record LoadedHonoree(Honoree Honoree, HonoreeSearchResult? Search);
}
