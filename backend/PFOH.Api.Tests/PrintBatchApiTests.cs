using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
