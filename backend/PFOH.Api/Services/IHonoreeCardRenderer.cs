using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public interface IHonoreeCardRenderer
{
    byte[] Render(
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        HonoreeReportAssets? assets);
}

public sealed class HonoreeCardRenderer(ILogger<HonoreeCardRenderer> logger) : IHonoreeCardRenderer
{
    public byte[] Render(
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        HonoreeReportAssets? assets) =>
        HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes, assets, logger);
}
