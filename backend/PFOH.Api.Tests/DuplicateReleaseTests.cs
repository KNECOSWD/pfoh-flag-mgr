using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class HonoreeDuplicateMatcherTests
{
    [Fact]
    public void Same_full_name_and_branch_is_high_and_uses_the_script_label()
    {
        var groups = HonoreeDuplicateMatcher.Match(new[]
        {
            Person(1, "John Q Smith", "USMC", "AA-10", "ada@example.com"),
            Person(2, "John Q. Smith Jr.", "United States Marine Corps", "M-4", "other@example.com")
        });

        var group = Assert.Single(groups);
        Assert.Equal("High", group.Likelihood);
        Assert.Equal(new[] { "H1" }, group.ReasonCodes);
        Assert.Equal("These 2 active records have same full name and same service branch.", group.Explanation);
        Assert.Equal("same full name and same service branch", Assert.Single(group.Pairs).Explanation);
    }

    [Fact]
    public void Middle_name_difference_on_an_adjacent_grid_is_labeled_per_pair()
    {
        var groups = HonoreeDuplicateMatcher.Match(new[]
        {
            Person(4, "John Smith", "US Army", "AA-1", null),
            Person(9, "John Quincy Smith", "United States Army", "AA-2", null)
        });

        var group = Assert.Single(groups);
        Assert.Equal("High", group.Likelihood);
        Assert.Contains("H2", group.ReasonCodes);
        Assert.Contains("H4", group.ReasonCodes);
        Assert.Contains("same first and last name (middle name, nickname or suffix differ) and same branch", group.Explanation);
        Assert.Contains("same name on the same or an adjacent flag grid", group.Explanation);
    }

    [Fact]
    public void Same_name_on_a_different_branch_is_low()
    {
        var groups = HonoreeDuplicateMatcher.Match(new[]
        {
            Person(3, "John Smith", "Navy", "AA-10", null),
            Person(8, "John Smith", "Army", "YY-2", null)
        });

        var group = Assert.Single(groups);
        Assert.Equal("Low", group.Likelihood);
        Assert.Equal(new[] { "H3" }, group.ReasonCodes);
        Assert.Equal("These 2 active records have same full name but a different service branch.", group.Explanation);
    }

    [Fact]
    public void Same_grid_is_medium_and_adjacent_sections_count_as_near()
    {
        Assert.True(HonoreeDuplicateMatcher.Near("AA-1", "AA-2"));
        Assert.True(HonoreeDuplicateMatcher.Near("AA-1", "AB-1"));
        Assert.False(HonoreeDuplicateMatcher.Near("AA-1", "BA-1"));

        var groups = HonoreeDuplicateMatcher.Match(new[]
        {
            Person(5, "Ada Lovelace", "Army", "M-3", null),
            Person(6, "Grace Hopper", "Navy", "M-3", null)
        });

        var group = Assert.Single(groups);
        Assert.Equal("Medium", group.Likelihood);
        Assert.Equal(
            "These 2 active records have assigned to the same flag grid, which holds only one flag.",
            group.Explanation);
    }

    [Fact]
    public void Same_sponsor_email_adds_the_sponsor_reason()
    {
        var groups = HonoreeDuplicateMatcher.Match(new[]
        {
            Person(1, "John Smith", "Army", null, "ada@example.com"),
            Person(2, "John Smith", "Navy", null, "ada@example.com")
        });

        var group = Assert.Single(groups);
        Assert.Contains("H5", group.ReasonCodes);
        Assert.Contains("same name submitted by the same sponsor", group.Explanation);
    }

    private static DuplicateCandidate Person(int id, string name, string branch, string? grid, string? email) =>
        new(id, name, branch, grid, null, email);
}

public class HonoreeClaimSchemaTests
{
    [Fact]
    public void Create_sql_adds_the_claim_table_and_does_not_drop_flag_claim_columns()
    {
        var sql = HonoreeClaimSchema.CreateSql + HonoreeClaimSchema.BackfillSql;
        Assert.Contains("CREATE TABLE [dbo].[HonoreeClaims]", sql);
        Assert.Contains("[HonoreeId] int NOT NULL", sql);
        Assert.Contains("[UserId] nvarchar(128) NOT NULL", sql);
        Assert.Contains("[ClaimedUtc] datetime2 NOT NULL", sql);
        Assert.Contains("UQ_HonoreeClaims_Honoree_User", sql);
        Assert.Contains("INSERT INTO [dbo].[HonoreeClaims]", sql);
        Assert.Contains("NOT EXISTS", sql);
        Assert.DoesNotContain("DROP COLUMN", sql);
        Assert.DoesNotContain("DROP TABLE [dbo].[FlagClaims]", sql);
        Assert.DoesNotContain("ALTER TABLE [dbo].[FlagClaims] DROP", sql);
    }

    [Fact]
    public void Reprint_review_sql_creates_only_that_table()
    {
        var sql = ReprintDuplicateReviewSchema.CreateSql;
        Assert.Contains("CREATE TABLE [dbo].[ReprintDuplicateReviews]", sql);
        Assert.Contains("[DuplicateChangeRequestId] int NOT NULL", sql);
        Assert.Contains("[KeptChangeRequestId] int NOT NULL", sql);
        Assert.Contains("[Status] nvarchar(20) NOT NULL", sql);
        Assert.Contains("[FlaggedUtc] datetime2 NOT NULL", sql);
        Assert.Contains("[ResolvedUtc] datetime2 NULL", sql);
        Assert.Contains("[ResolvedBy] nvarchar(255) NULL", sql);
        Assert.Contains("UX_ReprintDuplicateReviews_Duplicate", sql);
        Assert.DoesNotContain("ALTER TABLE [dbo].[HonoreeChangeRequests]", sql);
        Assert.DoesNotContain("DROP COLUMN", sql);
    }
}

public class DuplicateMergeApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Listing_groups_does_not_change_records_and_merge_keeps_every_claimant()
    {
        await ResetAsync();
        await SeedPairAsync();

        var client = factory.CreateClient();
        var listed = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/duplicates/groups"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var groups = await listed.Content.ReadFromJsonAsync<List<GroupResponse>>(Json);
        var group = Assert.Single(groups!);
        Assert.Equal("High", group.Likelihood);
        Assert.Contains("same full name and same service branch", group.Explanation);
        Assert.Equal(2, await CountActiveAsync());

        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = Fields(survivor: 2, photoFrom: 1, bioFrom: 1)
        }));
        var body = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, body);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(2, await db.Honorees.CountAsync());
        var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
        var duplicate = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
        Assert.True(survivor.IsActive);
        Assert.Equal("Photo bio", survivor.Description);
        Assert.Equal("photo-from-1.jpg", survivor.PhotoFileName);
        Assert.Equal("Smith", survivor.LastName);
        Assert.False(duplicate.IsActive);
        Assert.Null(duplicate.FlagGridId);
        Assert.Null(duplicate.DeletedDate);
        var released = await db.FlagGrids.SingleAsync(grid => grid.Id == 11);
        Assert.Null(released.HonoreeId);
        var kept = await db.FlagGrids.SingleAsync(grid => grid.Id == 12);
        Assert.Equal(2, kept.HonoreeId);
        Assert.Equal(12, survivor.FlagGridId);

        var claimants = await db.HonoreeClaims
            .Where(claim => claim.HonoreeId == 2 && claim.ClaimStatus == "Claimed")
            .OrderBy(claim => claim.Email)
            .ToListAsync();
        Assert.Equal(new[] { "ada@example.com", "bea@example.com" }, claimants.Select(claim => claim.Email).ToArray());
        Assert.Equal(2, claimants.Select(claim => claim.UserId).Distinct().Count());
    }

    [Fact]
    public async Task Merge_without_a_chosen_survivor_does_nothing()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 0,
            duplicateIds = new[] { 1, 2 },
            fields = Fields(2, 1, 1)
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Choose the surviving record", body);
        Assert.Equal(2, await CountActiveAsync());
    }

    [Fact]
    public async Task Two_people_can_claim_the_same_honoree()
    {
        await ResetAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            db.FlagGrids.Add(Grid(21, "B-1", now));
            db.Honorees.Add(Honoree(7, "Sam", "Rivera", "Army bio", "sam.jpg", 21, now));
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 21).HonoreeId = 7;
            db.FlagClaims.Add(Claim(71, 7, 21, "person-a", "ada@example.com", "Ada", now));
            db.FlagClaims.Add(Claim(72, 7, 21, "person-b", "bea@example.com", "Bea", now.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var response = await client.SendAsync(Admin(HttpMethod.Get, "/api/flag-claims/admin/honoree/7/claimants"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claimants = await response.Content.ReadFromJsonAsync<List<ClaimantRow>>(Json);
        Assert.Equal(2, claimants!.Count);
        Assert.Contains(claimants, claimant => claimant.ClaimantEmail == "ada@example.com");
        Assert.Contains(claimants, claimant => claimant.ClaimantEmail == "bea@example.com");
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
        db.FlagGrids.Add(Grid(11, "AA-1", now));
        db.FlagGrids.Add(Grid(12, "AA-2", now));
        db.Honorees.Add(Honoree(1, "John", "Smith", "Photo bio", "photo-from-1.jpg", 11, now, branchId: 1));
        db.Honorees.Add(Honoree(2, "John", "Smith", "Kept bio", "photo-from-2.jpg", 12, now, branchId: 1));
        await db.SaveChangesAsync();
        db.FlagGrids.Single(grid => grid.Id == 11).HonoreeId = 1;
        db.FlagGrids.Single(grid => grid.Id == 12).HonoreeId = 2;
        db.FlagClaims.Add(Claim(31, 1, 11, "person-a", "ada@example.com", "Ada", now));
        db.FlagClaims.Add(Claim(32, 2, 12, "person-b", "bea@example.com", "Bea", now));
        await db.SaveChangesAsync();
    }

    private async Task<int> CountActiveAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        return await db.Honorees.CountAsync(honoree => honoree.IsActive && honoree.DeletedDate == null);
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.ReprintDuplicateReviews.RemoveRange(db.ReprintDuplicateReviews);
        db.HonoreeClaims.RemoveRange(db.HonoreeClaims);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.ServiceBranches.RemoveRange(db.ServiceBranches);
        db.ServiceBranchCategories.RemoveRange(db.ServiceBranchCategories);
        await db.SaveChangesAsync();
    }

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

    private static Honoree Honoree(
        int id,
        string first,
        string last,
        string description,
        string photo,
        int? gridId,
        DateTime now,
        int? branchId = null) => new()
    {
        Id = id,
        FirstName = first,
        LastName = last,
        Rank = "Private",
        Description = description,
        PhotoFileName = photo,
        FlagGridId = gridId,
        ServiceBranchId = branchId,
        ServiceBranchCategoryId = branchId.HasValue ? 1 : null,
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

    private static Dictionary<string, int> Fields(int survivor, int photoFrom, int bioFrom) => new()
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
        ["awards"] = survivor,
        ["description"] = bioFrom,
        ["kia"] = survivor,
        ["photo"] = photoFrom,
        ["flagGrid"] = survivor,
        ["phone"] = survivor,
        ["email"] = survivor,
        ["sponsor"] = survivor
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

    private sealed record GroupResponse(string Likelihood, string Explanation, List<HonoreeRow> Honorees);
    private sealed record HonoreeRow(int Id);
    private sealed record ClaimantRow(string ClaimantEmail);
}

public class ReprintDuplicateReviewTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Maintenance_inserts_a_flagged_row_and_leaves_both_cards_queued()
    {
        await ResetAsync();
        await SeedDuplicateQueueAsync();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<ReprintQueueMaintenance>();
            await maintenance.DeactivateDuplicateActiveReprintQueueItemsAsync("PFOH.Admin", CancellationToken.None);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var review = await db.ReprintDuplicateReviews.SingleAsync();
        Assert.Equal(ReprintDuplicateReviewStatus.Flagged, review.Status);
        Assert.Null(review.ResolvedUtc);
        Assert.Equal(2, review.KeptChangeRequestId);
        Assert.Equal(1, review.DuplicateChangeRequestId);
        var items = await db.HonoreeChangeRequests.OrderBy(item => item.Id).ToListAsync();
        Assert.All(items, item => Assert.True(item.RequiresCardReprint));
        Assert.All(items, item => Assert.Null(item.CardPrintedUtc));
    }

    [Fact]
    public async Task Approve_removes_one_item_and_dismiss_keeps_both()
    {
        await ResetAsync();
        await SeedDuplicateQueueAsync();
        var client = factory.CreateClient();

        var listed = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/print/duplicate-reviews"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var rows = await listed.Content.ReadFromJsonAsync<List<ReviewRow>>(Json);
        var row = Assert.Single(rows!);
        Assert.Equal("John Smith", row.HonoreeName);

        var dismissed = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/print/duplicate-reviews/{row.Id}/dismiss"));
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var review = await db.ReprintDuplicateReviews.SingleAsync();
            Assert.Equal(ReprintDuplicateReviewStatus.Dismissed, review.Status);
            Assert.NotNull(review.ResolvedUtc);
            Assert.Equal(2, await db.HonoreeChangeRequests.CountAsync(item => item.RequiresCardReprint && item.CardPrintedUtc == null));
        }

        await ResetAsync();
        await SeedDuplicateQueueAsync();
        var again = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/print/duplicate-reviews"));
        var open = Assert.Single((await again.Content.ReadFromJsonAsync<List<ReviewRow>>(Json))!);
        var approved = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/print/duplicate-reviews/{open.Id}/approve"));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await using var after = factory.Services.CreateAsyncScope();
        var afterDb = after.ServiceProvider.GetRequiredService<PfohDbContext>();
        var kept = await afterDb.HonoreeChangeRequests.SingleAsync(item => item.Id == open.KeptChangeRequestId);
        var removed = await afterDb.HonoreeChangeRequests.SingleAsync(item => item.Id == open.DuplicateChangeRequestId);
        Assert.True(kept.RequiresCardReprint);
        Assert.False(removed.RequiresCardReprint);
        Assert.Contains("Removed duplicate reprint queue item", removed.ReviewNotes);
        var resolved = await afterDb.ReprintDuplicateReviews.SingleAsync();
        Assert.NotNull(resolved.ResolvedUtc);
        Assert.Equal(ReprintDuplicateReviewStatus.Flagged, resolved.Status);
        var remaining = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/print/duplicate-reviews"));
        Assert.Empty((await remaining.Content.ReadFromJsonAsync<List<ReviewRow>>(Json))!);
    }

    private async Task SeedDuplicateQueueAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        db.Honorees.Add(new Honoree
        {
            Id = 4,
            FirstName = "John",
            LastName = "Smith",
            Rank = string.Empty,
            Description = string.Empty,
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
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
        });
        db.FlagClaims.Add(new FlagClaim
        {
            Id = 8,
            HonoreeId = 4,
            ExternalUserObjectId = "admin-reprint:seed",
            ExternalUserEmail = "admin@example.com",
            ClaimStatus = "AdminReprintQueued",
            CreatedUtc = now
        });
        db.HonoreeChangeRequests.Add(QueueItem(1, now.AddMinutes(-5)));
        db.HonoreeChangeRequests.Add(QueueItem(2, now));
        await db.SaveChangesAsync();
    }

    private static HonoreeChangeRequest QueueItem(int id, DateTime reviewed) => new()
    {
        Id = id,
        FlagClaimId = 8,
        HonoreeId = 4,
        FirstName = "John",
        LastName = "Smith",
        RequestStatus = "Approved",
        RequiresCardReprint = true,
        CreatedUtc = reviewed,
        ReviewedUtc = reviewed
    };

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.ReprintDuplicateReviews.RemoveRange(db.ReprintDuplicateReviews);
        db.HonoreeClaims.RemoveRange(db.HonoreeClaims);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage Admin(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Admin");
        request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
        return request;
    }

    private sealed record ReviewRow(int Id, string HonoreeName, int DuplicateChangeRequestId, int KeptChangeRequestId);
}
