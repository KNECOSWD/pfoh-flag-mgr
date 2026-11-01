using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using SkiaSharp;
using Xunit;

namespace PFOH.Api.Tests;

public class HonoreePdfRegenerateAuthTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private const int PhotoHonoreeId = 9101;
    private const int PdfHonoreeId = 9102;
    private const int RegenerateHonoreeId = 9103;

    [Fact]
    public async Task Anonymous_regenerate_is_unauthorized()
    {
        var response = await factory.CreateClient().PostAsync($"/api/honorees/{RegenerateHonoreeId}/pdf/regenerate", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signed_in_non_admin_regenerate_is_forbidden()
    {
        var response = await factory.CreateClient().SendAsync(Regenerate(RegenerateHonoreeId, role: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("PFOH.Admin")]
    [InlineData("Admin")]
    public async Task Admin_regenerate_returns_ok_without_storage_fields(string role)
    {
        await SeedRegenerateHonoreeAsync();
        var png = SyntheticPng();
        factory.Files.Logos["DonorCardBlankLrg.png"] = png;
        factory.Files.Logos["Rotary.png"] = png;

        var response = await factory.CreateClient().SendAsync(Regenerate(RegenerateHonoreeId, role));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        var names = json.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("storageAccount", names);
        Assert.DoesNotContain("container", names);
        Assert.DoesNotContain("honoreepdfs", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BlobStorage", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.RootElement.GetProperty("uploaded").GetBoolean());
        Assert.Equal($"{RegenerateHonoreeId}_HonoreeReport.pdf", json.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(RegenerateHonoreeId, json.RootElement.GetProperty("honoreeId").GetInt32());
        Assert.Contains("saved as", json.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_honoree_actions_stay_anonymous()
    {
        await SeedPhotoHonoreeAsync();
        factory.Files.Pdfs[PdfHonoreeId] = "%PDF-1.4 synthetic"u8.ToArray();
        var client = factory.CreateClient();

        var search = await client.GetAsync("/api/honorees/search");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var searchBody = await search.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, searchBody.ValueKind);

        var photo = await client.GetAsync($"/api/honorees/{PhotoHonoreeId}/photo");
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/png", photo.Content.Headers.ContentType?.MediaType);

        var pdf = await client.GetAsync($"/api/honorees/{PdfHonoreeId}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
    }

    private static HttpRequestMessage Regenerate(int honoreeId, string? role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/honorees/{honoreeId}/pdf/regenerate");
        request.Headers.Add(TestAuthHandler.OidHeader, role is null ? "member-1" : "admin-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, role is null ? "member@example.com" : "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, role is null ? "Member" : "Admin");
        if (role is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, role);
        }

        return request;
    }

    private async Task SeedPhotoHonoreeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (await db.Honorees.AnyAsync(honoree => honoree.Id == PhotoHonoreeId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.Honorees.Add(new Honoree
        {
            Id = PhotoHonoreeId,
            FirstName = "Example",
            LastName = "Photo",
            Rank = "",
            Description = "Synthetic tribute",
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = "example-photo.png",
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
        await db.SaveChangesAsync();
        factory.Files.Photos["example-photo.png"] = SyntheticPng();
    }

    private async Task SeedRegenerateHonoreeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        if (await db.Honorees.AnyAsync(honoree => honoree.Id == RegenerateHonoreeId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.Honorees.Add(new Honoree
        {
            Id = RegenerateHonoreeId,
            FirstName = "Example",
            LastName = "Card",
            Rank = "",
            Description = "Synthetic tribute",
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
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
        await db.SaveChangesAsync();
    }

    private static byte[] SyntheticPng()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.SteelBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
