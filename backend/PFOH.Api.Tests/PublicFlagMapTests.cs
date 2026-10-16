using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class PublicFlagMapTests : IClassFixture<PfohApiFactory>
{
    private readonly PfohApiFactory factory;

    public PublicFlagMapTests(PfohApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        if (!db.FlagGrids.Any())
        {
            Seed(db);
        }

        factory.Files.Logos["Army.png"] = [0x89, 0x50, 0x4E, 0x47];
        factory.Files.Logos["DonorCardBlankLrg.png"] = [0x89, 0x50, 0x4E, 0x47];
        factory.Files.Logos["NotASeal.png"] = [0x89, 0x50, 0x4E, 0x47];
    }

    private static readonly string[] SecretValues =
    [
        "555-0100-SECRET",
        "secret-submitter@example.test",
        "Secret Sponsor",
        "SECRET-GRID-NOTE",
        "SECRET-REVIEW-NOTE",
        "secret-photo.jpg",
        "Pending Only Person",
        "Inactive Person",
        "Deleted Person",
        "Gone Grid Person",
        "Unplaced Person",
        "SECRET NAME ENTRY",
        "SECRET LOCATION"
    ];

    private static readonly string[] ExcludedNames =
    [
        "phoneNumber",
        "emailAddress",
        "sponsorName",
        "sponsorId",
        "salutation",
        "nameUserEntry",
        "flagLocation",
        "photoFileName",
        "legacyKey",
        "notes",
        "reviewNotes",
        "adminNotes",
        "submitterPhoneNumber",
        "submitterEmailAddress",
        "createdBy",
        "modifiedBy",
        "deletedBy",
        "externalUserEmail",
        "externalUserName",
        "requestStatus",
        "claimStatus"
    ];

    [Fact]
    public async Task Public_payloads_keep_the_allowlist_and_drop_private_fields()
    {
        var client = factory.CreateClient();
        var sections = await client.GetStringAsync("/api/public/flag-map/sections");
        var seats = await client.GetStringAsync("/api/public/flag-map/sections/A");
        var index = await client.GetStringAsync("/api/public/flag-map/search-index");
        var combined = sections + seats + index;

        foreach (var secret in SecretValues)
        {
            Assert.DoesNotContain(secret, combined, StringComparison.Ordinal);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(JsonDocument.Parse(sections).RootElement, names);
        CollectNames(JsonDocument.Parse(seats).RootElement, names);
        CollectNames(JsonDocument.Parse(index).RootElement, names);
        foreach (var excluded in ExcludedNames)
        {
            Assert.DoesNotContain(excluded, names);
        }

        using var seatDoc = JsonDocument.Parse(seats);
        var publicSeat = seatDoc.RootElement.GetProperty("seats").EnumerateArray()
            .Single(seat => seat.GetProperty("flagGridName").GetString() == "A-14");
        Assert.Equal("Public Example", publicSeat.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, publicSeat.GetProperty("rank").ValueKind);
        Assert.Equal("Public bio", publicSeat.GetProperty("description").GetString());
        Assert.Equal("Army", publicSeat.GetProperty("serviceBranchName").GetString());
        Assert.Equal("/api/public/flag-map/logos/Army.png", publicSeat.GetProperty("logoUrl").GetString());
        Assert.Contains("/photo", publicSeat.GetProperty("photoUrl").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-photo", publicSeat.GetProperty("photoUrl").GetString(), StringComparison.Ordinal);

        var openSeat = seatDoc.RootElement.GetProperty("seats").EnumerateArray()
            .Single(seat => seat.GetProperty("flagGridName").GetString() == "A-01");
        Assert.Equal("Open", openSeat.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, openSeat.GetProperty("displayName").ValueKind);

        var reservedSeat = seatDoc.RootElement.GetProperty("seats").EnumerateArray()
            .Single(seat => seat.GetProperty("flagGridName").GetString() == "A-02");
        Assert.Equal("Reserved", reservedSeat.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reservedSeat.GetProperty("displayName").ValueKind);
    }

    [Fact]
    public async Task Search_index_lists_active_honorees_with_section_and_branch()
    {
        var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/public/flag-map/search-index"));
        var hits = doc.RootElement.EnumerateArray().ToList();
        var hit = Assert.Single(hits, item => item.GetProperty("displayName").GetString() == "Public Example");
        Assert.Equal("A", hit.GetProperty("section").GetString());
        Assert.Equal("A-14", hit.GetProperty("flagGridName").GetString());
        Assert.Equal("Army", hit.GetProperty("serviceBranchName").GetString());
        Assert.Equal(JsonValueKind.Null, hit.GetProperty("rank").ValueKind);
        Assert.DoesNotContain(hits, item => item.GetProperty("displayName").GetString() == "Pending Only Person");
    }

    [Fact]
    public async Task Inactive_deleted_and_ungridded_honorees_stay_off_the_map()
    {
        var client = factory.CreateClient();
        var body = await client.GetStringAsync("/api/public/flag-map/sections/A")
            + await client.GetStringAsync("/api/public/flag-map/sections/AA")
            + await client.GetStringAsync("/api/public/flag-map/search-index");

        Assert.Contains("Public Example", body, StringComparison.Ordinal);
        Assert.Contains("Second Example", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Inactive Person", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Deleted Person", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Gone Grid Person", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Unplaced Person", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_send_an_etag_and_honor_if_none_match()
    {
        var client = factory.CreateClient();
        var first = await client.GetAsync("/api/public/flag-map/sections");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("public, max-age=60", first.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));
        Assert.Equal("noindex, nofollow", first.Headers.GetValues("X-Robots-Tag").Single());

        var again = new HttpRequestMessage(HttpMethod.Get, "/api/public/flag-map/sections");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Logo_route_serves_only_branch_seal_files()
    {
        var client = factory.CreateClient();
        var logo = await client.GetAsync("/api/public/flag-map/logos/Army.png");
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal("noindex, nofollow", logo.Headers.GetValues("X-Robots-Tag").Single());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/flag-map/logos")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/flag-map/logos/NotASeal.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/flag-map/logos/DonorCardBlankLrg.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/flag-map/logos/..%2Fsecret.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/flag-map/logos/notes.txt")).StatusCode);
    }

    [Fact]
    public async Task Flag_map_page_sends_the_robots_header()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/flag-map");
        Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
    }

    private static void Seed(PfohDbContext db)
    {
        var category = new ServiceBranchCategory
        {
            ServiceBranchCategoryName = "Armed forces",
            Description = "EXAMPLE",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.ServiceBranchCategories.Add(category);
        db.SaveChanges();

        var army = new ServiceBranch
        {
            ServiceBranchCategoryId = category.Id,
            ServiceBranchName = "Army",
            Description = "EXAMPLE branch",
            LogoFileName = "Army.png",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.ServiceBranches.Add(army);
        db.SaveChanges();

        var sponsor = new Sponsor
        {
            SponsorCategoryId = 1,
            Description = "hidden",
            FirstName = "Secret",
            LastName = "Sponsor",
            Salutation = "Mx",
            PhoneNumber = "555-0100-SECRET",
            EmailAddress = "secret-submitter@example.test",
            StreetAddress = "1 Hidden",
            City = "Plano",
            State = "TX",
            ZipCode = "75024",
            IsActive = true,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.Sponsors.Add(sponsor);

        FlagGrid Grid(string name, bool reserved = false, DateTime? deleted = null) => new()
        {
            FlagGridName = name,
            Reserved = reserved,
            Notes = "SECRET-GRID-NOTE",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow,
            DeletedDate = deleted
        };

        var open = Grid("A-01");
        var reserved = Grid("A-02", reserved: true);
        var occupied = Grid("A-14");
        var second = Grid("AA-03");
        var deletedGrid = Grid("B-09", deleted: DateTime.UtcNow);
        db.FlagGrids.AddRange(open, reserved, occupied, second, deletedGrid);
        db.SaveChanges();

        Honoree Person(string first, string last, FlagGrid? grid, bool active, DateTime? deleted, string rank = "") => new()
        {
            FirstName = first,
            LastName = last,
            Rank = rank,
            Description = first == "Public" ? "Public bio" : "hidden bio",
            ConflictsServed = first == "Public" ? "EXAMPLE conflict" : "",
            Awards = first == "Public" ? "EXAMPLE award" : "",
            PhoneNumber = "555-0100-SECRET",
            EmailAddress = "secret-submitter@example.test",
            Salutation = "Mx",
            NameUserEntry = "SECRET NAME ENTRY",
            DatesUserEntry = "",
            PhotoFileName = first == "Public" ? "secret-photo.jpg" : "",
            FlagLocation = "SECRET LOCATION",
            IsActive = active,
            DeletedDate = deleted,
            FlagGridId = grid?.Id,
            ServiceBranchId = army.Id,
            ServiceBranchCategoryId = category.Id,
            SponsorId = sponsor.Id,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        var publicHonoree = Person("Public", "Example", occupied, true, null);
        db.Honorees.AddRange(
            publicHonoree,
            Person("Second", "Example", second, true, null, "Sergeant"),
            Person("Inactive", "Person", null, false, null),
            Person("Deleted", "Person", null, true, DateTime.UtcNow),
            Person("Gone Grid", "Person", deletedGrid, true, null),
            Person("Unplaced", "Person", null, true, null));
        db.SaveChanges();

        var claim = new FlagClaim
        {
            HonoreeId = publicHonoree.Id,
            FlagGridId = occupied.Id,
            ExternalUserObjectId = "oid-secret",
            ExternalUserEmail = "secret-submitter@example.test",
            ExternalUserName = "Secret Sponsor",
            ClaimStatus = "Submitted",
            AdminNotes = "SECRET-REVIEW-NOTE"
        };
        db.FlagClaims.Add(claim);
        db.SaveChanges();

        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            FlagClaimId = claim.Id,
            HonoreeId = publicHonoree.Id,
            FlagGridId = occupied.Id,
            FirstName = "Pending Only",
            LastName = "Person",
            RequestStatus = "Submitted",
            ReviewNotes = "SECRET-REVIEW-NOTE",
            SubmitterEmailAddress = "secret-submitter@example.test",
            SubmitterPhoneNumber = "555-0100-SECRET",
            Description = "pending bio"
        });
        db.SaveChanges();
    }

    private static void CollectNames(JsonElement element, ISet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                names.Add(property.Name);
                CollectNames(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectNames(item, names);
            }
        }
    }
}

public class PublicFlagMapRateLimitTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    [Fact]
    public async Task Public_flag_map_limits_requests_per_ip()
    {
        var client = factory.CreateClient();
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 130; i++)
        {
            var response = await client.GetAsync("/api/public/flag-map/sections");
            last = response.StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
            {
                Assert.True(i >= 120);
                return;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
