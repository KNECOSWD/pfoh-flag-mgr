using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public sealed class ReactivationRedeemRaceInterceptor : SaveChangesInterceptor
{
    public static int FailuresRemaining;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfRacing(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfRacing(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ThrowIfRacing(DbContext? context)
    {
        if (FailuresRemaining <= 0 || context is null)
        {
            return;
        }

        var racing = context.ChangeTracker.Entries<HonoreeReactivationRequest>()
            .Any(entry => entry.State == EntityState.Added);
        if (!racing)
        {
            return;
        }

        FailuresRemaining--;
        throw new DbUpdateException(
            "Unique constraint failed.",
            new InvalidOperationException("Violation of UNIQUE KEY constraint 'UQ_HonoreeReactivationRequests_TokenId'."));
    }
}

public class ReactivationTokenTests
{
    [Fact]
    public void Token_is_32_url_safe_bytes_and_only_the_hash_is_compared()
    {
        var token = ReactivationTokenSecret.Create();
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.True(ReactivationTokenSecret.TryHash(token, out var hash));
        Assert.Equal(32, hash.Length);
        Assert.True(ReactivationTokenSecret.HashEquals(hash, hash));
        var other = SHA256(new byte[32]);
        Assert.False(ReactivationTokenSecret.HashEquals(hash, other));
        Assert.False(ReactivationTokenSecret.TryHash("not-a-token", out _));
    }

    [Fact]
    public void Lifetime_defaults_to_60_days()
    {
        var blank = new ConfigurationBuilder().Build();
        Assert.Equal(60, ReactivationLinkService.LifetimeDays(blank));
        var zero = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Reactivation:LifetimeDays"] = "0" })
            .Build();
        Assert.Equal(60, ReactivationLinkService.LifetimeDays(zero));
        var custom = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Reactivation:LifetimeDays"] = "15" })
            .Build();
        Assert.Equal(15, ReactivationLinkService.LifetimeDays(custom));
    }

    [Fact]
    public void Audit_lines_name_the_action_and_omit_email_and_the_token()
    {
        var logs = new List<string>();
        var logger = new CaptureLogger(logs);
        ReactivationAudit.Generated(logger, 7);
        ReactivationAudit.Revoked(logger, 7);
        ReactivationAudit.Used(logger, 7);
        ReactivationAudit.Approved(logger, 7, 11, true);

        var written = string.Join('\n', logs);
        Assert.Contains("generated", written, StringComparison.Ordinal);
        Assert.Contains("revoked", written, StringComparison.Ordinal);
        Assert.Contains("used", written, StringComparison.Ordinal);
        Assert.Contains("approved", written, StringComparison.Ordinal);
        Assert.Contains("HonoreeId=7", written, StringComparison.Ordinal);
        Assert.DoesNotContain("@", written, StringComparison.Ordinal);
        Assert.DoesNotContain("reactivate/", written, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Telemetry_redacts_the_reactivation_token()
    {
        var token = new string('a', 43);
        var redacted = AdminIdentityTelemetryInitializer.RedactText(
            "GET https://flags.example.test/api/reactivate/" + token);
        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.Contains("/api/reactivate/{token}", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Reactivation_code_does_not_send_email()
    {
        var root = Path.Combine(FindRepoRoot(), "backend", "PFOH.Api");
        var files = Directory.GetFiles(root, "*Reactivation*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var source = string.Join('\n', files.Select(File.ReadAllText));
        Assert.DoesNotContain("SmtpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MailMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendGrid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMail", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_keeps_the_eligibility_reason_column()
    {
        var csv = ReactivationCsv.Write(
        [
            new("4", "TEST Reactivation Honoree", "requester@example.test", "https://flags.example.test/reactivate/abc", "2026-12-01T00:00:00Z", "Eligible"),
            new("9", "", "", "", "", "not found")
        ]);
        Assert.StartsWith("honoree id,name,requester email,link,expiry,eligibility reason", csv, StringComparison.Ordinal);
        Assert.Contains("not found", csv, StringComparison.Ordinal);
        var formula = ReactivationCsv.Write(
        [
            new("1", "=cmd|'/c calc'!A0", "+1", "-1", "@sum", "\tkept")
        ]);
        Assert.Contains("'=cmd|'/c calc'!A0", formula, StringComparison.Ordinal);
        Assert.Contains("'+1", formula, StringComparison.Ordinal);
        Assert.Contains("'-1", formula, StringComparison.Ordinal);
        Assert.Contains("'@sum", formula, StringComparison.Ordinal);
        Assert.Contains("'\tkept", formula, StringComparison.Ordinal);
        Assert.Contains("\r", ReactivationCsv.Neutralize("\rline"), StringComparison.Ordinal);
        Assert.StartsWith("'", ReactivationCsv.Neutralize("\rline"), StringComparison.Ordinal);
        var parsed = ReactivationCsv.Parse("honoree id,requester email\n4,requester@example.test\nnope,\n");
        Assert.Equal("4", parsed[0].HonoreeId);
        Assert.Equal("requester@example.test", parsed[0].Email);
        Assert.Equal("nope", parsed[1].HonoreeId);
        Assert.Null(parsed[1].Email);
    }

    private static byte[] SHA256(byte[] bytes) => System.Security.Cryptography.SHA256.HashData(bytes);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root was not found.");
    }

    private sealed class CaptureLogger(List<string> lines) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}

public class ReactivationLinkTests(ReactivationApiFactory factory) : IClassFixture<ReactivationApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Requester = "requester@example.test";
    private const string OtherRequester = "other-requester@example.test";
    private const string HonoreeName = "TEST Reactivation Honoree";

    [Fact]
    public async Task Valid_link_requires_the_bound_email_and_does_not_activate_or_assign_a_flag()
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var client = factory.CreateClient();
        var link = await GenerateAsync(client, honoreeId);
        var token = TokenOf(link);

        var anonymous = await client.GetFromJsonAsync<StateDto>($"/api/reactivate/{token}", Json);
        Assert.NotNull(anonymous);
        Assert.Equal("sign-in", anonymous.Status);
        Assert.Null(anonymous.Honoree);

        var mismatch = await client.SendAsync(Authed(HttpMethod.Get, $"/api/reactivate/{token}", "other-user", "someone-else@example.test", "Someone Else"));
        var mismatchBody = await mismatch.Content.ReadFromJsonAsync<StateDto>(Json);
        Assert.Equal("email-mismatch", mismatchBody!.Status);
        Assert.Null(mismatchBody.Honoree);
        Assert.DoesNotContain(HonoreeName, mismatchBody.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Requester, mismatchBody.Message, StringComparison.OrdinalIgnoreCase);

        var ready = await client.SendAsync(Authed(HttpMethod.Get, $"/api/reactivate/{token}", "requester-user", Requester, "Test Requester"));
        var readyBody = await ready.Content.ReadFromJsonAsync<StateDto>(Json);
        Assert.Equal("ready", readyBody!.Status);
        Assert.Equal(HonoreeName, readyBody.Honoree!.Name);
        Assert.Equal("Sergeant", readyBody.Honoree.Rank);
        Assert.DoesNotContain(Requester, JsonSerializer.Serialize(readyBody), StringComparison.OrdinalIgnoreCase);

        var submit = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/reactivate/{token}",
            "requester-user",
            Requester,
            "Test Requester",
            body: new { note = "Please put this flag back." }));
        var submitted = await submit.Content.ReadFromJsonAsync<StateDto>(Json);
        Assert.Equal("submitted", submitted!.Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await db.Honorees.SingleAsync(item => item.Id == honoreeId);
        Assert.False(honoree.IsActive);
        Assert.Null(honoree.FlagGridId);
        var change = await db.HonoreeChangeRequests.SingleAsync();
        Assert.Equal("Submitted", change.RequestStatus);
        Assert.Null(change.FlagGridId);
        var used = await db.HonoreeReactivationTokens.SingleAsync();
        Assert.NotNull(used.UsedUtc);

        var pending = await client.SendAsync(Authed(HttpMethod.Get, "/api/admin/review/pending", "admin-user", "admin@example.test", "Admin", roles: "PFOH.Admin"));
        var queue = await pending.Content.ReadFromJsonAsync<List<ReviewDto>>(Json);
        var item = Assert.Single(queue!);
        Assert.Equal("Reactivation", item.SubmissionKind);
        Assert.Equal("Please put this flag back.", item.RequesterNote);
        Assert.NotNull(item.LinkUsedUtc);
        Assert.Contains("Reactivation request", FrontendLabelSource(), StringComparison.Ordinal);

        var again = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/reactivate/{token}",
            "requester-user",
            Requester,
            "Test Requester",
            body: new { note = "Second try" }));
        var againBody = await again.Content.ReadFromJsonAsync<StateDto>(Json);
        Assert.Equal("invalid", againBody!.Status);
        Assert.Equal(ReactivationLinkService.InvalidMessage, againBody.Message);
        Assert.Equal(1, await db.HonoreeChangeRequests.CountAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("used")]
    [InlineData("active")]
    [InlineData("flagged")]
    public async Task Unusable_links_share_one_message(string kind)
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var client = factory.CreateClient();
        var token = TokenOf(await GenerateAsync(client, honoreeId));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var row = await db.HonoreeReactivationTokens.SingleAsync();
            var honoree = await db.Honorees.SingleAsync();
            if (kind == "expired") row.ExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
            if (kind == "revoked") row.RevokedUtc = DateTime.UtcNow;
            if (kind == "used") row.UsedUtc = DateTime.UtcNow;
            if (kind == "active") honoree.IsActive = true;
            if (kind == "flagged") honoree.FlagGridId = await SeedGridAsync(db, "B-02");
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Authed(HttpMethod.Get, $"/api/reactivate/{token}", "requester-user", Requester, "Test Requester"));
        var body = await response.Content.ReadFromJsonAsync<StateDto>(Json);
        Assert.Equal("invalid", body!.Status);
        Assert.Equal(ReactivationLinkService.InvalidMessage, body.Message);
        Assert.Null(body.Honoree);
        Assert.DoesNotContain(HonoreeName, body.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Requester, body.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, body.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_assigns_the_next_flag_and_rejection_leaves_the_honoree_inactive()
    {
        await ResetAsync();
        await SeedGridAsync("A-01");
        await SeedGridAsync("C-28");
        var honoreeId = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var client = factory.CreateClient();
        var token = TokenOf(await GenerateAsync(client, honoreeId));
        var submit = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/reactivate/{token}",
            "requester-user",
            Requester,
            "Test Requester",
            body: new { note = "Back on the field" }));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        int changeId;
        await using (var before = factory.Services.CreateAsyncScope())
        {
            var db = before.ServiceProvider.GetRequiredService<PfohDbContext>();
            var honoree = await db.Honorees.SingleAsync(item => item.Id == honoreeId);
            Assert.False(honoree.IsActive);
            Assert.Null(honoree.FlagGridId);
            changeId = (await db.HonoreeChangeRequests.SingleAsync()).Id;
        }

        var rejectId = await SeedInactiveAsync(OtherRequester, "TEST", "Rejected Honoree");
        var rejectToken = TokenOf(await GenerateAsync(client, rejectId));
        await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/reactivate/{rejectToken}",
            "other-user",
            OtherRequester,
            "Other Requester",
            body: new { note = "" }));
        int rejectChangeId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            rejectChangeId = await db.HonoreeChangeRequests
                .Where(item => item.HonoreeId == rejectId)
                .Select(item => item.Id)
                .SingleAsync();
        }

        var rejected = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{rejectChangeId}/reject",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { reviewNotes = "Not this season." }));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var approved = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{changeId}/approve",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { requiresCardReprint = false, reviewNotes = "Approved" }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var approvedBody = await approved.Content.ReadFromJsonAsync<ApproveDto>(Json);
        Assert.True(approvedBody!.FlagSpotAssigned);
        Assert.Equal("A-01", approvedBody.FlagGridName);
        Assert.Equal("Reactivation", approvedBody.SubmissionKind);

        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var restored = await database.Honorees.SingleAsync(item => item.Id == honoreeId);
        Assert.True(restored.IsActive);
        Assert.Equal("A-01", (await database.FlagGrids.SingleAsync(grid => grid.Id == restored.FlagGridId)).FlagGridName);
        var declined = await database.Honorees.SingleAsync(item => item.Id == rejectId);
        Assert.False(declined.IsActive);
        Assert.Null(declined.FlagGridId);
        Assert.Equal("Rejected", (await database.HonoreeChangeRequests.SingleAsync(item => item.Id == rejectChangeId)).RequestStatus);
    }

    [Fact]
    public async Task Approval_with_no_open_spot_does_not_invent_a_flag()
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var client = factory.CreateClient();
        var token = TokenOf(await GenerateAsync(client, honoreeId));
        await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/reactivate/{token}",
            "requester-user",
            Requester,
            "Test Requester",
            body: new { note = "No spot yet" }));
        int changeId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            changeId = (await db.HonoreeChangeRequests.SingleAsync()).Id;
        }

        var approved = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{changeId}/approve",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { requiresCardReprint = false, reviewNotes = "Approved" }));
        var body = await approved.Content.ReadFromJsonAsync<ApproveDto>(Json);
        Assert.False(body!.FlagSpotAssigned);

        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var honoree = await database.Honorees.SingleAsync(item => item.Id == honoreeId);
        Assert.True(honoree.IsActive);
        Assert.Null(honoree.FlagGridId);
    }

    [Fact]
    public async Task Bulk_generation_covers_every_requester_and_csv_rows_report_why_they_were_skipped()
    {
        await ResetAsync();
        var first = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var second = await SeedInactiveAsync(OtherRequester, "TEST", "Second Honoree");
        var active = await SeedInactiveAsync("active-requester@example.test", "TEST", "Active Honoree", active: true);
        var flagged = await SeedInactiveAsync("flagged-requester@example.test", "TEST", "Flagged Honoree", flagGridName: "D-04");
        var noEmail = await SeedInactiveAsync(null, "TEST", "No Email Honoree");
        var client = factory.CreateClient();

        var all = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/links",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { allEligible = true }));
        var generated = await all.Content.ReadFromJsonAsync<GenerateDto>(Json);
        Assert.NotNull(generated);
        Assert.StartsWith("honoree id,name,requester email,link,expiry,eligibility reason", generated.Csv, StringComparison.Ordinal);
        Assert.Equal(2, generated.Rows.Count);
        Assert.All(generated.Rows, row => Assert.Equal("Eligible", row.EligibilityReason));
        Assert.Contains(generated.Rows, row => row.RequesterEmail == Requester && row.Link.StartsWith("https://flags.example.test/reactivate/", StringComparison.Ordinal));
        Assert.Contains(generated.Rows, row => row.RequesterEmail == OtherRequester);

        var oldToken = TokenOf(generated.Rows.Single(row => row.HonoreeId == first.ToString()).Link);
        var again = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/links",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { honoreeIds = new[] { first } }));
        var regenerated = await again.Content.ReadFromJsonAsync<GenerateDto>(Json);
        var newToken = TokenOf(Assert.Single(regenerated!.Rows).Link);
        Assert.NotEqual(oldToken, newToken);
        var stale = await client.GetFromJsonAsync<StateDto>($"/api/reactivate/{oldToken}", Json);
        Assert.Equal("invalid", stale!.Status);

        var csv = string.Join("\n",
            "honoree id,email",
            "99999,missing@example.test",
            $"{active},active-requester@example.test",
            $"{flagged},flagged-requester@example.test",
            $"{noEmail},",
            $"{second},wrong@example.test",
            $"{second},");
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "honorees.csv");
        var imported = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/links/csv",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: form));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var report = await imported.Content.ReadFromJsonAsync<GenerateDto>(Json);
        Assert.Equal(
            ["not found", "already active", "has a flag", "no email", "email mismatch", "Eligible"],
            report!.Rows.Select(row => row.EligibilityReason).ToArray());
        Assert.All(report.Rows.Take(5), row => Assert.Equal(string.Empty, row.Link));
        Assert.StartsWith("https://flags.example.test/reactivate/", report.Rows[5].Link, StringComparison.Ordinal);
        Assert.Contains("eligibility reason", report.Csv, StringComparison.Ordinal);

        var list = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/reactivation/eligible",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin"));
        var eligible = await list.Content.ReadFromJsonAsync<List<EligibleDto>>(Json);
        Assert.DoesNotContain(eligible!, row => row.HonoreeId == active || row.HonoreeId == flagged || row.HonoreeId == noEmail);
        Assert.Contains(eligible!, row => row.HonoreeId == first);
        Assert.Contains(eligible!, row => row.HonoreeId == second);

        var revoke = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/reactivation/{second}/revoke",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin"));
        var revoked = await revoke.Content.ReadFromJsonAsync<RevokeDto>(Json);
        Assert.True(revoked!.Revoked);
    }

    [Fact]
    public async Task Generating_with_no_selection_does_not_revoke_existing_links()
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var client = factory.CreateClient();
        var token = TokenOf(await GenerateAsync(client, honoreeId));

        var empty = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/links",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { honoreeIds = Array.Empty<int>() }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var still = await client.GetFromJsonAsync<StateDto>($"/api/reactivate/{token}", Json);
        Assert.Equal("sign-in", still!.Status);
    }

    [Fact]
    public async Task Bulk_revoke_clears_selected_links_and_every_active_link()
    {
        await ResetAsync();
        var first = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var second = await SeedInactiveAsync(OtherRequester, "TEST", "Second Honoree");
        var third = await SeedInactiveAsync("third-requester@example.test", "TEST", "Third Honoree");
        var client = factory.CreateClient();
        var firstToken = TokenOf(await GenerateAsync(client, first));
        var secondToken = TokenOf(await GenerateAsync(client, second));
        var thirdToken = TokenOf(await GenerateAsync(client, third));

        var selected = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/revoke",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { honoreeIds = new[] { first, second } }));
        var selectedBody = await selected.Content.ReadFromJsonAsync<RevokeBulkDto>(Json);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(2, selectedBody!.RevokedCount);
        Assert.Equal("invalid", (await client.GetFromJsonAsync<StateDto>($"/api/reactivate/{firstToken}", Json))!.Status);
        Assert.Equal("invalid", (await client.GetFromJsonAsync<StateDto>($"/api/reactivate/{secondToken}", Json))!.Status);
        Assert.Equal("sign-in", (await client.GetFromJsonAsync<StateDto>($"/api/reactivate/{thirdToken}", Json))!.Status);

        var none = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/revoke",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { honoreeIds = Array.Empty<int>() }));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var all = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/revoke",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { all = true }));
        var allBody = await all.Content.ReadFromJsonAsync<RevokeBulkDto>(Json);
        Assert.Equal(1, allBody!.RevokedCount);
        Assert.Equal("invalid", (await client.GetFromJsonAsync<StateDto>($"/api/reactivate/{thirdToken}", Json))!.Status);
    }

    [Fact]
    public async Task A_losing_redeem_returns_the_generic_invalid_message()
    {
        await ResetAsync();
        var honoreeId = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var client = factory.CreateClient();
        var token = TokenOf(await GenerateAsync(client, honoreeId));
        ReactivationRedeemRaceInterceptor.FailuresRemaining = 1;
        try
        {
            var response = await client.SendAsync(Authed(
                HttpMethod.Post,
                $"/api/reactivate/{token}",
                "requester-user",
                Requester,
                "Test Requester",
                body: new { note = "Race" }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<StateDto>(Json);
            Assert.Equal("invalid", body!.Status);
            Assert.Equal(ReactivationLinkService.InvalidMessage, body.Message);
        }
        finally
        {
            ReactivationRedeemRaceInterceptor.FailuresRemaining = 0;
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Empty(await db.HonoreeChangeRequests.ToListAsync());
        Assert.Null((await db.HonoreeReactivationTokens.SingleAsync()).UsedUtc);
    }

    [Fact]
    public async Task Approval_stops_when_the_honoree_is_no_longer_inactive_and_unflagged()
    {
        await ResetAsync();
        await SeedGridAsync("A-01");
        var activeId = await SeedInactiveAsync(Requester, "TEST", "Reactivation Honoree");
        var flaggedId = await SeedInactiveAsync(OtherRequester, "TEST", "Flagged Honoree");
        var client = factory.CreateClient();
        await SubmitAsync(client, TokenOf(await GenerateAsync(client, activeId)), Requester, "requester-user");
        await SubmitAsync(client, TokenOf(await GenerateAsync(client, flaggedId)), OtherRequester, "other-user");

        int activeChange;
        int flaggedChange;
        int gridId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            activeChange = await db.HonoreeChangeRequests.Where(item => item.HonoreeId == activeId).Select(item => item.Id).SingleAsync();
            flaggedChange = await db.HonoreeChangeRequests.Where(item => item.HonoreeId == flaggedId).Select(item => item.Id).SingleAsync();
            var active = await db.Honorees.SingleAsync(item => item.Id == activeId);
            active.IsActive = true;
            gridId = await db.FlagGrids.Select(grid => grid.Id).FirstAsync();
            var flagged = await db.Honorees.SingleAsync(item => item.Id == flaggedId);
            flagged.FlagGridId = gridId;
            var grid = await db.FlagGrids.SingleAsync(item => item.Id == gridId);
            grid.HonoreeId = flaggedId;
            await db.SaveChangesAsync();
        }

        var activeResponse = await ApproveAsync(client, activeChange);
        Assert.Equal(HttpStatusCode.Conflict, activeResponse.StatusCode);
        var flaggedResponse = await ApproveAsync(client, flaggedChange);
        Assert.Equal(HttpStatusCode.Conflict, flaggedResponse.StatusCode);
        Assert.Contains("no longer inactive and unflagged", await activeResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var check = factory.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var stillActive = await database.Honorees.SingleAsync(item => item.Id == activeId);
        Assert.True(stillActive.IsActive);
        Assert.Null(stillActive.FlagGridId);
        var stillFlagged = await database.Honorees.SingleAsync(item => item.Id == flaggedId);
        Assert.False(stillFlagged.IsActive);
        Assert.Equal(gridId, stillFlagged.FlagGridId);
        Assert.Equal("Submitted", (await database.HonoreeChangeRequests.SingleAsync(item => item.Id == activeChange)).RequestStatus);
        Assert.Equal("Submitted", (await database.HonoreeChangeRequests.SingleAsync(item => item.Id == flaggedChange)).RequestStatus);
    }

    [Fact]
    public async Task The_reactivate_endpoint_is_rate_limited()
    {
        await using var limited = new PfohApiFactory
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["RateLimit:ReactivatePermit"] = "2"
            }
        };
        var client = limited.CreateClient();
        var token = new string('b', 43);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/reactivate/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/reactivate/{token}")).StatusCode);
        var blocked = await client.GetAsync($"/api/reactivate/{token}");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    private static async Task SubmitAsync(HttpClient client, string token, string email, string owner)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/reactivate/{token}",
            owner,
            email,
            "Test Requester",
            body: new { note = "Please restore this honoree." }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ApproveAsync(HttpClient client, int changeId)
    {
        return await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{changeId}/approve",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { requiresCardReprint = false, reviewNotes = "Approved" }));
    }

    private async Task<string> GenerateAsync(HttpClient client, int honoreeId)
    {
        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            "/api/admin/reactivation/links",
            "admin-user",
            "admin@example.test",
            "Admin",
            roles: "PFOH.Admin",
            body: new { honoreeIds = new[] { honoreeId } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var generated = await response.Content.ReadFromJsonAsync<GenerateDto>(Json);
        var row = Assert.Single(generated!.Rows);
        Assert.Equal("Eligible", row.EligibilityReason);
        return row.Link;
    }

    private static string TokenOf(string link)
    {
        const string marker = "/reactivate/";
        return link[(link.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
    }

    private static string FrontendLabelSource() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "frontend", "src", "submissionDisplay.js"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root was not found.");
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.HonoreeReactivationRequests.RemoveRange(db.HonoreeReactivationRequests);
        db.HonoreeReactivationTokens.RemoveRange(db.HonoreeReactivationTokens);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.Sponsors.RemoveRange(db.Sponsors);
        db.SponsorCategories.RemoveRange(db.SponsorCategories);
        await db.SaveChangesAsync();
        if (!await db.SponsorCategories.AnyAsync())
        {
            var now = DateTime.UtcNow;
            db.SponsorCategories.Add(new SponsorCategory
            {
                SponsorCategoryName = "Family",
                Description = "Family",
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<int> SeedInactiveAsync(
        string? email,
        string first,
        string last,
        bool active = false,
        string? flagGridName = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        int? sponsorId = null;
        if (email is not null)
        {
            var categoryId = await db.SponsorCategories.Select(category => category.Id).FirstAsync();
            var sponsor = new Sponsor
            {
                SponsorCategoryId = categoryId,
                Description = "Test requester",
                FirstName = "Test",
                LastName = "Requester",
                Salutation = string.Empty,
                PhoneNumber = string.Empty,
                EmailAddress = email,
                StreetAddress = string.Empty,
                City = string.Empty,
                State = string.Empty,
                ZipCode = string.Empty,
                IsActive = true,
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            };
            db.Sponsors.Add(sponsor);
            await db.SaveChangesAsync();
            sponsorId = sponsor.Id;
        }

        int? gridId = null;
        if (flagGridName is not null)
        {
            gridId = await SeedGridAsync(db, flagGridName);
        }

        var honoree = new Honoree
        {
            SponsorId = sponsorId,
            FlagGridId = gridId,
            FirstName = first,
            LastName = last,
            Rank = "Sergeant",
            Description = "A clearly fake tribute.",
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
            PhoneNumber = string.Empty,
            EmailAddress = email ?? string.Empty,
            Awards = "Example award",
            ConflictsServed = "Example conflict",
            DatesUserEntry = "1968-1970",
            IsActive = active,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.Honorees.Add(honoree);
        await db.SaveChangesAsync();
        if (gridId is int assigned)
        {
            var grid = await db.FlagGrids.SingleAsync(item => item.Id == assigned);
            grid.HonoreeId = honoree.Id;
            await db.SaveChangesAsync();
        }

        return honoree.Id;
    }

    private async Task SeedGridAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        await SeedGridAsync(db, name);
    }

    private static async Task<int> SeedGridAsync(PfohDbContext db, string name)
    {
        var existing = await db.FlagGrids.FirstOrDefaultAsync(grid => grid.FlagGridName == name);
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = DateTime.UtcNow;
        var grid = new FlagGrid
        {
            FlagGridName = name,
            Notes = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.FlagGrids.Add(grid);
        await db.SaveChangesAsync();
        return grid.Id;
    }

    private static HttpRequestMessage Authed(
        HttpMethod method,
        string url,
        string ownerObjectId,
        string email,
        string name,
        string? roles = null,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
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

    private sealed record StateDto(string Status, string Message, SummaryDto? Honoree);

    private sealed record SummaryDto(string Name, string? Rank);

    private sealed record ReviewDto(string SubmissionKind, string? RequesterNote, DateTime? LinkUsedUtc);

    private sealed record ApproveDto(bool? FlagSpotAssigned, string? FlagGridName, string? SubmissionKind);

    private sealed record GenerateDto(string Csv, List<RowDto> Rows);

    private sealed record RowDto(string HonoreeId, string Name, string RequesterEmail, string Link, string Expiry, string EligibilityReason);

    private sealed record EligibleDto(int HonoreeId, string Name, string RequesterEmail);

    private sealed record RevokeDto(bool Revoked);

    private sealed record RevokeBulkDto(int RevokedCount);
}

public sealed class ReactivationApiFactory : PfohApiFactory
{
    public ReactivationApiFactory()
    {
        ExtraSettings = new Dictionary<string, string?>
        {
            ["App:PublicBaseUrl"] = "https://flags.example.test",
            ["RateLimit:ReactivatePermit"] = "100000",
            ["Reactivation:LifetimeDays"] = "60"
        };
    }
}
