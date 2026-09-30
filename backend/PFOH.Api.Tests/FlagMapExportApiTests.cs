using System.IO.Compression;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class FlagMapExportApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    [Fact]
    public async Task Export_requires_an_administrator()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/admin/review/flag-map-export");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var signedIn = await client.SendAsync(ExportRequest("/api/admin/review/flag-map-export", admin: false));
        Assert.Equal(HttpStatusCode.Forbidden, signedIn.StatusCode);
    }

    [Fact]
    public async Task Export_default_columns_come_from_the_honoree_and_skip_other_fields()
    {
        await ResetAsync();
        await SeedMapAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(ExportRequest("/api/admin/review/flag-map-export", admin: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Contains("spreadsheet", response.Content.Headers.ContentType?.MediaType);

        var headers = ReadHeaders(bytes);
        Assert.Equal(
            [
                "First Name",
                "Last Name",
                "Full Name",
                "Flag Grid",
                "Status",
                "Rank",
                "Service Branch",
                "Sponsor Name",
                "KIA"
            ],
            headers);
        Assert.DoesNotContain(headers, header => InventedHeaders.Contains(header));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        Assert.Equal(["A-01", "A-10", "B-01", "D-05"], ReadColumn(worksheet, 4));

        Assert.Equal("Mary Ann", worksheet.Cell(2, 1).GetString());
        Assert.Equal("Jones III (Skip)", worksheet.Cell(2, 2).GetString());
        Assert.Equal("Mary Ann Jones III (Skip)", worksheet.Cell(2, 3).GetString());
        Assert.Equal("A-01", worksheet.Cell(2, 4).GetString());
        Assert.Equal("Occupied", worksheet.Cell(2, 5).GetString());
        Assert.Equal("SGT", worksheet.Cell(2, 6).GetString());
        Assert.Equal("Army", worksheet.Cell(2, 7).GetString());
        Assert.Equal("Jane Q. Sponsor", worksheet.Cell(2, 8).GetString());
        Assert.Equal("Yes", worksheet.Cell(2, 9).GetString());

        Assert.Equal("Robert", worksheet.Cell(3, 1).GetString());
        Assert.Equal("Jones Jr.", worksheet.Cell(3, 2).GetString());
        Assert.Equal("A-10", worksheet.Cell(3, 4).GetString());
        Assert.Equal("Occupied", worksheet.Cell(3, 5).GetString());
        Assert.Equal(string.Empty, worksheet.Cell(3, 8).GetString());
        Assert.Equal("No", worksheet.Cell(3, 9).GetString());

        Assert.Equal("Zoe", worksheet.Cell(4, 1).GetString());
        Assert.Equal("Adams Sr.", worksheet.Cell(4, 2).GetString());
        Assert.Equal("No", worksheet.Cell(4, 9).GetString());

        Assert.Equal("Ann", worksheet.Cell(5, 1).GetString());
        Assert.Equal("Zulu II", worksheet.Cell(5, 2).GetString());
        Assert.Equal("Reserved", worksheet.Cell(5, 5).GetString());

        var workbookText = ReadWorkbookText(bytes);
        Assert.Contains("Jane Q. Sponsor", workbookText, StringComparison.Ordinal);
        Assert.DoesNotContain("DO-NOT-EXPORT-DESCRIPTION", workbookText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SERVICE-YEARS", workbookText, StringComparison.Ordinal);
        Assert.DoesNotContain("555-0199-SECRET", workbookText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-honoree@example.test", workbookText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-sponsor@example.test", workbookText, StringComparison.Ordinal);
        Assert.DoesNotContain("1 Secret St", workbookText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_omits_unchecked_fields_and_rejects_fields_outside_the_offer_set()
    {
        await ResetAsync();
        await SeedMapAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(ExportRequest(
            "/api/admin/review/flag-map-export?columns=kia,flagGrid,sponsorName",
            admin: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(["Flag Grid", "Sponsor Name", "KIA"], ReadHeaders(bytes));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        Assert.Equal("A-01", worksheet.Cell(2, 1).GetString());
        Assert.Equal("Jane Q. Sponsor", worksheet.Cell(2, 2).GetString());
        Assert.Equal("Yes", worksheet.Cell(2, 3).GetString());
        Assert.Equal(string.Empty, worksheet.Cell(1, 4).GetString());
        Assert.DoesNotContain("Mary Ann", ReadWorkbookText(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("SGT", ReadWorkbookText(bytes), StringComparison.Ordinal);

        var unknown = await client.SendAsync(ExportRequest(
            "/api/admin/review/flag-map-export?columns=firstName,nickname,description",
            admin: true));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var empty = await client.SendAsync(ExportRequest(
            "/api/admin/review/flag-map-export?columns=",
            admin: true));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    private async Task SeedMapAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        var sponsorCategory = new SponsorCategory
        {
            SponsorCategoryName = "Family",
            Description = "category",
            CreatedBy = "test",
            ModifiedBy = "test",
            CreatedDate = now,
            ModifiedDate = now
        };
        var branchCategory = new ServiceBranchCategory
        {
            ServiceBranchCategoryName = "Military",
            Description = "category",
            CreatedBy = "test",
            ModifiedBy = "test",
            CreatedDate = now,
            ModifiedDate = now
        };
        var army = new ServiceBranch
        {
            ServiceBranchName = "Army",
            Description = "branch",
            LogoFileName = "",
            ServiceBranchCategory = branchCategory,
            CreatedBy = "test",
            ModifiedBy = "test",
            CreatedDate = now,
            ModifiedDate = now
        };
        var sponsor = new Sponsor
        {
            SponsorCategory = sponsorCategory,
            Description = "sponsor-notes",
            FirstName = "Jane",
            MiddleName = "Q.",
            LastName = "Sponsor",
            Salutation = "",
            PhoneNumber = "555-0101-SPONSOR",
            EmailAddress = "secret-sponsor@example.test",
            StreetAddress = "1 Secret St",
            City = "Plano",
            State = "TX",
            ZipCode = "75000",
            IsActive = true,
            CreatedBy = "test",
            ModifiedBy = "test",
            CreatedDate = now,
            ModifiedDate = now
        };

        var a01 = Grid("A-01", now);
        var a02 = Grid("A-02", now);
        var a10 = Grid("A-10", now);
        var b01 = Grid("B-01", now);
        var c01 = Grid("C-01", now, reserved: true);
        var d05 = Grid("D-05", now, reserved: true);

        db.Honorees.Add(Honoree(
            "Mary Ann",
            "Jones",
            now,
            grid: a01,
            branch: army,
            sponsor: sponsor,
            suffix: "III",
            nickname: "Skip",
            rank: "SGT",
            kia: true,
            description: "DO-NOT-EXPORT-DESCRIPTION",
            dates: "SECRET-SERVICE-YEARS",
            phone: "555-0199-SECRET",
            email: "secret-honoree@example.test"));
        db.Honorees.Add(Honoree(
            "Robert",
            "Jones",
            now,
            grid: a10,
            branch: army,
            suffix: "Jr.",
            rank: "CPL"));
        db.Honorees.Add(Honoree(
            "Zoe",
            "Adams",
            now,
            grid: b01,
            branch: army,
            suffix: "Sr.",
            rank: "PO1"));
        db.Honorees.Add(Honoree(
            "Ann",
            "Zulu",
            now,
            grid: d05,
            branch: army,
            suffix: "II",
            rank: "Lt"));
        db.Honorees.Add(Honoree(
            "Hidden",
            "OpenGrid",
            now,
            grid: a02,
            branch: army,
            rank: "PVT",
            active: false));
        db.FlagGrids.Add(c01);
        await db.SaveChangesAsync();

        a01.HonoreeId = await HonoreeIdAsync(db, "Jones", "Mary Ann");
        a10.HonoreeId = await HonoreeIdAsync(db, "Jones", "Robert");
        b01.HonoreeId = await HonoreeIdAsync(db, "Adams");
        d05.HonoreeId = await HonoreeIdAsync(db, "Zulu");
        await db.SaveChangesAsync();
    }

    private static FlagGrid Grid(string name, DateTime now, bool reserved = false) => new()
    {
        FlagGridName = name,
        Reserved = reserved,
        Notes = "",
        CreatedBy = "test",
        ModifiedBy = "test",
        CreatedDate = now,
        ModifiedDate = now
    };

    private static Honoree Honoree(
        string firstName,
        string lastName,
        DateTime now,
        FlagGrid grid,
        ServiceBranch branch,
        Sponsor? sponsor = null,
        string? suffix = null,
        string? nickname = null,
        string rank = "",
        bool kia = false,
        string description = "",
        string dates = "",
        string phone = "",
        string email = "",
        bool active = true) => new()
    {
        FirstName = firstName,
        LastName = lastName,
        Suffix = suffix,
        Nickname = nickname,
        Rank = rank,
        KIA = kia,
        Description = description,
        DatesUserEntry = dates,
        PhoneNumber = phone,
        EmailAddress = email,
        Salutation = "",
        Awards = "",
        ConflictsServed = "",
        NameUserEntry = "",
        PhotoFileName = "",
        IsActive = active,
        FlagGrid = grid,
        ServiceBranch = branch,
        Sponsor = sponsor,
        CreatedBy = "test",
        ModifiedBy = "test",
        CreatedDate = now,
        ModifiedDate = now
    };

    private static async Task<int> HonoreeIdAsync(PfohDbContext db, string lastName, string? firstName = null)
    {
        return await db.Honorees
            .Where(honoree => honoree.LastName == lastName && (firstName == null || honoree.FirstName == firstName))
            .Select(honoree => honoree.Id)
            .SingleAsync();
    }

    private static HttpRequestMessage ExportRequest(string url, bool admin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.OidHeader, admin ? "admin-1" : "user-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, admin ? "admin@example.com" : "user@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, admin ? "Admin" : "User");
        if (admin)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
        }

        return request;
    }

    private static string[] ReadHeaders(byte[] bytes)
    {
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var worksheet = workbook.Worksheet(FlagMapExcelExporter.SheetName);
        var headers = new List<string>();
        for (var column = 1; worksheet.Cell(1, column).GetString().Length > 0; column++)
        {
            headers.Add(worksheet.Cell(1, column).GetString());
        }

        return headers.ToArray();
    }

    private static string[] ReadColumn(IXLWorksheet worksheet, int column)
    {
        var values = new List<string>();
        for (var row = 2; worksheet.Cell(row, column).GetString().Length > 0; row++)
        {
            values.Add(worksheet.Cell(row, column).GetString());
        }

        return values.ToArray();
    }

    private static string ReadWorkbookText(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private static readonly string[] InventedHeaders =
    [
        "Middle Name",
        "Nickname",
        "Description",
        "Service Years",
        "Submitter",
        "Claimants",
        "Honoree name"
    ];
}
