using Microsoft.AspNetCore.Hosting;
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

public sealed class HonoreeCardRenderer : IHonoreeCardRenderer
{
    public byte[] Render(
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        HonoreeReportAssets? assets) =>
        HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes, assets);
}
