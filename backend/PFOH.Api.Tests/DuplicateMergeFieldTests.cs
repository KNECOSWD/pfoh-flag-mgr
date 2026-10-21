using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class DuplicateMergeFieldApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Groups_return_the_fields_the_review_screen_shows()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();

        var listed = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/duplicates/groups"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var groups = await listed.Content.ReadFromJsonAsync<List<GroupBody>>(Json);
        var group = Assert.Single(groups!);
        var left = Assert.Single(group.Honorees, honoree => honoree.Id == 1);
        Assert.Equal("Jr.", left.Suffix);
        Assert.Equal("Photo bio", left.Description);
        Assert.Equal("United States Army", left.ServiceBranchName);
        Assert.Equal("1942 - 1945", left.ServiceYears);
        Assert.Equal("Bronze Star", left.Awards);
        Assert.Equal("john-1.jpg", left.PhotoFileName);
        Assert.Equal("/api/honorees/1/photo", left.PhotoUrl);
        Assert.Contains("John", left.Name);
        Assert.Contains("Jr.", left.Name);

        var right = Assert.Single(group.Honorees, honoree => honoree.Id == 2);
        Assert.Equal("Sr.", right.Suffix);
        Assert.Equal("", right.PhotoFileName);
        Assert.Null(right.PhotoUrl);
    }

    [Fact]
    public async Task Custom_and_chosen_values_are_saved_and_claimants_carry_over()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var fields = SourceFields(survivor: 2, photoFrom: 1, awardsFrom: 2);
        fields.Remove("firstName");
        fields.Remove("middleName");
        fields.Remove("description");
        fields.Remove("serviceBranch");
        fields.Remove("serviceYears");
        fields.Remove("kia");
        fields.Remove("email");
        fields.Remove("phone");
        fields.Remove("sponsor");

        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string>
            {
                ["firstName"] = "  Patricia\nAnn\u0000  ",
                ["middleName"] = "   ",
                ["description"] = "Line one\r\nLine two\u0000",
                ["serviceBranch"] = "united states army",
                ["serviceYears"] = "1960 - 1964",
                ["kia"] = " Yes ",
                ["email"] = "  pat@example.com ",
                ["phone"] = " 214-555-0199 ",
                ["sponsor"] = "Ada Lovelace"
            }
        }));
        var body = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, body);
        var payload = JsonSerializer.Deserialize<MergeBody>(body, Json);
        Assert.NotNull(payload);
        Assert.Equal(2, payload.SurvivorId);
        Assert.Equal(new[] { "ada@example.com", "bea@example.com" }, payload.Claimants.Select(claimant => claimant.Email).OrderBy(email => email).ToArray());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(2, await db.Honorees.CountAsync());
        var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
        var other = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
        Assert.Equal("Patricia Ann", survivor.FirstName);
        Assert.Null(survivor.MiddleName);
        Assert.Equal("Line one\nLine two", survivor.Description);
        Assert.Equal(1, survivor.ServiceBranchId);
        Assert.Equal(1, survivor.ServiceBranchCategoryId);
        Assert.Equal(1960, survivor.StartYear);
        Assert.Equal(1964, survivor.EndYear);
        Assert.Equal("1960 - 1964", survivor.DatesUserEntry);
        Assert.True(survivor.KIA);
        Assert.Equal("pat@example.com", survivor.EmailAddress);
        Assert.Equal("214-555-0199", survivor.PhoneNumber);
        Assert.Equal(7, survivor.SponsorId);
        Assert.Equal("john-1.jpg", survivor.PhotoFileName);
        Assert.Equal("Purple Heart", survivor.Awards);
        Assert.Equal("Smith", survivor.LastName);
        Assert.True(survivor.IsActive);
        Assert.False(other.IsActive);
        Assert.Null(other.FlagGridId);
        Assert.Null(other.DeletedDate);
        var released = await db.FlagGrids.SingleAsync(grid => grid.Id == 11);
        Assert.Null(released.HonoreeId);
        var kept = await db.FlagGrids.SingleAsync(grid => grid.Id == 12);
        Assert.Equal(2, kept.HonoreeId);

        var claimants = await db.HonoreeClaims
            .Where(claim => claim.HonoreeId == 2 && claim.ClaimStatus == "Claimed")
            .OrderBy(claim => claim.Email)
            .ToListAsync();
        Assert.Equal(new[] { "ada@example.com", "bea@example.com" }, claimants.Select(claim => claim.Email).ToArray());
    }

    [Fact]
    public async Task Unknown_fields_bad_ids_and_invalid_custom_values_are_rejected()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();

        await RejectAsync(client, "Unknown field 'salutation'.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2).Concat(new[] { KeyValuePair.Create("salutation", 2) }).ToDictionary(pair => pair.Key, pair => pair.Value)
        });

        await RejectAsync(client, "Unknown field 'rankTitle'.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2),
            values = new Dictionary<string, string> { ["rankTitle"] = "Capt" }
        });

        var badId = SourceFields(2, 1, 2);
        badId["firstName"] = 99;
        await RejectAsync(client, "Each kept value has to come from one of the confirmed records.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = badId
        });

        var fields = SourceFields(2, 1, 2);
        fields.Remove("firstName");
        await RejectAsync(client, "First name must be 200 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["firstName"] = new string('A', 201) }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("suffix");
        await RejectAsync(client, "Suffix must be 100 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["suffix"] = new string('I', 101) }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("phone");
        await RejectAsync(client, "Phone must be 50 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["phone"] = new string('5', 51) }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("email");
        await RejectAsync(client, "Email is not a valid email address.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["email"] = "not-an-email" }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("description");
        await RejectAsync(client, "Honoree description is required.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["description"] = "   " }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("serviceYears");
        await RejectAsync(client, "Service years must be 500 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["serviceYears"] = new string('1', 501) }
        });

        await RejectAsync(client, "A typed photo value is not accepted.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2),
            values = new Dictionary<string, string> { ["photo"] = "typed.jpg" }
        });

        await RejectAsync(client, "A typed awards value is not accepted.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2),
            values = new Dictionary<string, string> { ["awards"] = "Medal of Honor" }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("serviceBranch");
        await RejectAsync(client, "Type a service branch that already exists", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["serviceBranch"] = "Not A Real Branch" }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("kia");
        await RejectAsync(client, "Killed in action must be Yes or No.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["kia"] = "Maybe" }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
        var other = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
        Assert.Equal("John", survivor.FirstName);
        Assert.Equal("Kept bio", survivor.Description);
        Assert.True(survivor.IsActive);
        Assert.True(other.IsActive);
        Assert.Equal(2, await db.Honorees.CountAsync(honoree => honoree.IsActive && honoree.DeletedDate == null));
    }

    [Fact]
    public async Task Inactive_and_out_of_group_records_are_rejected()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            db.Honorees.Add(Honoree(5, "Grace", "Hopper", "Other bio", "", null, now));
            var inactive = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
            inactive.IsActive = false;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var inactiveMerge = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.Conflict, inactiveMerge.StatusCode);
        Assert.Contains("inactive or deleted", await inactiveMerge.Content.ReadAsStringAsync());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var restored = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
            restored.IsActive = true;
            await db.SaveChangesAsync();
        }

        var outside = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 2, 5 },
            fields = SourceFields(2, 2, 2)
        }));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        var body = await outside.Content.ReadAsStringAsync();
        Assert.Contains("not in that group", body);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Kept bio", (await after.Honorees.SingleAsync(honoree => honoree.Id == 2)).Description);
        Assert.True((await after.Honorees.SingleAsync(honoree => honoree.Id == 5)).IsActive);
        Assert.Equal(3, await after.Honorees.CountAsync(honoree => honoree.IsActive && honoree.DeletedDate == null));
    }

    private async Task RejectAsync(HttpClient client, string expected, object body)
    {
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", body));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        Assert.Contains(expected, text);
    }

    private async Task SeedPairAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        db.ServiceBranchCategories.Add(new ServiceBranchCategory
        {
            Id = 1,
            ServiceBranchCategoryName = "Armed forces",
            Description = "Armed forces",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.ServiceBranches.Add(new ServiceBranch
        {
            Id = 1,
            ServiceBranchCategoryId = 1,
            ServiceBranchName = "United States Army",
            Description = "Army",
            LogoFileName = "army.png",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.SponsorCategories.Add(new SponsorCategory
        {
            Id = 1,
            SponsorCategoryName = "Family",
            Description = "Family",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.Sponsors.Add(new Sponsor
        {
            Id = 7,
            SponsorCategoryId = 1,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = string.Empty,
            PhoneNumber = string.Empty,
            EmailAddress = "ada-sponsor@example.com",
            StreetAddress = string.Empty,
            City = string.Empty,
            State = string.Empty,
            ZipCode = string.Empty,
            Salutation = string.Empty,
            IsActive = true,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.FlagGrids.Add(Grid(11, "AA-1", now));
        db.FlagGrids.Add(Grid(12, "AA-2", now));
        var left = Honoree(1, "John", "Smith", "Photo bio", "john-1.jpg", 11, now);
        left.MiddleName = "Q";
        left.Suffix = "Jr.";
        left.Awards = "Bronze Star";
        left.DatesUserEntry = "1942 - 1945";
        left.StartYear = 1942;
        left.EndYear = 1945;
        left.PhoneNumber = "214-555-0101";
        left.EmailAddress = "one@example.com";
        var right = Honoree(2, "John", "Smith", "Kept bio", "", 12, now);
        right.MiddleName = "Q";
        right.Suffix = "Sr.";
        right.Awards = "Purple Heart";
        right.DatesUserEntry = "1950 - 1953";
        right.StartYear = 1950;
        right.EndYear = 1953;
        right.KIA = true;
        right.PhoneNumber = "214-555-0102";
        right.EmailAddress = "two@example.com";
        db.Honorees.Add(left);
        db.Honorees.Add(right);
        await db.SaveChangesAsync();
        db.FlagGrids.Single(grid => grid.Id == 11).HonoreeId = 1;
        db.FlagGrids.Single(grid => grid.Id == 12).HonoreeId = 2;
        db.FlagClaims.Add(Claim(31, 1, 11, "person-a", "ada@example.com", "Ada", now));
        db.FlagClaims.Add(Claim(32, 2, 12, "person-b", "bea@example.com", "Bea", now));
        await db.SaveChangesAsync();
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.ReprintDuplicateReviews.RemoveRange(db.ReprintDuplicateReviews);
        db.HonoreeNotDuplicatePairs.RemoveRange(db.HonoreeNotDuplicatePairs);
        db.HonoreeClaims.RemoveRange(db.HonoreeClaims);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.Sponsors.RemoveRange(db.Sponsors);
        db.SponsorCategories.RemoveRange(db.SponsorCategories);
        db.ServiceBranches.RemoveRange(db.ServiceBranches);
        db.ServiceBranchCategories.RemoveRange(db.ServiceBranchCategories);
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, int> SourceFields(int survivor, int photoFrom, int awardsFrom) => new()
    {
        ["firstName"] = survivor,
        ["middleName"] = survivor,
        ["lastName"] = survivor,
        ["suffix"] = survivor,
        ["nickname"] = survivor,
        ["rank"] = survivor,
        ["serviceBranch"] = survivor,
        ["serviceYears"] = survivor,
        ["conflictsServed"] = survivor,
        ["awards"] = awardsFrom,
        ["description"] = survivor,
        ["kia"] = survivor,
        ["photo"] = photoFrom,
        ["flagGrid"] = survivor,
        ["phone"] = survivor,
        ["email"] = survivor,
        ["sponsor"] = survivor
    };

    private static FlagGrid Grid(int id, string name, DateTime now) => new()
    {
        Id = id,
        FlagGridName = name,
        Notes = string.Empty,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = now,
        ModifiedDate = now
    };

    private static Honoree Honoree(int id, string first, string last, string description, string photo, int? gridId, DateTime now) => new()
    {
        Id = id,
        FirstName = first,
        LastName = last,
        Rank = "Private",
        Description = description,
        PhotoFileName = photo,
        FlagGridId = gridId,
        ServiceBranchId = 1,
        ServiceBranchCategoryId = 1,
        Salutation = string.Empty,
        NameUserEntry = string.Empty,
        PhoneNumber = string.Empty,
        EmailAddress = string.Empty,
        Awards = string.Empty,
        ConflictsServed = string.Empty,
        DatesUserEntry = string.Empty,
        IsActive = true,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = now,
        ModifiedDate = now
    };

    private static FlagClaim Claim(int id, int honoreeId, int gridId, string userId, string email, string name, DateTime now) => new()
    {
        Id = id,
        HonoreeId = honoreeId,
        FlagGridId = gridId,
        ExternalUserObjectId = userId,
        ExternalUserEmail = email,
        ExternalUserName = name,
        ClaimStatus = "Claimed",
        CreatedUtc = now
    };

    private static HttpRequestMessage Admin(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Admin");
        request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record GroupBody(List<HonoreeBody> Honorees);
    private sealed record HonoreeBody(
        int Id,
        string Name,
        string? Suffix,
        string Description,
        string? ServiceBranchName,
        string ServiceYears,
        string Awards,
        string PhotoFileName,
        string? PhotoUrl);
    private sealed record MergeBody(int SurvivorId, List<ClaimantBody> Claimants);
    private sealed record ClaimantBody(string Email);
}
