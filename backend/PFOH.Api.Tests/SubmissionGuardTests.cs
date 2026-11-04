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
    public async Task Public_duplicate_check_returns_one_card_and_hides_private_fields()
    {
        await ResetAsync();
        await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        await SeedPendingAsync("Ben", "Brooks", "Petty Officer", 2, "secret-pending@example.test", "2145550188", DateTime.UtcNow);
        var client = factory.CreateClient();

        var approved = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var approvedCard = AssertPublicCard(approved.Raw, "Ada Mann");
        Assert.Equal("Captain", approvedCard.Rank);
        Assert.Equal("Army", approvedCard.Branch);
        Assert.Equal("AA-10", approvedCard.FlagLocation);
        Assert.Contains("same full name", approvedCard.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-approved@example.test", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2145550199", approved.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"email\"", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"phone\"", approved.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"notes\"", approved.Raw, StringComparison.OrdinalIgnoreCase);

        var otherAccount = await CheckAsync(client, "Ben", "Brooks", 2, "family@example.test");
        var pendingCard = AssertPublicCard(otherAccount.Raw, "Ben Brooks");
        Assert.Equal("Petty Officer", pendingCard.Rank);
        Assert.Equal("Navy", pendingCard.Branch);
        Assert.Null(pendingCard.FlagLocation);
        Assert.DoesNotContain("secret-pending@example.test", otherAccount.Raw, StringComparison.OrdinalIgnoreCase);

        var sameAccount = await CheckAsync(client, "Ben", "Brooks", 2, "typed-victim@example.test", oid: "original-owner");
        var owned = AssertPublicCard(sameAccount.Raw, "Ben Brooks");
        Assert.False(string.IsNullOrWhiteSpace(owned.MatchToken));

        var differentBranch = await CheckAsync(client, "Ada", "Mann", 2, "someone-else@example.test");
        Assert.Empty(differentBranch.Matches);
        Assert.DoesNotContain("\"kind\"", differentBranch.Raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Public_check_picks_the_highest_score_then_approved_then_newest()
    {
        var client = factory.CreateClient();
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await ResetAsync();
        await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199", created: newer, middle: "Q");
        await SeedPendingAsync("Ada", "Mann", "Petty Officer", 1, "secret-pending@example.test", "2145550188", older);
        var higherScore = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var highCard = AssertPublicCard(higherScore.Raw, "Ada Mann");
        Assert.Equal("Petty Officer", highCard.Rank);
        Assert.Null(highCard.FlagLocation);
        Assert.DoesNotContain("Captain", higherScore.Raw, StringComparison.Ordinal);

        await ResetAsync();
        await SeedPendingAsync("Ada", "Mann", "Petty Officer", 1, "secret-pending@example.test", "2145550188", newer);
        await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199", created: older, suffix: "Jr");
        var approvedWins = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var approvedCard = AssertPublicCard(approvedWins.Raw, "Ada Mann Jr");
        Assert.Equal("Captain", approvedCard.Rank);
        Assert.Equal("AA-10", approvedCard.FlagLocation);
        Assert.DoesNotContain("Petty Officer", approvedWins.Raw, StringComparison.Ordinal);

        await ResetAsync();
        await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "older@example.test", "2145550191", created: older);
        await SeedApprovedAsync("Ada", "Mann", "General", "Army", "BB-20", "newer@example.test", "2145550192", created: newer, suffix: "Jr");
        var newestApproved = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var newestApprovedCard = AssertPublicCard(newestApproved.Raw, "Ada Mann Jr");
        Assert.Equal("General", newestApprovedCard.Rank);
        Assert.Equal("BB-20", newestApprovedCard.FlagLocation);
        Assert.DoesNotContain("AA-10", newestApproved.Raw, StringComparison.Ordinal);

        await ResetAsync();
        await SeedPendingAsync("Ada", "Mann", "Captain", 1, "older@example.test", "2145550181", older);
        await SeedPendingAsync("Ada", "Mann", "General", 1, "newer@example.test", "2145550182", newer, suffix: "Jr");
        var newestPending = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var newestPendingCard = AssertPublicCard(newestPending.Raw, "Ada Mann Jr");
        Assert.Equal("General", newestPendingCard.Rank);
        Assert.Null(newestPendingCard.FlagLocation);
        Assert.DoesNotContain("Captain", newestPending.Raw, StringComparison.Ordinal);

        await ResetAsync();
        var tied = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await SeedPendingAsync("Ada", "Mann", "Captain", 1, "first@example.test", "2145550171", tied);
        await SeedPendingAsync("Ada", "Mann", "General", 1, "second@example.test", "2145550172", tied, suffix: "Jr");
        var newerId = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var newerIdCard = AssertPublicCard(newerId.Raw, "Ada Mann Jr");
        Assert.Equal("General", newerIdCard.Rank);
        Assert.DoesNotContain("Captain", newerId.Raw, StringComparison.Ordinal);

        await ResetAsync();
        await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "approved@example.test", "2145550161", created: older, nickname: "Zebra");
        foreach (var nickname in new[] { "A", "B", "C", "D", "E", "F", "G", "H" })
        {
            await SeedPendingAsync("Ada", "Mann", "Seaman", 1, $"{nickname}@example.test", "2145550162", newer, nickname: nickname);
        }

        var beyondTheAdminCap = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var cappedCard = AssertPublicCard(beyondTheAdminCap.Raw, "Ada Mann (Zebra)");
        Assert.Equal("Captain", cappedCard.Rank);
        Assert.Equal("AA-10", cappedCard.FlagLocation);
        Assert.DoesNotContain("Seaman", beyondTheAdminCap.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("(A)", beyondTheAdminCap.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_duplicate_check_keeps_every_labeled_match()
    {
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var client = factory.CreateClient();

        foreach (var role in new[] { "PFOH.Admin", "Admin" })
        {
            await ResetAsync();
            var pendingId = await SeedPendingAsync("Ada", "Mann", "Petty Officer", 1, "secret-pending@example.test", "2145550188", newer);
            var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199", created: older, suffix: "Jr");
            var body = await CheckAsync(client, "Ada", "Mann", 1, "admin@example.test", oid: "admin-user", roles: role);
            Assert.Equal(2, body.Matches.Count);
            Assert.Equal("Ada Mann", body.Matches[0].Name);
            Assert.Equal("Pending", body.Matches[0].Kind);
            Assert.Equal("High", body.Matches[0].Likelihood);
            Assert.Equal(pendingId, body.Matches[0].ChangeRequestId);
            Assert.True(body.Matches[0].CanUpdateDirectly);
            AssertAdminMatchFields(body.Raw, 0);
            Assert.Equal("Ada Mann Jr", body.Matches[1].Name);
            Assert.Equal("Approved", body.Matches[1].Kind);
            Assert.Equal(honoreeId, body.Matches[1].HonoreeId);
            Assert.Equal("AA-10", body.Matches[1].FlagLocation);
            Assert.True(body.Matches[1].CanUpdateDirectly);
            AssertAdminMatchFields(body.Raw, 1);
            Assert.Contains("\"kind\"", body.Raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-approved@example.test", body.Raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("2145550199", body.Raw, StringComparison.Ordinal);

            await ResetAsync();
            await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "approved@example.test", "2145550161", created: older, nickname: "Zebra");
            foreach (var nickname in new[] { "A", "B", "C", "D", "E", "F", "G", "H" })
            {
                await SeedPendingAsync("Ada", "Mann", "Seaman", 1, $"{nickname}@example.test", "2145550162", newer, nickname: nickname);
            }

            var capped = await CheckAsync(client, "Ada", "Mann", 1, "admin@example.test", oid: "admin-user", roles: role);
            Assert.Equal(8, capped.Matches.Count);
            Assert.All(capped.Matches, match => Assert.Equal("Pending", match.Kind));
            Assert.DoesNotContain(capped.Matches, match => match.Name.Contains("Zebra", StringComparison.Ordinal));
            Assert.Equal("Ada Mann (A)", capped.Matches[0].Name);
            Assert.Equal("Ada Mann (H)", capped.Matches[7].Name);
        }
    }

    [Fact]
    public async Task Public_submit_conflict_returns_the_same_single_card()
    {
        await ResetAsync();
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedPendingAsync("Ada", "Mann", "Petty Officer", 1, "secret-pending@example.test", "2145550188", newer);
        await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199", created: older, suffix: "Jr");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "public-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Ada", "Mann", 1, "family@example.test", "General", null, null, null, null),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var card = AssertPublicConflict(raw, "Ada Mann Jr");
        Assert.Equal("Captain", card.Rank);
        Assert.Equal("AA-10", card.FlagLocation);
        Assert.DoesNotContain("Petty Officer", raw, StringComparison.Ordinal);

        var admin = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: Nomination("Ada", "Mann", 1, "admin@example.test", "General", null, null, null, null),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, admin.StatusCode);
        var adminRaw = await admin.Content.ReadAsStringAsync();
        using var adminDoc = JsonDocument.Parse(adminRaw);
        Assert.Equal("similar-entry", adminDoc.RootElement.GetProperty("code").GetString());
        var adminMatches = adminDoc.RootElement.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(2, adminMatches.Count);
        Assert.Equal("Pending", adminMatches[0].GetProperty("kind").GetString());
        Assert.Equal("Ada Mann", adminMatches[0].GetProperty("name").GetString());
        Assert.Equal("Approved", adminMatches[1].GetProperty("kind").GetString());
        Assert.Equal("Ada Mann Jr", adminMatches[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Public_same_person_uses_the_match_token_without_a_status_label()
    {
        await ResetAsync();
        var pendingId = await SeedPendingAsync("Ben", "Brooks", "Petty Officer", 2, "family@example.test", "2145550188", DateTime.UtcNow, owner: "same-person");
        var client = factory.CreateClient();
        var pendingCheck = await CheckAsync(client, "Ben", "Brooks", 2, "family@example.test", oid: "same-person");
        var pendingCard = AssertPublicCard(pendingCheck.Raw, "Ben Brooks");

        var update = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "same-person",
            "family@example.test",
            "Family Member",
            body: Nomination("Ben", "Brooks", 2, "family@example.test", "Chief", "update", null, null, null, matchToken: pendingCard.MatchToken),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await using var pendingScope = factory.Services.CreateAsyncScope();
        var pendingDb = pendingScope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var pending = await pendingDb.HonoreeChangeRequests.Include(row => row.FlagClaim).SingleAsync();
        Assert.Equal(pendingId, pending.Id);
        Assert.Equal("Chief", pending.Rank);
        Assert.Equal("Submitted", pending.RequestStatus);
        Assert.Equal("same-person", pending.FlagClaim!.ExternalUserObjectId);

        await ResetAsync();
        var honoreeId = await SeedApprovedAsync("Ada", "Mann", "Captain", "Army", "AA-10", "secret-approved@example.test", "2145550199");
        var approvedCheck = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test");
        var approvedCard = AssertPublicCard(approvedCheck.Raw, "Ada Mann");
        var correction = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "public-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Ada", "Mann", 1, "family@example.test", "General", "update", null, null, null, matchToken: approvedCard.MatchToken),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, correction.StatusCode);
        await using var approvedScope = factory.Services.CreateAsyncScope();
        var approvedDb = approvedScope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await approvedDb.Honorees.SingleAsync(row => row.Id == honoreeId);
        Assert.Equal("Captain", honoree.Rank);
        Assert.True(honoree.IsActive);
        var row = await approvedDb.HonoreeChangeRequests.SingleAsync();
        Assert.Equal(honoreeId, row.HonoreeId);
        Assert.Equal("General", row.Rank);
        Assert.Equal("Submitted", row.RequestStatus);
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
    public async Task No_does_not_supersede_a_different_person_shown_in_the_prompt()
    {
        await ResetAsync();
        var (sameDay, previousDay) = ChicagoSamples();
        var alanId = await SeedPendingAsync(
            "Example", "Spacedtwin", "Captain", 1, "family@example.test", "2145550101", sameDay,
            owner: "family-user", middle: "Alan");
        var seniorId = await SeedPendingAsync(
            "Example", "Spacedsuffix", "Captain", 1, "family@example.test", "2145550102", sameDay,
            owner: "family-user", suffix: "Sr.");
        var yesterdayId = await SeedPendingAsync(
            "Example", "Spacedtwin", "Captain", 1, "family@example.test", "2145550103", previousDay,
            owner: "family-user", middle: "Bruce");
        var client = factory.CreateClient();

        var bruce = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Spacedtwin", 1, "family@example.test", "Captain", "submit-new", null, null, null, middle: "Bruce"),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, bruce.StatusCode);

        var junior = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Spacedsuffix", 1, "family@example.test", "Captain", "submit-new", null, null, null, suffix: "Jr."),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, junior.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Submitted", (await db.HonoreeChangeRequests.SingleAsync(row => row.Id == alanId)).RequestStatus);
        Assert.Equal("Submitted", (await db.HonoreeChangeRequests.SingleAsync(row => row.Id == seniorId)).RequestStatus);
        Assert.Equal("Submitted", (await db.HonoreeChangeRequests.SingleAsync(row => row.Id == yesterdayId)).RequestStatus);
        Assert.Empty(await db.SubmissionAuditEvents.ToListAsync());
        Assert.Equal(5, await db.HonoreeChangeRequests.CountAsync());
    }

    [Fact]
    public async Task No_does_not_supersede_an_exact_copy_the_card_limit_hid()
    {
        await ResetAsync();
        var (sameDay, _) = ChicagoSamples();
        var seeded = new List<int>();
        for (var i = 0; i < 9; i++)
        {
            seeded.Add(await SeedPendingAsync(
                "Ada", "Mann", "Captain", 1, "family@example.test", "2145550101", sameDay,
                owner: "family-user", photo: $"pending/ada-{i}.jpg"));
        }

        var client = factory.CreateClient();
        var check = await CheckAsync(client, "Ada", "Mann", 1, "family@example.test", oid: "family-user");
        AssertPublicCard(check.Raw, "Ada Mann");

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
        var rows = await db.HonoreeChangeRequests.Where(row => seeded.Contains(row.Id)).ToListAsync();
        Assert.All(rows, row => Assert.Equal("Submitted", row.RequestStatus));
        Assert.Empty(await db.SubmissionAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task A_different_person_within_20_seconds_is_not_given_the_first_receipt()
    {
        await ResetAsync();
        var client = factory.CreateClient();
        var first = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Quickpair", 1, "family@example.test", "Sergeant", null, null, null, null, middle: "Carl"),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<CreatedDto>(Json);
        Assert.NotNull(firstBody?.ReceiptToken);

        var sameAgain = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Quickpair", 1, "family@example.test", "Sergeant", null, null, null, null, middle: "Carl"),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, sameAgain.StatusCode);
        var replay = await sameAgain.Content.ReadFromJsonAsync<CreatedDto>(Json);
        Assert.Equal(firstBody.ReceiptToken, replay!.ReceiptToken);

        var differentMiddle = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Quickpair", 1, "family@example.test", "Major", null, null, null, null, middle: "Dean"),
            idempotencyKey: Guid.NewGuid()));
        var differentBody = await differentMiddle.Content.ReadAsStringAsync();
        Assert.DoesNotContain(firstBody.ReceiptToken!.Value.ToString(), differentBody, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(HttpStatusCode.OK, differentMiddle.StatusCode);

        var dean = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Quickpair", 1, "family@example.test", "Major", "submit-new", null, null, null, middle: "Dean"),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, dean.StatusCode);
        var deanBody = await dean.Content.ReadFromJsonAsync<CreatedDto>(Json);
        Assert.NotEqual(firstBody.ReceiptToken, deanBody!.ReceiptToken);

        var junior = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Quicksuffix", 1, "family@example.test", "Sergeant", null, null, null, null, suffix: "Jr."),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, junior.StatusCode);
        var juniorBody = await junior.Content.ReadFromJsonAsync<CreatedDto>(Json);

        var seniorPrompt = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Quicksuffix", 1, "family@example.test", "Sergeant", null, null, null, null, suffix: "Sr."),
            idempotencyKey: Guid.NewGuid()));
        var seniorPromptBody = await seniorPrompt.Content.ReadAsStringAsync();
        Assert.DoesNotContain(juniorBody!.ReceiptToken!.Value.ToString(), seniorPromptBody, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(HttpStatusCode.OK, seniorPrompt.StatusCode);

        var senior = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/nominate",
            "family-user",
            "family@example.test",
            "Family Member",
            body: Nomination("Example", "Quicksuffix", 1, "family@example.test", "Sergeant", "submit-new", null, null, null, suffix: "Sr."),
            idempotencyKey: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, senior.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var rows = await db.HonoreeChangeRequests.ToListAsync();
        Assert.Contains(rows, row => row.MiddleName == "Carl" && row.Rank == "Sergeant" && row.RequestStatus == "Submitted");
        Assert.Contains(rows, row => row.MiddleName == "Dean" && row.Rank == "Major" && row.RequestStatus == "Submitted");
        Assert.Contains(rows, row => row.Suffix == "Jr." && row.RequestStatus == "Submitted");
        Assert.Contains(rows, row => row.Suffix == "Sr." && row.RequestStatus == "Submitted");
        Assert.Equal(2, rows.Count(row => row.LastName == "Quickpair"));
    }

    [Fact]
    public async Task A_stranger_correction_stays_when_the_claimant_submits()
    {
        await ResetAsync();
        var honoreeId = await SeedApprovedAsync("Ada", "Claimed", "Captain", "Army", "AA-10", "secret@example.test", "2145550199");
        var aliceId = await SeedPendingAsync(
            "Ada", "Claimed", "Captain", 1, "alice@example.test", "2145550111", DateTime.UtcNow,
            owner: "alice", photo: "pending/alice.jpg", honoreeId: honoreeId);
        var bobId = await SeedPendingAsync(
            "Ada", "Claimed", "General", 1, "bob@example.test", "2145550122", DateTime.UtcNow.AddMinutes(1),
            owner: "bob", photo: "pending/bob.jpg", honoreeId: honoreeId, correctsChangeRequestId: aliceId);
        factory.Files.Photos["pending/alice.jpg"] = [1];
        factory.Files.Photos["pending/bob.jpg"] = [2];

        var client = factory.CreateClient();
        var before = await PendingIdsAsync(client);
        Assert.Contains(aliceId, before);
        Assert.Contains(bobId, before);

        int aliceClaimId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var alice = await db.HonoreeChangeRequests.Include(row => row.FlagClaim).SingleAsync(row => row.Id == aliceId);
            aliceClaimId = alice.FlagClaimId;
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                FlagClaimId = aliceClaimId,
                HonoreeId = honoreeId,
                FirstName = "Ada",
                LastName = "Claimed",
                Rank = "Major",
                ServiceBranchId = 1,
                ServiceBranchCategoryId = 1,
                Description = "A later draft from the claimant.",
                RequestStatus = "Draft",
                SubmitterEmailAddress = "alice@example.test",
                SubmitterPhoneNumber = "2145550111",
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var submit = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/flag-claims/{aliceClaimId}/submit",
            "alice",
            "alice@example.test",
            "Alice"));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        await using var read = factory.Services.CreateAsyncScope();
        var saved = read.ServiceProvider.GetRequiredService<PfohDbContext>();
        var bob = await saved.HonoreeChangeRequests.SingleAsync(row => row.Id == bobId);
        Assert.Equal("Submitted", bob.RequestStatus);
        Assert.Equal("pending/bob.jpg", bob.PhotoFileName);
        Assert.DoesNotContain("pending/bob.jpg", factory.Files.DeletedPendingPhotos);
        Assert.Empty(await saved.SubmissionAuditEvents.Where(row => row.ChangeRequestId == bobId).ToListAsync());

        var after = await PendingIdsAsync(client);
        Assert.Contains(bobId, after);
        Assert.DoesNotContain(aliceId, after);
        Assert.Contains(after, id => id != bobId && id != aliceId);
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
        Assert.DoesNotContain("CleanupDuplicatePendingAsync", File.ReadAllText(SourcePath("backend/PFOH.Api/Services/PendingReviewMaintenance.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("SupersedeSameDayAsync", File.ReadAllText(SourcePath("backend/PFOH.Api/Controllers/FlagClaimsController.cs")), StringComparison.Ordinal);
        var rollback = script.Split("-- Rollback", 2)[1];
        Assert.Contains("Superseded by an exact copy from the same account on this day", rollback, StringComparison.Ordinal);
        Assert.Contains("newer submission for this honoree", rollback, StringComparison.Ordinal);
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
        string phone,
        DateTime? created = null,
        string? suffix = null,
        string? middle = null,
        string? nickname = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = created ?? DateTime.UtcNow;
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
            MiddleName = middle,
            LastName = last,
            Suffix = suffix,
            Nickname = nickname,
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
        string owner = "original-owner",
        string? middle = null,
        string? suffix = null,
        string? photo = null,
        int? honoreeId = null,
        int? correctsChangeRequestId = null,
        string? nickname = null)
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
            SubmittedUtc = submittedUtc,
            HonoreeId = honoreeId
        };
        var change = new HonoreeChangeRequest
        {
            FirstName = first,
            MiddleName = middle,
            LastName = last,
            Suffix = suffix,
            Nickname = nickname,
            Rank = rank,
            ServiceBranchId = branchId,
            HonoreeId = honoreeId,
            CorrectsChangeRequestId = correctsChangeRequestId,
            PhotoFileName = photo,
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

    private async Task<CheckBody> CheckAsync(HttpClient client, string first, string last, int branchId, string email, string? oid = null, string? roles = null)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/flag-claims/duplicate-check",
            oid ?? "public-user",
            email,
            "Family Member",
            roles: roles,
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
        int? changeRequestId,
        string? middle = null,
        string? suffix = null,
        string? matchToken = null)
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
        if (!string.IsNullOrEmpty(middle))
        {
            content.Add(new StringContent(middle), "MiddleName");
        }

        if (!string.IsNullOrEmpty(suffix))
        {
            content.Add(new StringContent(suffix), "Suffix");
        }

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

        if (!string.IsNullOrEmpty(matchToken))
        {
            content.Add(new StringContent(matchToken), "MatchToken");
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

    private static readonly string[] PublicCardFields = ["branch", "flagLocation", "matchToken", "name", "rank", "reason"];

    private static readonly string[] AdminMatchFields =
    [
        "branch", "canUpdateDirectly", "changeRequestId", "flagLocation", "honoreeId",
        "kind", "likelihood", "matchToken", "name", "rank", "reason"
    ];

    private static PublicCard AssertPublicCard(string raw, string name)
    {
        using var doc = JsonDocument.Parse(raw);
        var rootNames = doc.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(item => item).ToArray();
        Assert.Equal(["enabled", "matches"], rootNames);
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        return ReadPublicCard(doc.RootElement.GetProperty("matches"), name);
    }

    private static PublicCard AssertPublicConflict(string raw, string name)
    {
        using var doc = JsonDocument.Parse(raw);
        var rootNames = doc.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(item => item).ToArray();
        Assert.Equal(["code", "matches"], rootNames);
        Assert.Equal("similar-entry", doc.RootElement.GetProperty("code").GetString());
        return ReadPublicCard(doc.RootElement.GetProperty("matches"), name);
    }

    private static PublicCard ReadPublicCard(JsonElement matches, string name)
    {
        var match = Assert.Single(matches.EnumerateArray());
        var names = match.EnumerateObject().Select(property => property.Name).OrderBy(item => item).ToArray();
        Assert.Equal(PublicCardFields, names);
        Assert.Equal(name, match.GetProperty("name").GetString());
        var token = match.GetProperty("matchToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return new PublicCard(
            name,
            match.GetProperty("rank").ValueKind == JsonValueKind.Null ? null : match.GetProperty("rank").GetString(),
            match.GetProperty("branch").ValueKind == JsonValueKind.Null ? null : match.GetProperty("branch").GetString(),
            match.GetProperty("flagLocation").ValueKind == JsonValueKind.Null ? null : match.GetProperty("flagLocation").GetString(),
            match.GetProperty("reason").GetString() ?? string.Empty,
            token!);
    }

    private static void AssertAdminMatchFields(string raw, int index)
    {
        using var doc = JsonDocument.Parse(raw);
        var rootNames = doc.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(item => item).ToArray();
        Assert.Equal(["enabled", "matches"], rootNames);
        var match = doc.RootElement.GetProperty("matches")[index];
        var names = match.EnumerateObject().Select(property => property.Name).OrderBy(item => item).ToArray();
        Assert.Equal(AdminMatchFields, names);
    }

    private sealed record PublicCard(
        string Name,
        string? Rank,
        string? Branch,
        string? FlagLocation,
        string Reason,
        string MatchToken);

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

    [Fact]
    public async Task Turning_the_check_off_returns_no_matches()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/flag-claims/duplicate-check")
        {
            Content = JsonContent.Create(new { firstName = "Ada", lastName = "Mann", serviceBranchId = 1 })
        };
        request.Headers.Add(TestAuthHandler.OidHeader, "family-user");
        request.Headers.Add(TestAuthHandler.EmailHeader, "family@example.test");
        request.Headers.Add(TestAuthHandler.NameHeader, "Family Member");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("matches").EnumerateArray());
        Assert.False(doc.RootElement.TryGetProperty("count", out _));
        Assert.False(doc.RootElement.GetProperty("matches").EnumerateArray().Any());
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
