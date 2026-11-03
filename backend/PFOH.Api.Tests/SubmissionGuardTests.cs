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
        Assert.False(string.IsNullOrWhiteSpace(approvedMatch.MatchToken));
        Assert.DoesNotContain("secret-approved@example.test", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2145550199", approved.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"email\"", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"phone\"", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"notes\"", approved.Raw, StringComparison.OrdinalIgnoreCase);

        var otherAccount = await CheckAsync(client, "Ben", "Brooks", 2, "family@example.test");
        var pendingMatch = Assert.Single(otherAccount.Matches);
        Assert.Equal("Pending", pendingMatch.Kind);
        Assert.Null(pendingMatch.ChangeRequestId);
        Assert.False(string.IsNullOrWhiteSpace(pendingMatch.MatchToken));
        Assert.Null(pendingMatch.FlagLocation);
        Assert.False(pendingMatch.CanUpdateDirectly);
        Assert.Contains("\"changeRequestId\":null", otherAccount.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-pending@example.test", otherAccount.Raw, StringComparison.OrdinalIgnoreCase);

        var sameAccount = await CheckAsync(client, "Ben", "Brooks", 2, "typed-victim@example.test", oid: "original-owner");
        var owned = Assert.Single(sameAccount.Matches);
        Assert.True(owned.CanUpdateDirectly);
        Assert.Equal(pendingId, owned.ChangeRequestId);

        var differentBranch = await CheckAsync(client, "Ada", "Mann", 2, "someone-else@example.test");
        Assert.Empty(differentBranch.Matches);
    }

    [Fact]
    public async Task Approved_match_from_the_public_form_becomes_a_correction_request()
    {
        await ResetAsync();
        var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        var client = factory.CreateClient();
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
    }

    [Fact]
    public async Task Pending_match_from_the_same_account_updates_that_entry()
    {
        await ResetAsync();
        var pendingId = await SeedPendingAsync("Ben", "Brooks", "Petty Officer", 2, "family@example.test", "2145550188", DateTime.UtcNow, owner: "same-person");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "same-person",
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
    public async Task Typed_email_does_not_update_another_accounts_pending_entry()
    {
        await ResetAsync();
        var pendingId = await SeedPendingAsync("Ben", "Brooks", "Petty Officer", 2, "victim@example.test", "2145550188", DateTime.UtcNow, owner: "victim-user");
        var client = factory.CreateClient();

        var apply = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/flag-claims/pending/{pendingId}/apply",
            "attacker-user",
            "attacker@example.test",
            "Attacker",
            body: Nomination("Ben", "Brooks", 2, "victim@example.test", "Chief", null, null, null, null),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
        var denied = await apply.Content.ReadAsStringAsync();
        Assert.DoesNotContain("2145550188", denied, StringComparison.Ordinal);
        Assert.DoesNotContain("victim@example.test", denied, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Petty Officer", denied, StringComparison.Ordinal);

        var nominate = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "attacker-user",
            "attacker@example.test",
            "Attacker",
            body: Nomination("Ben", "Brooks", 2, "victim@example.test", "Chief", "update", "Pending", null, pendingId),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, nominate.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var rows = await db.HonoreeChangeRequests.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Petty Officer", rows[0].Rank);
        Assert.Equal("Submitted", rows[0].RequestStatus);
        Assert.Equal(pendingId, rows[1].CorrectsChangeRequestId);
        Assert.Equal("Chief", rows[1].Rank);
    }

    [Fact]
    public async Task Submit_as_new_supersedes_only_unapproved_same_account_same_day_same_honoree_rows()
    {
        await ResetAsync();
        var (sameDay, previousDay) = ChicagoSamples();
        var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        var sameDayId = await SeedPendingAsync("Ada", "Mann", "Captain", 1, "family@example.test", "2145550101", sameDay, owner: "family-user");
        var yesterdayId = await SeedPendingAsync("Ada", "Mann", "Captain", 1, "family@example.test", "2145550102", previousDay, owner: "family-user");
        var otherAccountId = await SeedPendingAsync("Ada", "Mann", "Captain", 1, "other@example.test", "2145550103", sameDay, owner: "other-user");
        var unrelatedId = await SeedPendingAsync("Quinn", "Hale", "Sergeant", 1, "family@example.test", "2145550104", sameDay, owner: "family-user");
        var otherBranchId = await SeedPendingAsync("Ada", "Mann", "Captain", 2, "family@example.test", "2145550105", sameDay, owner: "family-user");
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
        Assert.Equal("Submitted", rows[otherAccountId].RequestStatus);
        Assert.Equal("Submitted", rows[unrelatedId].RequestStatus);
        Assert.Equal("Submitted", rows[otherBranchId].RequestStatus);
        var honoree = await db.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.Equal("Captain", honoree.Rank);
        Assert.True(honoree.IsActive);
        Assert.Null(honoree.DeletedDate);
        var audit = Assert.Single(await db.SubmissionAuditEvents.ToListAsync());
        Assert.Equal(SubmissionAuditEvent.Superseded, audit.Action);
        Assert.Equal(sameDayId, audit.ChangeRequestId);
        Assert.Equal("family@example.test", audit.ActorEmail);
        Assert.Equal("family-user", audit.ActorObjectId);
        var seeded = new[] { sameDayId, yesterdayId, otherAccountId, unrelatedId, otherBranchId };
        var keeperId = rows.Keys.Single(id => !seeded.Contains(id));
        Assert.Equal(keeperId, audit.KeeperChangeRequestId);

        var pending = await PendingIdsAsync(client);
        Assert.DoesNotContain(sameDayId, pending);
        Assert.Contains(yesterdayId, pending);
        Assert.Contains(otherAccountId, pending);
        Assert.Contains(unrelatedId, pending);

        var superseded = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/superseded",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, superseded.StatusCode);
        var hidden = await superseded.Content.ReadFromJsonAsync<SupersededPage>(Json);
        var listed = Assert.Single(hidden!.Items);
        Assert.Equal(sameDayId, listed.ChangeRequestId);
        Assert.Equal("family@example.test", listed.SubmitterEmail);
        Assert.Equal(keeperId, listed.KeeperChangeRequestId);
        Assert.Matches(" (CST|CDT)$", listed.SubmittedCentral);
        Assert.Equal(1, hidden.Total);

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
        var shown = JsonSerializer.Deserialize<ReceiptPageDto>(body, Json);
        Assert.NotNull(shown);
        Assert.Matches("^PFOH-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{6}$", shown.Reference);
        Assert.DoesNotContain(payload.ReceiptToken!.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
        var byCode = await client.GetAsync($"/api/submissions/{shown.Reference}");
        Assert.Equal(HttpStatusCode.NotFound, byCode.StatusCode);
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
        Assert.Equal(shown.Reference, (await db.SubmissionReceipts.SingleAsync()).PublicCode);

        var stolen = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "other-user",
            "other@example.test",
            "Other Person",
            body: Nomination("Cara", "Cole", 1, "family@example.test", "Sergeant", null, null, null, null),
            idempotencyKey: key));
        Assert.Equal(HttpStatusCode.Conflict, stolen.StatusCode);
        var stolenBody = await stolen.Content.ReadAsStringAsync();
        Assert.DoesNotContain(payload.ReceiptToken!.Value.ToString(), stolenBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2145550100", stolenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("family@example.test", stolenBody, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await db.HonoreeChangeRequests.ToListAsync());
    }

    [Fact]
    public async Task Submission_page_html_is_not_stored_or_indexed()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync($"/submission/{Guid.NewGuid():D}");
        var cache = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("no-store", cache, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Headers.TryGetValues("X-Robots-Tag", out var robots));
        Assert.Contains("noindex, nofollow", string.Join(' ', robots), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_as_new_does_not_supersede_another_accounts_same_day_rows()
    {
        await ResetAsync();
        var (sameDay, _) = ChicagoSamples();
        var aliceId = await SeedPendingAsync("Ada", "Mann", "Captain", 1, "alice@example.test", "2145550111", sameDay, owner: "alice-user");
        var client = factory.CreateClient();
        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "bob-user",
            "bob@example.test",
            "Bob",
            body: Nomination("Ada", "Mann", 1, "alice@example.test", "Captain", "submit-new", null, null, null),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var alice = await db.HonoreeChangeRequests.SingleAsync(row => row.Id == aliceId);
        Assert.Equal("Submitted", alice.RequestStatus);
        Assert.Empty(await db.SubmissionAuditEvents.ToListAsync());
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
    public void Migration_script_is_safe_on_the_live_schema_and_submit_does_not_send_mail()
    {
        var script = File.ReadAllText(SourcePath("backend/PFOH.Api/Data/Scripts/20261103180000_AddSubmissionGuard.sql"));
        var apply = script.Split("-- Rollback", 2)[0];
        var executable = string.Join(
            '\n',
            apply.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.Contains(SubmissionGuardSchema.CreateSql.Trim(), script, StringComparison.Ordinal);
        Assert.Contains(SubmissionGuardSchema.AddPublicCodeSql.Trim(), script, StringComparison.Ordinal);
        Assert.Contains(SubmissionGuardSchema.AddPublicCodeIndexSql.Trim(), script, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE ", executable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", executable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE [dbo].[Honorees]", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE [dbo].[FlagClaims]", apply, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID", apply, StringComparison.Ordinal);
        Assert.Contains("COL_LENGTH", apply, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE [dbo].[SubmissionReceipts]", script, StringComparison.Ordinal);

        var api = File.ReadAllText(SourcePath("backend/PFOH.Api/Program.cs"));
        var submit = File.ReadAllText(SourcePath("backend/PFOH.Api/Controllers/FlagClaimsController.cs"));
        Assert.DoesNotContain("ISubmissionMailer", api, StringComparison.Ordinal);
        Assert.DoesNotContain("SendConfirmation", submit, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupDuplicatePendingAsync", api, StringComparison.Ordinal);
        Assert.Contains("SET LOCK_TIMEOUT 5000", SubmissionGuardSchema.CreateSql, StringComparison.Ordinal);
        Assert.Contains("SET LOCK_TIMEOUT 5000", SubmissionGuardSchema.AddPublicCodeSql, StringComparison.Ordinal);
        Assert.Contains("SET LOCK_TIMEOUT 5000", SubmissionGuardSchema.AddPublicCodeIndexSql, StringComparison.Ordinal);
        Assert.Contains("quiet window", script, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(SourcePath("backend/PFOH.Api/Services/ISubmissionMailer.cs")));
        Assert.False(File.Exists(SourcePath("backend/PFOH.Api/Services/LoggingSubmissionMailer.cs")));
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

    private async Task<CheckBody> CheckAsync(HttpClient client, string first, string last, int branchId, string email, string? oid = null)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/duplicate-check",
            oid ?? "public-user",
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
        bool CanUpdateDirectly,
        string? MatchToken);

    private sealed record CreatedDto(Guid? ReceiptToken, ChangeIdDto LatestChangeRequest);

    private sealed record ChangeIdDto(int Id);

    private sealed record PendingIdDto(int ChangeRequestId);

    private sealed record ReceiptPageDto(string Reference);

    private sealed record SupersededPage(List<SupersededItem> Items, int Total);

    private sealed record SupersededItem(
        int ChangeRequestId,
        string HonoreeName,
        string? SubmitterEmail,
        string SubmittedCentral,
        int? KeeperChangeRequestId);
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

        var earlierId = await saved.HonoreeChangeRequests.OrderBy(row => row.Id).Select(row => row.Id).FirstAsync();
        var submitNew = GuardOffForm("submit-new", "Captain", null);
        var submitNewRequest = GuardOffRequest(submitNew);
        var submitNewResponse = await client.SendAsync(submitNewRequest);
        Assert.Equal(HttpStatusCode.OK, submitNewResponse.StatusCode);

        var update = GuardOffForm("update", "General", earlierId);
        var updateResponse = await client.SendAsync(GuardOffRequest(update));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        saved.ChangeTracker.Clear();
        var earlier = await saved.HonoreeChangeRequests.SingleAsync(row => row.Id == earlierId);
        Assert.Equal("Captain", earlier.Rank);
        Assert.Equal("Submitted", earlier.RequestStatus);
        Assert.Equal(4, await saved.HonoreeChangeRequests.CountAsync());
        Assert.Empty(await saved.SubmissionAuditEvents.ToListAsync());

        var apply = GuardOffForm(null, "Major", null);
        var applyRequest = GuardOffRequest(apply);
        applyRequest.RequestUri = new Uri($"/api/flag-claims/pending/{earlierId}/apply", UriKind.Relative);
        var applyResponse = await client.SendAsync(applyRequest);
        Assert.Equal(HttpStatusCode.NotFound, applyResponse.StatusCode);
        saved.ChangeTracker.Clear();
        Assert.Equal("Captain", (await saved.HonoreeChangeRequests.SingleAsync(row => row.Id == earlierId)).Rank);
    }

    private static MultipartFormDataContent GuardOffForm(string? action, string rank, int? changeRequestId)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("Ada"), "FirstName" },
            { new StringContent("Mann"), "LastName" },
            { new StringContent(rank), "Rank" },
            { new StringContent("1"), "ServiceBranchId" },
            { new StringContent("1"), "ServiceBranchCategoryId" },
            { new StringContent("A clearly fake tribute."), "Description" },
            { new StringContent("2145550100"), "SubmitterPhoneNumber" },
            { new StringContent("family@example.test"), "SubmitterEmailAddress" },
            { new StringContent("false"), "Kia" }
        };
        if (action is not null)
        {
            content.Add(new StringContent(action), "DuplicateAction");
            if (action == "update")
            {
                content.Add(new StringContent("Pending"), "MatchKind");
            }
        }

        if (changeRequestId is int change)
        {
            content.Add(new StringContent(change.ToString()), "MatchChangeRequestId");
        }

        return content;
    }

    private static HttpRequestMessage GuardOffRequest(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/flag-claims/nominate") { Content = content };
        request.Headers.Add(TestAuthHandler.OidHeader, "family-user");
        request.Headers.Add(TestAuthHandler.EmailHeader, "family@example.test");
        request.Headers.Add(TestAuthHandler.NameHeader, "Family Member");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return request;
    }
}
