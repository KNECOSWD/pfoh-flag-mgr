using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class AuthenticatedGetRateLimitTests : IDisposable
{
    private readonly PfohApiFactory factory = new() { AuthenticatedGetPermit = 60 };

    [Fact]
    public async Task Profile_get_returns_429_with_retry_after_after_sixty_reads_for_one_user()
    {
        var client = factory.CreateClient();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 61; i++)
        {
            var response = await client.SendAsync(ProfileGet("rate-limit-user"));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
                Assert.Equal(60, i);
                break;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.NotNull(limited);
        Assert.True(limited!.Headers.TryGetValues("Retry-After", out var retryAfter), "429 includes Retry-After");
        var seconds = int.Parse(Assert.Single(retryAfter));
        Assert.InRange(seconds, 1, 60);

        var other = await client.SendAsync(ProfileGet("someone-else"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        var anonymous = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Health_and_version_stay_available_past_the_authenticated_read_limit()
    {
        var client = factory.CreateClient();
        for (var i = 0; i < 70; i++)
        {
            var health = await client.GetAsync("/health");
            var version = await client.GetAsync("/api/version");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        }
    }

    public void Dispose() => factory.Dispose();

    private static HttpRequestMessage ProfileGet(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add(TestAuthHandler.OidHeader, userId);
        request.Headers.Add(TestAuthHandler.EmailHeader, $"{userId}@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Rate Limit");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

public class RealAuthenticatedGetLimitTests : IDisposable
{
    private readonly PfohApiFactory factory = new() { UseRealAuthenticatedGetLimit = true };

    [Fact]
    public async Task Real_limit_rejects_the_61st_profile_read_and_still_serves_card_pdfs_and_photos()
    {
        var client = factory.CreateClient();
        var cardId = await SeedQueuedCardAsync();
        var photoId = await SeedReviewPhotoAsync();
        const string userId = "bulk-reader";

        HttpResponseMessage? limited = null;
        for (var i = 0; i < 61; i++)
        {
            var response = await client.SendAsync(SignedGet("/api/profile", userId, admin: true));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
                Assert.Equal(60, i);
                break;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.NotNull(limited);

        for (var i = 0; i < 61; i++)
        {
            var pdf = await client.SendAsync(SignedGet($"/api/admin/print/cards/{cardId}/pdf", userId, admin: true));
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            var photo = await client.SendAsync(SignedGet($"/api/flagclaims/change-requests/{photoId}/photo", userId, admin: true));
            Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        }
    }

    public void Dispose() => factory.Dispose();

    private async Task<int> SeedQueuedCardAsync()
    {
        factory.Files.Pdfs[101] = "%PDF-1.4 synthetic"u8.ToArray();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var card = new HonoreeChangeRequest
        {
            FlagClaimId = 101,
            HonoreeId = 101,
            FirstName = "Ann",
            LastName = "Able",
            RequestStatus = "Approved",
            RequiresCardReprint = true
        };
        db.HonoreeChangeRequests.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }

    private async Task<int> SeedReviewPhotoAsync()
    {
        factory.Files.Photos["pending/15.jpg"] = [1, 2, 3];
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        db.FlagClaims.Add(new FlagClaim
        {
            Id = 4,
            ExternalUserObjectId = "owner-photo",
            ExternalUserEmail = "owner@example.com",
            ExternalUserName = "Olivia Owner",
            ClaimStatus = "Submitted",
            CreatedUtc = now,
            SubmittedUtc = now
        });
        var change = new HonoreeChangeRequest
        {
            FlagClaimId = 4,
            FirstName = "Pat",
            LastName = "Photo",
            RequestStatus = "Submitted",
            PhotoFileName = "pending/15.jpg",
            CreatedUtc = now,
            SubmittedUtc = now
        };
        db.HonoreeChangeRequests.Add(change);
        await db.SaveChangesAsync();
        return change.Id;
    }

    private static HttpRequestMessage SignedGet(string url, string userId, bool admin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.OidHeader, userId);
        request.Headers.Add(TestAuthHandler.EmailHeader, $"{userId}@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Bulk Reader");
        if (admin)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

