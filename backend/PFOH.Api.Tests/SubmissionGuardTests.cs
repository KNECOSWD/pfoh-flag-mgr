using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class SubmissionGuardTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Duplicate_check_labels_pending_and_approved_and_hides_private_fields()
    {
        await ResetAsync();
        var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        var pendingId = await SeedPendingAsync("Ben", "Brooks", "Petty Officer", 2, "secret-pending@example.test", "2145550188", DateTime.UtcNow);
        var client = factory.CreateClient();

        var approved = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var approvedMatch = Assert.Single(approved.Matches);
        Assert.Equal("Approved", approvedMatch.Kind);
        Assert.Equal(honoreeId, approvedMatch.HonoreeId);
        Assert.Equal("Captain", approvedMatch.Rank);
        Assert.Equal("Army", approvedMatch.Branch);
        Assert.Equal("AA-10", approvedMatch.FlagLocation);
        Assert.False(approvedMatch.CanUpdateDirectly);
        Assert.DoesNotContain("secret-approved@example.test", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2145550199", approved.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("email", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notes", approved.Raw, StringComparison.OrdinalIgnoreCase);

        var otherEmail = await CheckAsync(client, "Ben", "Brooks", 2, "family@example.test");
        var pendingMatch = Assert.Single(otherEmail.Matches);
        Assert.Equal("Pending", pendingMatch.Kind);
        Assert.Equal(pendingId, pendingMatch.ChangeRequestId);
        Assert.Null(pendingMatch.FlagLocation);
        Assert.False(pendingMatch.CanUpdateDirectly);
        Assert.DoesNotContain("secret-pending@example.test", otherEmail.Raw, StringComparison.OrdinalIgnoreCase);

        var sameEmail = await CheckAsync(client, "Ben", "Brooks", 2, "secret-pending@example.test");
        Assert.True(Assert.Single(sameEmail.Matches).CanUpdateDirectly);

        var differentBranch = await CheckAsync(client, "Ada", "Mann", 2, "someone-else@example.test");
        Assert.Empty(differentBranch.Matches);
    }

    [Fact]
    public async Task Approved_match_from_the_public_form_becomes_a_correction_request()
    {
        await ResetAsync();
        var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        var client = factory.CreateClient();
        factory.Mail.Sent.Clear();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "public-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Ada", "Mann", 1, "family@example.test", "General", "update", "Approved", honoreeId, null),
            idempotencyKey: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.Equal("Captain", honoree.Rank);
        Assert.Equal("AA-10", (await db.FlagGrids.SingleAsync()).FlagGridName);
        Assert.True(honoree.IsActive);
        var correction = await db.HonoreeChangeRequests.Include(row => row.FlagClaim).SingleAsync();
        Assert.Equal(honoreeId, correction.HonoreeId);
        Assert.Equal("Submitted", correction.RequestStatus);
        Assert.Equal("General", correction.Rank);
        Assert.Null(correction.FlagClaim!.HonoreeId);
        var pending = await PendingIdsAsync(client);
        Assert.Contains(correction.Id, pending);
        Assert.Single(factory.Mail.Sent);
        Assert.DoesNotContain("secret-approved@example.test", factory.Mail.Sent[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_match_from_the_same_email_updates_that_entry()
    {
        await ResetAsync();
        var pendingId = await SeedPendingAsync("Ben", "Brooks", "Petty Officer", 2, "family@example.test", "2145550188", DateTime.UtcNow, owner: "same-person");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "other-oid",
            "family@example.test",
            "Family Member",
            body: Nomination("Ben", "Brooks", 2, "family@example.test", "Chief", "update", "Pending", null, pendingId),
            idempotencyKey: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var pending = await db.HonoreeChangeRequests.Include(row => row.FlagClaim).SingleAsync();
        Assert.Equal(pendingId, pending.Id);
        Assert.Equal("Chief", pending.Rank);
        Assert.Equal("Submitted", pending.RequestStatus);
        Assert.Equal("same-person", pending.FlagClaim!.ExternalUserObjectId);
    }

    [Fact]
    public async Task Pending_match_from_a_different_email_is_a_correction_request()
    {
        await ResetAsync();
        var pendingId = await SeedPendingAsync("Ben", "Brooks", "Petty Officer", 2, "first-family@example.test", "2145550188", DateTime.UtcNow);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "second-person",
            "second-family@example.test",
            "Second Family",
            body: Nomination("Ben", "Brooks", 2, "second-family@example.test", "Chief", "update", "Pending", null, pendingId),
            idempotencyKey: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var rows = await db.HonoreeChangeRequests.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Petty Officer", rows[0].Rank);
        Assert.Equal("Submitted", rows[0].RequestStatus);
        Assert.Equal(pendingId, rows[1].CorrectsChangeRequestId);
        Assert.Null(rows[1].HonoreeId);
        Assert.Equal("Chief", rows[1].Rank);
        Assert.Equal("Submitted", rows[1].RequestStatus);

        var approve = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{rows[1].Id}/approve",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { requiresCardReprint = false, reviewNotes = "Apply the correction" }));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        db.ChangeTracker.Clear();
        var after = await db.HonoreeChangeRequests.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal("Chief", after[0].Rank);
        Assert.Equal("Submitted", after[0].RequestStatus);
        Assert.Equal("Approved", after[1].RequestStatus);
        Assert.Empty(await db.Honorees.ToListAsync());
    }

    [Fact]
    public async Task Submit_as_new_supersedes_only_unapproved_same_email_same_day_rows()
    {
        await ResetAsync();
        var (sameDay, previousDay) = ChicagoSamples();
        var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        var sameDayId = await SeedPendingAsync("Ada", "Mann", "Captain", 1, "family@example.test", "2145550101", sameDay);
        var yesterdayId = await SeedPendingAsync("Ada", "Mann", "Captain", 1, "family@example.test", "2145550102", previousDay);
        var otherEmailId = await SeedPendingAsync("Ada", "Mann", "Captain", 1, "other@example.test", "2145550103", sameDay);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Ada", "Mann", 1, "family@example.test", "Captain", "submit-new", null, null, null),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var rows = await db.HonoreeChangeRequests.ToDictionaryAsync(row => row.Id);
        Assert.Equal("Superseded", rows[sameDayId].RequestStatus);
        Assert.Equal("Submitted", rows[yesterdayId].RequestStatus);
        Assert.Equal("Submitted", rows[otherEmailId].RequestStatus);
        var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.Equal("Captain", honoree.Rank);
        Assert.True(honoree.IsActive);
        Assert.Null(honoree.DeletedDate);
        var audit = Assert.Single(await db.SubmissionAuditEvents.ToListAsync());
        Assert.Equal(SubmissionAuditEvent.Superseded, audit.Action);
        Assert.Equal(sameDayId, audit.ChangeRequestId);

        var pending = await PendingIdsAsync(client);
        Assert.DoesNotContain(sameDayId, pending);
        Assert.Contains(yesterdayId, pending);
        Assert.Contains(otherEmailId, pending);

        var superseded = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/superseded",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, superseded.StatusCode);
        var hidden = await superseded.Content.ReadFromJsonAsync<List<SupersededDto>>(Json);
        Assert.Contains(hidden!, row => row.ChangeRequestId == sameDayId);

        var restore = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{sameDayId}/restore",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal("Submitted", (await db.HonoreeChangeRequests.SingleAsync(row => row.Id == sameDayId)).RequestStatus);
        var actions = await db.SubmissionAuditEvents.OrderBy(row => row.Id).Select(row => row.Action).ToListAsync();
        Assert.Equal([SubmissionAuditEvent.Superseded, SubmissionAuditEvent.Restored], actions);
        Assert.Contains(sameDayId, await PendingIdsAsync(client));
    }

    [Fact]
    public async Task Receipt_is_an_unguessable_token_for_that_submission_only()
    {
        await ResetAsync();
        await SeedApprovedAsync("Hidden", "Person", "Major", "Army", "BB-2", "hidden@example.test", "2145550177");
        var client = factory.CreateClient();
        factory.Mail.Sent.Clear();
        var key = Guid.NewGuid();
        var created = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Cara", "Cole", 1, "family@example.test", "Sergeant", null, null, null, null),
            idempotencyKey: key));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var payload = await created.Content.ReadFromJsonAsync<CreatedDto>(Json);
        Assert.NotNull(payload?.ReceiptToken);
        Assert.NotEqual(payload.LatestChangeRequest.Id.ToString(), payload.ReceiptToken.ToString());
        Assert.NotEqual(Guid.Empty, payload.ReceiptToken);

        var page = await client.GetAsync($"/api/submissions/{payload.ReceiptToken}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadAsStringAsync();
        Assert.Contains("Cara", body, StringComparison.Ordinal);
        Assert.Contains("Pending review", body, StringComparison.Ordinal);
        Assert.Contains(payload.ReceiptToken!.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hidden", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden@example.test", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("family@example.test", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("214555", body, StringComparison.Ordinal);

        var sequential = await client.GetAsync("/api/submissions/1");
        Assert.Equal(HttpStatusCode.NotFound, sequential.StatusCode);
        var missing = await client.GetAsync($"/api/submissions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var again = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Cara", "Cole", 1, "family@example.test", "Sergeant", null, null, null, null),
            idempotencyKey: key));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replay = await again.Content.ReadFromJsonAsync<CreatedDto>(Json);
        Assert.Equal(payload.ReceiptToken, replay!.ReceiptToken);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Single(await db.HonoreeChangeRequests.ToListAsync());
        Assert.Single(factory.Mail.Sent);
        Assert.DoesNotContain("—", factory.Mail.Sent[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("—", factory.Mail.Sent[0].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_can_update_an_approved_record_directly()
    {
        await ResetAsync();
        var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: Nomination("Ada", "Mann", 1, "admin@example.test", "General", "update", "Approved", honoreeId, null),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.Include(row => row.FlagGrid).SingleAsync();
        Assert.Equal("General", honoree.Rank);
        Assert.Equal("AA-10", honoree.FlagGrid!.FlagGridName);
        Assert.True(honoree.IsActive);
        Assert.Equal("secret-approved@example.test", honoree.EmailAddress);
        Assert.DoesNotContain(await db.HonoreeChangeRequests.ToListAsync(), row => row.RequestStatus == "Submitted");
    }

    [Fact]
    public void Confirmation_mailer_does_not_reference_a_network_client()
    {
        var source = File.ReadAllText(SourcePath("backend/PFOH.Api/Services/LoggingSubmissionMailer.cs"));
        Assert.DoesNotContain("SmtpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MailMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMail", source, StringComparison.Ordinal);
        var script = File.ReadAllText(SourcePath("backend/PFOH.Api/Data/Scripts/20261103180000_AddSubmissionGuard.sql"));
        Assert.Contains(SubmissionGuardSchema.CreateSql.Trim(), script, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE [dbo].[SubmissionReceipts]", script, StringComparison.Ordinal);
    }

    private async Task<int> SeedApprovedAsync(
        string first,
        string last,
        string rank,
        string branch,
        string gridName,
        string email,
        string phone)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        var branchId = branch == "Navy" ? 2 : 1;
        var grid = new FlagGrid
        {
            FlagGridName = gridName,
            Notes = "Open spot",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.FlagGrids.Add(grid);
        var honoree = new Honoree
        {
            FirstName = first,
            LastName = last,
            Rank = rank,
            ServiceBranchId = branchId,
            ServiceBranchCategoryId = 1,
            Description = "A private tribute note that must stay off the match preview.",
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
            PhoneNumber = phone,
            EmailAddress = email,
            Awards = string.Empty,
            ConflictsServed = string.Empty,
            DatesUserEntry = string.Empty,
            IsActive = true,
            FlagGrid = grid,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.Honorees.Add(honoree);
        await db.SaveChangesAsync();
        grid.HonoreeId = honoree.Id;
        await db.SaveChangesAsync();
        return honoree.Id;
    }

    private async Task<int> SeedPendingAsync(
        string first,
        string last,
        string rank,
        int branchId,
        string email,
        string phone,
        DateTime submittedUtc,
        string owner = "original-owner")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var claim = new FlagClaim
        {
            ExternalUserObjectId = owner,
            ExternalUserEmail = email,
            ExternalUserName = "Original Submitter",
            ClaimStatus = "Submitted",
            CreatedUtc = submittedUtc,
            SubmittedUtc = submittedUtc
        };
        var change = new HonoreeChangeRequest
        {
            FirstName = first,
            LastName = last,
            Rank = rank,
            ServiceBranchId = branchId,
            ServiceBranchCategoryId = 1,
            Description = "Pending tribute",
            RequestStatus = "Submitted",
            SubmitterEmailAddress = email,
            SubmitterPhoneNumber = phone,
            CreatedUtc = submittedUtc,
            SubmittedUtc = submittedUtc
        };
        claim.HonoreeChangeRequests.Add(change);
        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync();
        return change.Id;
    }

    private async Task ResetAsync()
    {
        factory.Mail.Sent.Clear();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.SubmissionAuditEvents.RemoveRange(db.SubmissionAuditEvents);
        db.SubmissionReceipts.RemoveRange(db.SubmissionReceipts);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.ServiceBranches.RemoveRange(db.ServiceBranches);
        db.ServiceBranchCategories.RemoveRange(db.ServiceBranchCategories);
        db.SponsorCategories.RemoveRange(db.SponsorCategories);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
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
        db.ServiceBranches.Add(Branch(1, "Army", now));
        db.ServiceBranches.Add(Branch(2, "Navy", now));
        await db.SaveChangesAsync();
    }

    private static ServiceBranch Branch(int id, string name, DateTime now) => new()
    {
        Id = id,
        ServiceBranchCategoryId = 1,
        ServiceBranchName = name,
        Description = name,
        LogoFileName = string.Empty,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = now,
        ModifiedDate = now
    };

    private async Task<CheckBody> CheckAsync(HttpClient client, string first, string last, int branchId, string email)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/duplicate-check",
            "public-user",
            email,
            "Family Member",
            body: new
            {
                firstName = first,
                lastName = last,
                serviceBranchId = branchId,
                submitterEmailAddress = email
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize<CheckBody>(raw, Json);
        Assert.NotNull(parsed);
        return parsed with { Raw = raw };
    }

    private async Task<List<int>> PendingIdsAsync(HttpClient client)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/pending",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<PendingIdDto>>(Json);
        return items!.Select(item => item.ChangeRequestId).ToList();
    }

    private static (DateTime SameDay, DateTime PreviousDay) ChicagoSamples()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var sameLocal = DateTime.SpecifyKind(localNow.AddMinutes(-1), DateTimeKind.Unspecified);
        if (sameLocal.Date != localNow.Date)
        {
            sameLocal = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified);
        }

        var previousLocal = DateTime.SpecifyKind(localNow.Date.AddMinutes(-30), DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(sameLocal, zone), TimeZoneInfo.ConvertTimeToUtc(previousLocal, zone));
    }

    private static string SourcePath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, relative);
    }

    private static MultipartFormDataContent Nomination(
        string first,
        string last,
        int branchId,
        string email,
        string rank,
        string? action,
        string? matchKind,
        int? honoreeId,
        int? changeRequestId)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(first), "FirstName" },
            { new StringContent(last), "LastName" },
            { new StringContent(rank), "Rank" },
            { new StringContent(branchId.ToString()), "ServiceBranchId" },
            { new StringContent("1"), "ServiceBranchCategoryId" },
            { new StringContent("A clearly fake tribute."), "Description" },
            { new StringContent("2145550100"), "SubmitterPhoneNumber" },
            { new StringContent(email), "SubmitterEmailAddress" },
            { new StringContent("false"), "Kia" }
        };
        if (action is not null)
        {
            content.Add(new StringContent(action), "DuplicateAction");
        }

        if (matchKind is not null)
        {
            content.Add(new StringContent(matchKind), "MatchKind");
        }

        if (honoreeId is int honoree)
        {
            content.Add(new StringContent(honoree.ToString()), "MatchHonoreeId");
        }

        if (changeRequestId is int change)
        {
            content.Add(new StringContent(change.ToString()), "MatchChangeRequestId");
        }

        return content;
    }

    private static HttpRequestMessage Authed(
        HttpMethod method,
        string url,
        string ownerObjectId,
        string email,
        string name,
        string? roles = null,
        object? body = null,
        Guid? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        if (idempotencyKey is Guid key)
        {
            request.Headers.Add("Idempotency-Key", key.ToString("D"));
        }

        if (body is HttpContent content)
        {
            request.Content = content;
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record CheckBody(bool Enabled, List<MatchBody> Matches, string Raw = "")
    {
        public string Raw { get; init; } = Raw;
    }

    private sealed record MatchBody(
        string Kind,
        int? HonoreeId,
        int? ChangeRequestId,
        string Name,
        string? Rank,
        string? Branch,
        string? FlagLocation,
        string Likelihood,
        string Reason,
        bool CanUpdateDirectly);

    private sealed record CreatedDto(Guid? ReceiptToken, ChangeIdDto LatestChangeRequest);

    private sealed record ChangeIdDto(int Id);

    private sealed record PendingIdDto(int ChangeRequestId);

    private sealed record SupersededDto(int ChangeRequestId, string HonoreeName);
}

public sealed class RecordingSubmissionMailer : ISubmissionMailer
{
    public List<SubmissionConfirmation> Sent { get; } = new();

    public Task SendConfirmationAsync(SubmissionConfirmation message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class SubmissionGuardOffFactory : PfohApiFactory
{
    public SubmissionGuardOffFactory()
    {
        ExtraSettings = new Dictionary<string, string?>
        {
            ["SubmissionDuplicates:Enabled"] = "false"
        };
    }
}

public class SubmissionGuardOffTests(SubmissionGuardOffFactory factory) : IClassFixture<SubmissionGuardOffFactory>
{
    [Fact]
    public async Task Turning_the_check_off_allows_the_submit_and_does_not_supersede()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
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
            ServiceBranchName = "Army",
            Description = "Army",
            LogoFileName = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        var claim = new FlagClaim
        {
            ExternalUserObjectId = "earlier",
            ExternalUserEmail = "family@example.test",
            ClaimStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        };
        claim.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            FirstName = "Ada",
            LastName = "Mann",
            Rank = "Captain",
            ServiceBranchId = 1,
            ServiceBranchCategoryId = 1,
            Description = "Earlier",
            RequestStatus = "Submitted",
            SubmitterEmailAddress = "family@example.test",
            SubmitterPhoneNumber = "2145550100",
            CreatedUtc = now,
            SubmittedUtc = now
        });
        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        var content = new MultipartFormDataContent
        {
            { new StringContent("Ada"), "FirstName" },
            { new StringContent("Mann"), "LastName" },
            { new StringContent("Captain"), "Rank" },
            { new StringContent("1"), "ServiceBranchId" },
            { new StringContent("1"), "ServiceBranchCategoryId" },
            { new StringContent("A clearly fake tribute."), "Description" },
            { new StringContent("2145550100"), "SubmitterPhoneNumber" },
            { new StringContent("family@example.test"), "SubmitterEmailAddress" },
            { new StringContent("false"), "Kia" }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/flag-claims/nominate") { Content = content };
        request.Headers.Add(TestAuthHandler.OidHeader, "family-user");
        request.Headers.Add(TestAuthHandler.EmailHeader, "family@example.test");
        request.Headers.Add(TestAuthHandler.NameHeader, "Family Member");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = factory.Services.CreateAsyncScope();
        var saved = read.ServiceProvider.GetRequiredService<PfohDbContext>();
        var statuses = await saved.HonoreeChangeRequests.Select(row => row.RequestStatus).ToListAsync();
        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, status => Assert.Equal("Submitted", status));
        Assert.Empty(await saved.SubmissionAuditEvents.ToListAsync());
    }
}
