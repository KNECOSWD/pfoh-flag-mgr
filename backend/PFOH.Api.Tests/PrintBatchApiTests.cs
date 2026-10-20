using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class PrintBatchApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly byte[] StoredPdf = "%PDF-1.4 synthetic"u8.ToArray();

    [Fact]
    public async Task Plan_and_card_endpoints_require_an_administrator()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/admin/print/cards/1/pdf");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var plan = await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/print/plan", admin: false, new { changeRequestIds = new[] { 1 } }));
        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);
    }

    [Fact]
    public async Task Plan_orders_queued_cards_by_name_and_names_items_not_in_the_queue()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/api/admin/print/plan",
            admin: true,
            new { changeRequestIds = new[] { ids.Zane, ids.Ann, ids.Rejected, 9999, ids.Ann } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = json.RootElement.GetProperty("cards").EnumerateArray().ToList();
        Assert.Equal(["Ann Able", "Zane Zulu"], cards.Select(c => c.GetProperty("honoreeName").GetString()!).ToArray());
        Assert.Equal([ids.Ann, ids.Zane], cards.Select(c => c.GetProperty("changeRequestId").GetInt32()).ToArray());
        Assert.Equal([101, 102], cards.Select(c => c.GetProperty("honoreeId").GetInt32()).ToArray());
        Assert.Equal(
            [ids.Rejected, 9999],
            json.RootElement.GetProperty("notInQueue").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public async Task Card_pdf_returns_the_stored_pdf_or_reports_it_missing_without_generating_it()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        factory.Files.Pdfs[101] = StoredPdf;
        var client = factory.CreateClient();

        var stored = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/admin/print/cards/{ids.Ann}/pdf", admin: true));
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal("application/pdf", stored.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StoredPdf, await stored.Content.ReadAsByteArrayAsync());

        var missing = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/admin/print/cards/{ids.Zane}/pdf", admin: true));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pdf-missing", body.GetProperty("code").GetString());
        Assert.Equal(102, body.GetProperty("honoreeId").GetInt32());
        Assert.Equal("Zane Zulu", body.GetProperty("honoreeName").GetString());
        Assert.False(factory.Files.Pdfs.ContainsKey(102));

        var notQueued = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/admin/print/cards/{ids.Rejected}/pdf", admin: true));
        Assert.Equal(HttpStatusCode.NotFound, notQueued.StatusCode);
        var notQueuedBody = await notQueued.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not-in-queue", notQueuedBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Card_pdf_names_the_honoree_when_storage_cannot_be_read()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        factory.Files.UnreadablePdfs.Add(101);
        var client = factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/api/admin/print/cards/{ids.Ann}/pdf", admin: true));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("storage-read-failed", body.GetProperty("code").GetString());
        Assert.Equal("Ann Able", body.GetProperty("honoreeName").GetString());
        Assert.Contains("Synthetic storage read failure.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Mark_printed_and_history_require_an_administrator()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var anonymous = await client.PostAsJsonAsync("/api/admin/print/mark-printed", new { changeRequestIds = new[] { 1 } });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var anonymousHistory = await client.GetAsync("/api/admin/print/history");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousHistory.StatusCode);

        var forbidden = await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/print/mark-printed", admin: false, new { changeRequestIds = new[] { 1 } }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var forbiddenHistory = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/print/history", admin: false));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenHistory.StatusCode);
    }

    [Fact]
    public async Task Mark_printed_records_one_event_and_keeps_the_card_batch_columns()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/api/admin/print/mark-printed",
            admin: true,
            new { changeRequestIds = new[] { ids.Ann, ids.Zane, ids.Rejected }, comment = "  Morning run  " }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var batchId = json.RootElement.GetProperty("batchId").GetGuid();
        Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var cards = await db.HonoreeChangeRequests.OrderBy(r => r.Id).ToListAsync();
        var printed = cards.Where(r => r.Id == ids.Ann || r.Id == ids.Zane).ToList();
        Assert.All(printed, card =>
        {
            Assert.Equal(batchId, card.CardPrintBatchId);
            Assert.NotNull(card.CardPrintedUtc);
            Assert.Equal(DateTimeKind.Utc, card.CardPrintedUtc!.Value.Kind);
            Assert.True(card.RequiresCardReprint);
            Assert.Equal("Approved", card.RequestStatus);
        });
        Assert.Equal(printed[0].CardPrintedUtc, printed[1].CardPrintedUtc);

        var rejected = cards.Single(r => r.Id == ids.Rejected);
        Assert.Null(rejected.CardPrintedUtc);
        Assert.Null(rejected.CardPrintBatchId);
        Assert.Equal("Rejected", rejected.RequestStatus);

        var events = await db.CardPrintEvents.ToListAsync();
        var recorded = Assert.Single(events);
        Assert.Equal(batchId, recorded.CardPrintBatchId);
        Assert.Equal(printed[0].CardPrintedUtc, recorded.PrintedUtc);
        Assert.Equal("admin@example.com", recorded.PrintedBy);
        Assert.Equal(2, recorded.CardCount);
        Assert.Equal("Morning run", recorded.Comment);
    }

    [Fact]
    public async Task Mark_printed_uses_the_display_name_when_the_account_has_no_email()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        var client = factory.CreateClient();
        var request = AdminRequest(
            HttpMethod.Post,
            "/api/admin/print/mark-printed",
            admin: true,
            new { changeRequestIds = new[] { ids.Ann }, comment = "" });
        request.Headers.Remove(TestAuthHandler.EmailHeader);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var recorded = Assert.Single(await db.CardPrintEvents.ToListAsync());
        Assert.Equal("Admin", recorded.PrintedBy);
        Assert.Null(recorded.Comment);
        Assert.Equal(1, recorded.CardCount);
    }

    [Fact]
    public async Task A_comment_over_500_characters_is_rejected_and_writes_nothing()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        var client = factory.CreateClient();
        var comment = new string('x', 501);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/api/admin/print/mark-printed",
            admin: true,
            new { changeRequestIds = new[] { ids.Ann }, comment }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Empty(await db.CardPrintEvents.ToListAsync());
        Assert.All(await db.HonoreeChangeRequests.ToListAsync(), card =>
        {
            Assert.Null(card.CardPrintedUtc);
            Assert.Null(card.CardPrintBatchId);
        });
    }

    [Fact]
    public async Task Print_history_lists_events_newest_first()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        var client = factory.CreateClient();

        var first = await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/print/mark-printed", admin: true, new { changeRequestIds = new[] { ids.Ann }, comment = "First" }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.SendAsync(AdminRequest(HttpMethod.Post, "/api/admin/print/mark-printed", admin: true, new { changeRequestIds = new[] { ids.Zane }, comment = "Second" }));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var history = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/print/history", admin: true));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var json = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        var events = json.RootElement.EnumerateArray().ToList();
        Assert.Equal("Second", events[0].GetProperty("comment").GetString());
        Assert.Equal("First", events[1].GetProperty("comment").GetString());
        Assert.Equal([1, 1], events.Select(e => e.GetProperty("cardCount").GetInt32()).ToArray());
        Assert.Equal("admin@example.com", events[0].GetProperty("printedBy").GetString());
        Assert.True(events[0].GetProperty("printedUtc").GetDateTime() >= events[1].GetProperty("printedUtc").GetDateTime());
    }

    [Fact]
    public async Task Mark_printed_with_no_matching_cards_records_no_history()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/api/admin/print/mark-printed",
            admin: true,
            new { changeRequestIds = new[] { ids.Rejected }, comment = "Nothing to print" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Empty(await db.CardPrintEvents.ToListAsync());
        Assert.All(await db.HonoreeChangeRequests.ToListAsync(), card =>
        {
            Assert.Null(card.CardPrintedUtc);
            Assert.Null(card.CardPrintBatchId);
        });

        var history = await client.SendAsync(AdminRequest(HttpMethod.Get, "/api/admin/print/history", admin: true));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Empty(historyJson.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task The_legacy_merge_endpoint_is_gone()
    {
        await ResetAsync();
        var ids = await SeedQueueAsync();
        var client = factory.CreateClient();

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/api/admin/print/merge",
            admin: true,
            new { changeRequestIds = new[] { ids.Ann, ids.Zane } }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Files.Pdfs);
    }

    private async Task ResetAsync()
    {
        factory.Files.Reset();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    private async Task<(int Ann, int Zane, int Rejected)> SeedQueueAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();

        HonoreeChangeRequest Card(int honoreeId, string first, string last, string status, bool reprint) => new()
        {
            FlagClaimId = honoreeId,
            HonoreeId = honoreeId,
            FirstName = first,
            LastName = last,
            RequestStatus = status,
            RequiresCardReprint = reprint
        };

        var zane = Card(102, "Zane", "Zulu", "Approved", true);
        var ann = Card(101, "Ann", "Able", "Approved", true);
        var rejected = Card(103, "Rita", "Rejected", "Rejected", true);
        db.HonoreeChangeRequests.AddRange(zane, ann, rejected);
        await db.SaveChangesAsync();
        return (ann.Id, zane.Id, rejected.Id);
    }

    private static HttpRequestMessage AdminRequest(HttpMethod method, string url, bool admin, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, admin ? "admin-1" : "user-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, admin ? "admin@example.com" : "user@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, admin ? "Admin" : "User");
        if (admin)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
