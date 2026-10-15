using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class ChangeRequestPhotoApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Change_request_photo_requires_authentication()
    {
        await ResetAsync();
        var id = await SeedSubmittedChangeAsync("owner-photo", "pending/15.jpg");
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/flagclaims/change-requests/{id}/photo");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Change_request_photo_forbids_a_signed_in_user_who_does_not_own_the_claim()
    {
        await ResetAsync();
        var id = await SeedSubmittedChangeAsync("owner-photo", "pending/15.jpg");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            $"/api/flagclaims/change-requests/{id}/photo",
            "someone-else",
            "else@example.com",
            "Someone Else"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Change_request_photo_allows_the_claim_owner()
    {
        await ResetAsync();
        var id = await SeedSubmittedChangeAsync("owner-photo", photoFileName: null);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            $"/api/flagclaims/change-requests/{id}/photo",
            "owner-photo",
            "owner@example.com",
            "Olivia Owner"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Pending honoree photo was not found.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_request_photo_allows_an_admin_who_does_not_own_the_claim()
    {
        await ResetAsync();
        var id = await SeedSubmittedChangeAsync("owner-photo", photoFileName: null);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            $"/api/flagclaims/change-requests/{id}/photo",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Pending honoree photo was not found.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_request_photo_streams_from_honoreeimages_for_the_owner_and_an_admin()
    {
        await ResetAsync();
        var id = await SeedSubmittedChangeAsync("owner-photo", "pending/15.jpg");
        var client = factory.CreateClient();

        var owner = await client.SendAsync(Authed(
            HttpMethod.Get,
            $"/api/flagclaims/change-requests/{id}/photo",
            "owner-photo",
            "owner@example.com",
            "Olivia Owner"));
        var admin = await client.SendAsync(Authed(
            HttpMethod.Get,
            $"/api/flagclaims/change-requests/{id}/photo",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "Admin"));

        Assert.Equal(HttpStatusCode.InternalServerError, owner.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, admin.StatusCode);
        Assert.Contains("honoreeimages", await owner.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("honoreeimages", await admin.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_review_includes_the_honoree_photo_and_detail_fields()
    {
        await ResetAsync();
        var id = await SeedSubmittedChangeAsync(
            "owner-photo",
            "pending/15.jpg",
            description: "A tribute for the honoree",
            awards: "Silver Star",
            conflictsServed: "World War II");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/admin/review/pending",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<ReviewItem>>(JsonOptions);
        var item = Assert.Single(items!);
        Assert.Equal(id, item.ChangeRequestId);
        Assert.Equal("pending/15.jpg", item.PhotoFileName);
        Assert.Equal("A tribute for the honoree", item.Description);
        Assert.Equal("Silver Star", item.Awards);
        Assert.Equal("World War II", item.ConflictsServed);
    }

    [Fact]
    public async Task Approve_does_not_approve_when_the_pending_honoree_photo_cannot_be_moved()
    {
        await ResetAsync();
        var id = await SeedSubmittedChangeAsync("owner-photo", "pending/15.jpg");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Post,
            $"/api/admin/review/{id}/approve",
            "admin-user",
            "admin@example.com",
            "Admin",
            roles: "PFOH.Admin",
            body: new { requiresCardReprint = false, reviewNotes = "Approve the honoree" }));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("pending honoree photo", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not approved", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("honoreeimages", body, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var change = await db.HonoreeChangeRequests.SingleAsync(r => r.Id == id);
        Assert.Equal("Submitted", change.RequestStatus);
        Assert.Equal("pending/15.jpg", change.PhotoFileName);
        Assert.Null(change.ReviewedUtc);

        var claim = await db.FlagClaims.SingleAsync(c => c.Id == change.FlagClaimId);
        Assert.Equal("Submitted", claim.ClaimStatus);
        Assert.Null(claim.ApprovedUtc);
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.Sponsors.RemoveRange(db.Sponsors);
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedSubmittedChangeAsync(
        string ownerObjectId,
        string? photoFileName,
        string? description = "Honoree tribute",
        string? awards = "Medal",
        string? conflictsServed = "Service")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;

        db.FlagGrids.Add(new FlagGrid
        {
            Id = 7,
            FlagGridName = "A-1",
            Notes = string.Empty,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });

        db.FlagClaims.Add(new FlagClaim
        {
            Id = 4,
            FlagGridId = 7,
            ExternalUserObjectId = ownerObjectId,
            ExternalUserEmail = "owner@example.com",
            ExternalUserName = "Olivia Owner",
            ClaimStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        });

        var change = new HonoreeChangeRequest
        {
            Id = 15,
            FlagClaimId = 4,
            FlagGridId = 7,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = description,
            Awards = awards,
            ConflictsServed = conflictsServed,
            PhotoFileName = photoFileName,
            RequestStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        };
        db.HonoreeChangeRequests.Add(change);
        await db.SaveChangesAsync();
        return change.Id;
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

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record ReviewItem(
        int ChangeRequestId,
        string? PhotoFileName,
        string? Description,
        string? Awards,
        string? ConflictsServed);
}
