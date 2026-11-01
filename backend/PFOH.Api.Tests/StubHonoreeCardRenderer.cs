using System.Text;
using Microsoft.AspNetCore.Hosting;
using PFOH.Api.Models;
using PFOH.Api.Services;

namespace PFOH.Api.Tests;

public sealed class StubHonoreeCardRenderer : IHonoreeCardRenderer
{
    public bool Fail { get; set; }

    public int Calls { get; set; }

    public byte[] Render(
        IWebHostEnvironment environment,
        Honoree honoree,
        HonoreeSearchResult? searchResult,
        byte[]? photoBytes,
        HonoreeReportAssets? assets)
    {
        Calls++;
        if (Fail)
        {
            throw new InvalidOperationException("Synthetic PDF failure.");
        }

        return Encoding.UTF8.GetBytes(
            $"%PDF-1.4\n{honoree.Id}|{honoree.FirstName}|{honoree.LastName}|{honoree.PhotoFileName}|{(honoree.IsActive ? 1 : 0)}");
    }

    public void Reset()
    {
        Fail = false;
        Calls = 0;
    }
}
