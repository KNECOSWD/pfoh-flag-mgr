using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class AdminActivityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DateTime Noon = new(2026, 6, 15, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Utc_windows_are_calendar_days()
    {
        var (start7, start30, end) = AdminActivityWindows.For(Noon);

        Assert.Equal(new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc), start7);
        Assert.Equal(new DateTime(2026, 5, 17, 0, 0, 0, DateTimeKind.Utc), start30);
        Assert.Equal(new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc), end);
        Assert.Equal(7, (end - start7).TotalDays);
        Assert.Equal(30, (end - start30).TotalDays);
    }

    [Fact]
    public async Task Counts_use_utc_days_and_status_totals_match_the_rows()
    {
        await using var factory = new PfohApiFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        await db.Database.EnsureCreatedAsync();

        var (start7, start30, end) = AdminActivityWindows.For(Noon);
        db.UserProfiles.AddRange(
            Profile("acct-inside-today", Noon),
            Profile("acct-start-7", start7),
            Profile("acct-before-7", start7.AddTicks(-1)),
            Profile("acct-start-30", start30),
            Profile("acct-before-30", start30.AddTicks(-1)));
        db.HonoreeChangeRequests.AddRange(
            Change("Submitted", Noon),
            Change("Submitted", Noon.AddDays(-2)),
            Change("Draft", Noon.AddDays(-1)),
            Change("Approved", start30.AddDays(2)),
            Change("Rejected", start30.AddTicks(-1)));
        db.FlagClaims.AddRange(
            Claim("Claimed", Noon),
            Claim("Approved", start7),
            Claim("Cancelled", start30.AddDays(1)),
            Claim("Rejected", start30.AddTicks(-1)));
        db.SubmissionReceipts.AddRange(
            Receipt(1, Noon),
            Receipt(2, start30.AddDays(3)),
            Receipt(3, start30.AddTicks(-1)));
        await db.SaveChangesAsync();

        var activity = await AdminActivityQuery.ReadAsync(db, Noon, CancellationToken.None);

        Assert.Equal(2, activity.Accounts.Last7Days);
        Assert.Equal(4, activity.Accounts.Last30Days);
        Assert.Equal(5, activity.Accounts.AllTime);

        AssertStatus(activity.ChangeRequests.Last7Days, 3, ("Draft", 1), ("Submitted", 2));
        AssertStatus(activity.ChangeRequests.Last30Days, 4, ("Approved", 1), ("Draft", 1), ("Submitted", 2));
        AssertStatus(activity.FlagClaims.Last7Days, 2, ("Approved", 1), ("Claimed", 1));
        AssertStatus(activity.FlagClaims.Last30Days, 3, ("Approved", 1), ("Cancelled", 1), ("Claimed", 1));
        Assert.Equal(1, activity.Receipts.Last7Days);
        Assert.Equal(2, activity.Receipts.Last30Days);

        Assert.Equal(30, activity.Daily.Count);
        Assert.Equal("2026-05-17", activity.Daily[0].Date);
        Assert.Equal("2026-06-15", activity.Daily[^1].Date);
        Assert.Equal(activity.Accounts.Last30Days, activity.Daily.Sum(day => day.Accounts));
        Assert.Equal(activity.ChangeRequests.Last30Days.Total, activity.Daily.Sum(day => day.ChangeRequests));
        Assert.Equal(activity.FlagClaims.Last30Days.Total, activity.Daily.Sum(day => day.FlagClaims));
        Assert.Equal(activity.Receipts.Last30Days, activity.Daily.Sum(day => day.Receipts));

        var today = activity.Daily[^1];
        Assert.Equal(1, today.Accounts);
        Assert.Equal(1, today.ChangeRequests);
        Assert.Equal(1, today.FlagClaims);
        Assert.Equal(1, today.Receipts);
        Assert.Equal(1, activity.Daily.Single(day => day.Date == "2026-06-08").Accounts);
        Assert.Equal(0, activity.Daily.Single(day => day.Date == "2026-05-18").Accounts);
        Assert.Equal(0, activity.Daily.Single(day => day.Date == "2026-05-18").ChangeRequests);
        Assert.Null(activity.PageViews);
        Assert.Equal("2026-05-16", activity.Registrations[0].Date);
        Assert.Equal("2026-06-15", activity.Registrations[^1].Date);
        Assert.Equal(activity.Accounts.AllTime, activity.Registrations.Sum(day => day.Count));
        Assert.Equal(1, activity.Registrations.Single(day => day.Date == "2026-05-16").Count);
        Assert.Equal(0, activity.Registrations.Single(day => day.Date == "2026-05-18").Count);
    }

    [Fact]
    public async Task Endpoint_is_admin_only_and_windows_match_the_stored_rows()
    {
        await using var factory = new PfohApiFactory();
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/admin/activity");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var member = await client.SendAsync(Authed("member-1", "Supporter"));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        var adminRole = await client.SendAsync(Authed("admin-role", "Admin"));
        Assert.Equal(HttpStatusCode.OK, adminRole.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            db.HonoreeChangeRequests.AddRange(
                Change("Submitted", now.AddHours(-1)),
                Change("Submitted", now.AddDays(-1)),
                Change("Approved", now.AddDays(-10)),
                Change("Rejected", now.AddDays(-40)));
            db.FlagClaims.AddRange(
                Claim("Claimed", now.AddHours(-2)),
                Claim("Cancelled", now.AddDays(-10)),
                Claim("Rejected", now.AddDays(-40)));
            db.SubmissionReceipts.AddRange(
                Receipt(11, now.AddHours(-3)),
                Receipt(12, now.AddDays(-10)),
                Receipt(13, now.AddDays(-40)));
            db.UserProfiles.AddRange(
                Profile("acct-recent", now.AddHours(-1)),
                Profile("acct-old", now.AddDays(-40)));
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Authed("admin-1", "PFOH.Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-user@example.test", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret Name", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretFirst", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("PFOH-SECRET", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner-secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("user_Id", raw, StringComparison.Ordinal);

        var activity = JsonSerializer.Deserialize<AdminActivityResponse>(raw, Json);
        Assert.NotNull(activity);
        AssertStatus(activity.ChangeRequests.Last7Days, 2, ("Submitted", 2));
        AssertStatus(activity.ChangeRequests.Last30Days, 3, ("Approved", 1), ("Submitted", 2));
        AssertStatus(activity.FlagClaims.Last7Days, 1, ("Claimed", 1));
        AssertStatus(activity.FlagClaims.Last30Days, 2, ("Cancelled", 1), ("Claimed", 1));
        Assert.Equal(1, activity.Receipts.Last7Days);
        Assert.Equal(2, activity.Receipts.Last30Days);
        Assert.Equal(30, activity.Daily.Count);
        Assert.Equal(activity.ChangeRequests.Last7Days.Total, activity.ChangeRequests.Last7Days.ByStatus.Sum(row => row.Count));
        Assert.Equal(activity.ChangeRequests.Last30Days.Total, activity.ChangeRequests.Last30Days.ByStatus.Sum(row => row.Count));
        Assert.Equal(activity.FlagClaims.Last7Days.Total, activity.FlagClaims.Last7Days.ByStatus.Sum(row => row.Count));
        Assert.Equal(activity.FlagClaims.Last30Days.Total, activity.FlagClaims.Last30Days.ByStatus.Sum(row => row.Count));

        await using var check = factory.Services.CreateAsyncScope();
        var stored = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var (start7, start30, end) = AdminActivityWindows.For(activity.AsOfUtc);
        var profiles = await stored.UserProfiles.AsNoTracking().ToListAsync();
        Assert.Contains(profiles, profile => profile.Email == "secret-user@example.test");
        Assert.Equal(profiles.Count, activity.Accounts.AllTime);
        Assert.Null(activity.PageViews);
        Assert.Equal(profiles.Count(profile => profile.CreatedUtc < end), activity.Registrations.Sum(day => day.Count));
        Assert.Equal(profiles.Count(profile => profile.CreatedUtc >= start7 && profile.CreatedUtc < end), activity.Accounts.Last7Days);
        Assert.Equal(profiles.Count(profile => profile.CreatedUtc >= start30 && profile.CreatedUtc < end), activity.Accounts.Last30Days);
        Assert.True(activity.Accounts.AllTime >= activity.Accounts.Last30Days);
        Assert.True(activity.Accounts.Last30Days >= activity.Accounts.Last7Days);
    }

    private static void AssertStatus(StatusWindow window, int total, params (string Status, int Count)[] expected)
    {
        Assert.Equal(total, window.Total);
        Assert.Equal(window.Total, window.ByStatus.Sum(row => row.Count));
        Assert.Equal(expected.Select(row => row.Status), window.ByStatus.Select(row => row.Status));
        Assert.Equal(expected.Select(row => row.Count), window.ByStatus.Select(row => row.Count));
    }

    private static HttpRequestMessage Authed(string oid, string roles)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/activity");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Example Admin");
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        return request;
    }

    private static UserProfile Profile(string id, DateTime created) => new()
    {
        OwnerObjectId = id,
        DisplayName = "Secret Name",
        Email = "secret-user@example.test",
        Phone = "+12145550100",
        CreatedUtc = created,
        UpdatedUtc = created
    };

    private static HonoreeChangeRequest Change(string status, DateTime created) => new()
    {
        FlagClaimId = 1,
        FirstName = "SecretFirst",
        LastName = "SecretLast",
        RequestStatus = status,
        SubmitterEmailAddress = "secret-user@example.test",
        CreatedUtc = created
    };

    private static FlagClaim Claim(string status, DateTime created) => new()
    {
        ExternalUserObjectId = "owner-secret-" + status + created.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ExternalUserEmail = "secret-user@example.test",
        ExternalUserName = "Secret Name",
        ClaimStatus = status,
        CreatedUtc = created
    };

    private static SubmissionReceipt Receipt(int id, DateTime created) => new()
    {
        ChangeRequestId = id,
        PublicToken = Guid.Parse($"aaaaaaaa-bbbb-cccc-dddd-{id:x12}"),
        PublicCode = "PFOH-SECRET" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        CreatedUtc = created
    };
}
