using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class SubmissionCardPdfTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    [Fact]
    public async Task Owner_and_admin_can_open_a_pending_card_with_its_photo_and_another_user_cannot()
    {
        await ResetAsync();
        var photo = Encoding.ASCII.GetBytes("pending-photo");
        await SeedClaimAsync(
            changeId: 41,
            claimId: 11,
            owner: "submitter",
            requestStatus: "Submitted",
            claimStatus: "Submitted",
            honoreeId: null,
            requestFirstName: "PendingAda",
            honoreeFirstName: null,
            requestPhoto: "pending/pending-ada.png",
            honoreePhoto: null);
        factory.Files.Photos["pending/pending-ada.png"] = photo;
        var client = factory.CreateClient();

        var owner = await client.SendAsync(Authed(HttpMethod.Get, "/api/flag-claims/11/card", "submitter", "submitter@example.com", "Submitter"));
        var admin = await client.SendAsync(Authed(HttpMethod.Get, "/api/flag-claims/11/card", "admin-user", "admin@example.com", "Admin", roles: "PFOH.Admin"));
        var other = await client.SendAsync(Authed(HttpMethod.Get, "/api/flag-claims/11/card", "someone-else", "else@example.com", "Someone Else"));
        var missing = await client.SendAsync(Authed(HttpMethod.Get, "/api/flag-claims/999/card", "submitter", "submitter@example.com", "Submitter"));

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal("application/pdf", owner.Content.Headers.ContentType?.MediaType);
        var ownerPdf = await owner.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(ownerPdf, 0, 4));
        Assert.Contains("PendingAda", PdfTitle(ownerPdf));
        Assert.Contains("pending/pending-ada.png", factory.Files.DownloadedImages);

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task An_approved_card_uses_the_current_honoree_and_current_photo()
    {
        await ResetAsync();
        factory.Files.Photos["current-ada.jpg"] = Encoding.ASCII.GetBytes("current-photo");
        factory.Files.Photos["pending/stale.png"] = Encoding.ASCII.GetBytes("stale-photo");
        await SeedClaimAsync(
            changeId: 42,
            claimId: 12,
            owner: "submitter",
            requestStatus: "Approved",
            claimStatus: "Claimed",
            honoreeId: 8,
            requestFirstName: "StaleAda",
            honoreeFirstName: "CurrentAda",
            requestPhoto: "pending/stale.png",
            honoreePhoto: "current-ada.jpg");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authed(
            HttpMethod.Get,
            "/api/flag-claims/12/card",
            "submitter",
            "submitter@example.com",
            "Submitter"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var title = PdfTitle(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("CurrentAda", title);
        Assert.DoesNotContain("StaleAda", title);
        Assert.Contains("current-ada.jpg", factory.Files.DownloadedImages);
        Assert.DoesNotContain("pending/stale.png", factory.Files.DownloadedImages);
    }

    private async Task ResetAsync()
    {
        factory.Files.Reset();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        await db.SaveChangesAsync();
    }

    private async Task SeedClaimAsync(
        int changeId,
        int claimId,
        string owner,
        string requestStatus,
        string claimStatus,
        int? honoreeId,
        string requestFirstName,
        string? honoreeFirstName,
        string? requestPhoto,
        string? honoreePhoto)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        if (honoreeId is int id)
        {
            db.Honorees.Add(new Honoree
            {
                Id = id,
                FirstName = honoreeFirstName ?? "Current",
                LastName = "Lovelace",
                Rank = "Sergeant",
                Description = "Current tribute",
                Salutation = string.Empty,
                NameUserEntry = string.Empty,
                PhotoFileName = honoreePhoto ?? string.Empty,
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
        }

        db.FlagClaims.Add(new FlagClaim
        {
            Id = claimId,
            HonoreeId = honoreeId,
            ExternalUserObjectId = owner,
            ExternalUserEmail = "submitter@example.com",
            ExternalUserName = "Submitter",
            ClaimStatus = claimStatus,
            CreatedUtc = now,
            SubmittedUtc = now
        });
        db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            Id = changeId,
            FlagClaimId = claimId,
            HonoreeId = honoreeId,
            FirstName = requestFirstName,
            LastName = "Lovelace",
            Description = "Pending tribute",
            PhotoFileName = requestPhoto,
            RequestStatus = requestStatus,
            CreatedUtc = now,
            SubmittedUtc = now
        });
        await db.SaveChangesAsync();
    }

    private static string PdfTitle(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        const string marker = "/Title <";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The honoree card PDF is missing its title.");
        start += marker.Length;
        var end = text.IndexOf('>', start);
        Assert.True(end > start, "The honoree card PDF title is incomplete.");
        var hex = string.Concat(text[start..end].Where(character => !char.IsWhiteSpace(character)));
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static HttpRequestMessage Authed(
        HttpMethod method,
        string url,
        string ownerObjectId,
        string email,
        string name,
        string? roles = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, ownerObjectId);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, name);
        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        return request;
    }
}
