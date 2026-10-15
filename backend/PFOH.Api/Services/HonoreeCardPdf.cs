using PFOH.Api.Models;

namespace PFOH.Api.Services;

public static class HonoreeCardPdf
{
    public static Honoree FromChangeRequest(HonoreeChangeRequest change) => new()
    {
        FirstName = change.FirstName,
        MiddleName = change.MiddleName,
        LastName = change.LastName,
        Suffix = change.Suffix,
        Nickname = change.Nickname,
        Rank = change.Rank ?? string.Empty,
        ServiceBranchId = change.ServiceBranchId,
        ServiceBranchCategoryId = change.ServiceBranchCategoryId,
        ServiceBranch = change.ServiceBranch,
        StartYear = change.StartYear,
        EndYear = change.EndYear,
        DatesUserEntry = change.DatesUserEntry ?? string.Empty,
        ConflictsServed = change.ConflictsServed ?? string.Empty,
        Awards = change.Awards ?? string.Empty,
        Description = change.Description ?? string.Empty,
        KIA = change.KIA,
        PhotoFileName = change.PhotoFileName ?? string.Empty,
        FlagGridId = change.FlagGridId,
        FlagGrid = change.FlagGrid,
        IsActive = true
    };

    public static async Task<byte[]> RenderAsync(
        IWebHostEnvironment environment,
        HonoreeFileStorage fileStorage,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        string? photoFileName,
        string? fallbackImageUrl,
        CancellationToken ct)
    {
        byte[]? photoBytes = null;
        try
        {
            photoBytes = await fileStorage.DownloadImageAsync(photoFileName, fallbackImageUrl, ct);
        }
        catch
        {
            photoBytes = null;
        }

        HonoreeReportAssets? assets = null;
        try
        {
            assets = await fileStorage.LoadReportAssetsAsync(honoree, searchResult, ct);
        }
        catch
        {
            assets = null;
        }

        return HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes, assets);
    }
}
