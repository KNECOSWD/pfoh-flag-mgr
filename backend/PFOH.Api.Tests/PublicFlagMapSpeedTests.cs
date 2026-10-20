using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using SkiaSharp;
using Xunit;

namespace PFOH.Api.Tests;

public class PublicFlagMapSpeedTests : IClassFixture<PfohApiFactory>
{
    private readonly PfohApiFactory factory;

    public PublicFlagMapSpeedTests(PfohApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        if (db.FlagGrids.Any())
        {
            return;
        }

        var category = new ServiceBranchCategory
        {
            ServiceBranchCategoryName = "Armed forces",
            Description = "speed",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.ServiceBranchCategories.Add(category);
        db.SaveChanges();

        var army = new ServiceBranch
        {
            ServiceBranchCategoryId = category.Id,
            ServiceBranchName = "Army",
            Description = "speed",
            LogoFileName = "Army.png",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.ServiceBranches.Add(army);
        db.SaveChanges();

        var grid = new FlagGrid
        {
            FlagGridName = "A-14",
            Reserved = false,
            Notes = "",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        var photoGrid = new FlagGrid
        {
            FlagGridName = "B-02",
            Reserved = false,
            Notes = "",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        var cacheGrid = new FlagGrid
        {
            FlagGridName = "C-03",
            Reserved = false,
            Notes = "",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
        db.FlagGrids.AddRange(grid, photoGrid, cacheGrid);
        db.SaveChanges();

        db.Honorees.AddRange(
            Person("Public", "Quincy", "Example", "Jr", grid, army, category, ""),
            Person("Photo", "", "Person", "", photoGrid, army, category, "wide-photo.png"),
            Person("Cache", "", "Marker", "", cacheGrid, army, category, ""));
        db.SaveChanges();
        factory.Files.Photos["wide-photo.png"] = WidePng();
        factory.Files.Logos["Army.png"] = WidePng();
    }

    [Fact]
    public async Task Search_index_keeps_display_name_and_drops_duplicate_name_parts()
    {
        var body = await factory.CreateClient().GetStringAsync("/api/public/flag-map/search-index");
        using var doc = JsonDocument.Parse(body);
        var hit = doc.RootElement.EnumerateArray().Single(item => item.GetProperty("flagGridName").GetString() == "A-14");
        Assert.Equal("Public Quincy Example Jr", hit.GetProperty("displayName").GetString());
        Assert.Equal("A", hit.GetProperty("section").GetString());
        Assert.Equal("Army", hit.GetProperty("serviceBranchName").GetString());
        var names = hit.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("firstName", names);
        Assert.DoesNotContain("middleName", names);
        Assert.DoesNotContain("lastName", names);
        Assert.DoesNotContain("suffix", names);
        Assert.DoesNotContain("nickname", names);
        Assert.Contains("Quincy", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Field_snapshot_is_one_cached_payload()
    {
        var client = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<PublicFlagMapCache>();
        var before = cache.Builds;
        var first = await client.GetAsync("/api/public/flag-map/field");
        var second = await client.GetAsync("/api/public/flag-map/sections/A");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("public, max-age=60", first.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var body = await first.Content.ReadAsStringAsync();
        Assert.Contains("A-14", body, StringComparison.Ordinal);
        Assert.Contains("\"seats\"", body, StringComparison.Ordinal);
        Assert.True(cache.Builds <= before + 1);
    }

    [Fact]
    public async Task Changing_a_honoree_drops_the_cached_search_index()
    {
        var client = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<PublicFlagMapCache>();
        var first = await client.GetAsync("/api/public/flag-map/search-index");
        var before = cache.Builds;
        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var person = db.Honorees.Single(honoree => honoree.FirstName == "Cache" && honoree.LastName == "Marker");
            person.Nickname = "Speed";
            db.SaveChanges();
        }

        var second = await client.GetAsync("/api/public/flag-map/search-index");
        Assert.NotEqual(etag, second.Headers.ETag?.Tag);
        Assert.Contains("Speed", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before + 1, cache.Builds);
    }

    [Fact]
    public async Task Json_responses_compress_and_logos_stay_images()
    {
        var client = factory.CreateClient();
        var brotli = new HttpRequestMessage(HttpMethod.Get, "/api/public/flag-map/sections");
        brotli.Headers.AcceptEncoding.ParseAdd("br");
        var br = await client.SendAsync(brotli);
        Assert.Equal(HttpStatusCode.OK, br.StatusCode);
        Assert.Contains("br", br.Content.Headers.ContentEncoding);

        var gzipRequest = new HttpRequestMessage(HttpMethod.Get, "/api/public/flag-map/search-index");
        gzipRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        var gzip = await client.SendAsync(gzipRequest);
        Assert.Contains("gzip", gzip.Content.Headers.ContentEncoding);

        var logo = await client.GetAsync("/api/public/flag-map/logos/Army.png?w=256");
        Assert.DoesNotContain("br", logo.Content.Headers.ContentEncoding);
        Assert.DoesNotContain("gzip", logo.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Logos_resize_to_webp_or_png_and_the_packaged_merchant_marine_file_is_served()
    {
        var client = factory.CreateClient();
        var png = await client.GetAsync("/api/public/flag-map/logos/Army.png?w=256");
        var webp = await client.GetAsync("/api/public/flag-map/logos/Army.png?w=128&fmt=webp");
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/webp", webp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", png.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("max-age=31536000", webp.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("Accept", webp.Headers.Vary.ToString());
        var pngBytes = await png.Content.ReadAsByteArrayAsync();
        var webpBytes = await webp.Content.ReadAsByteArrayAsync();
        Assert.True(webpBytes.Length < pngBytes.Length);
        using var decoded = SKBitmap.Decode(webpBytes);
        Assert.NotNull(decoded);
        Assert.True(Math.Max(decoded!.Width, decoded.Height) <= 128);

        var plain = new HttpRequestMessage(HttpMethod.Get, "/api/public/flag-map/logos/Army.png");
        plain.Headers.Accept.ParseAdd("application/json");
        var jsonAccept = await client.SendAsync(plain);
        Assert.Equal("image/png", jsonAccept.Content.Headers.ContentType?.MediaType);

        factory.Files.Logos["Navy.png"] = [0x89, 0x50, 0x4E, 0x47];
        var broken = await client.GetAsync("/api/public/flag-map/logos/Navy.png?fmt=webp");
        Assert.Equal("image/png", broken.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, (await broken.Content.ReadAsByteArrayAsync()).Length);

        var marine = await client.GetAsync("/api/public/flag-map/logos/MerchantMarine.png");
        Assert.Equal(HttpStatusCode.OK, marine.StatusCode);
        Assert.Equal("image/png", marine.Content.Headers.ContentType?.MediaType);
        Assert.True((await marine.Content.ReadAsByteArrayAsync()).Length > 10_000);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/flag-map/logos/NotASeal.png")).StatusCode);
    }

    [Fact]
    public async Task Honoree_photo_is_display_sized_and_stays_uncached()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var id = db.Honorees.Single(honoree => honoree.PhotoFileName == "wide-photo.png").Id;
        var original = factory.Files.Photos["wide-photo.png"];
        var response = await factory.CreateClient().GetAsync($"/api/honorees/{id}/photo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length <= DisplayImage.PhotoBudgetBytes);
        Assert.True(bytes.Length < original.Length);
        var cacheControl = response.Headers.NonValidated["Cache-Control"].ToString();
        Assert.Equal("no-cache, no-store, must-revalidate", cacheControl);
        Assert.DoesNotContain("max-age", cacheControl, StringComparison.Ordinal);
    }

    [Fact]
    public void Hashed_assets_are_immutable_and_html_is_not()
    {
        Assert.True(StaticAssetCaching.IsHashedAsset("/assets/index-BeRASr9J.js"));
        Assert.True(StaticAssetCaching.IsHashedAsset("/assets/american-flag-wave-M_tQIEYf.png"));
        Assert.False(StaticAssetCaching.IsHashedAsset("/assets/Army.png"));
        Assert.Equal(StaticAssetCaching.ImmutableYear, StaticAssetCaching.CacheControlFor("/assets/index-BeRASr9J.js"));
        Assert.Equal(StaticAssetCaching.Html, StaticAssetCaching.CacheControlFor("/index.html"));
        Assert.Null(StaticAssetCaching.CacheControlFor("/assets/Army.png"));
    }

    private static Honoree Person(
        string first,
        string middle,
        string last,
        string suffix,
        FlagGrid grid,
        ServiceBranch branch,
        ServiceBranchCategory category,
        string photo)
    {
        return new Honoree
        {
            FirstName = first,
            MiddleName = middle,
            LastName = last,
            Suffix = suffix,
            Rank = "",
            Description = "Public bio",
            ConflictsServed = "",
            Awards = "",
            PhoneNumber = "",
            EmailAddress = "",
            Salutation = "",
            NameUserEntry = "",
            DatesUserEntry = "",
            PhotoFileName = photo,
            FlagLocation = "",
            IsActive = true,
            FlagGridId = grid.Id,
            ServiceBranchId = branch.Id,
            ServiceBranchCategoryId = category.Id,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };
    }

    private static byte[] WidePng()
    {
        using var bitmap = new SKBitmap(1600, 900);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(18, 48, 96));
        using var paint = new SKPaint { Color = new SKColor(180, 40, 40) };
        canvas.DrawRect(new SKRect(0, 0, 500, 900), paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

public class PublicFlagMapHtmlTests : IClassFixture<PublicFlagMapHtmlTests.HtmlFactory>
{
    private readonly HtmlFactory factory;

    public PublicFlagMapHtmlTests(HtmlFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Flag_map_html_preloads_the_critical_json_and_hashed_assets_are_immutable()
    {
        var client = factory.CreateClient();
        var page = await client.GetAsync("/flag-map");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.False(page.Headers.Contains("X-Robots-Tag"));
        Assert.Contains("no-cache", page.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("/api/public/flag-map/sections", html, StringComparison.Ordinal);
        Assert.Contains("/api/public/flag-map/search-index", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"preload\"", html, StringComparison.Ordinal);

        var asset = await client.GetAsync("/assets/index-BeRASr9J.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("max-age=31536000", asset.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_api_paths_return_404_and_not_the_spa()
    {
        var client = factory.CreateClient();
        foreach (var path in new[] { "/api/public/flag-map/mine", "/api/does-not-exist", "/api" })
        {
            var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Not found", body, StringComparison.Ordinal);
            Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase);
            var cache = response.Headers.NonValidated["Cache-Control"].ToString();
            Assert.Contains("no-store", cache, StringComparison.OrdinalIgnoreCase);
            Assert.Null(response.Headers.ETag);
            Assert.False(response.Headers.TryGetValues("ETag", out _));
        }

        var page = await client.GetAsync("/flag-map");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);

        var sections = await client.GetAsync("/api/public/flag-map/sections");
        Assert.Equal(HttpStatusCode.OK, sections.StatusCode);
        Assert.Contains("json", sections.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class HtmlFactory : PfohApiFactory
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pfoh-flag-map-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            File.WriteAllText(
                Path.Combine(root, "index.html"),
                "<!doctype html><html><head><title>PFOH</title></head><body></body></html>");
            File.WriteAllText(Path.Combine(root, "assets", "index-BeRASr9J.js"), "console.log('map');");
            base.ConfigureWebHost(builder);
            builder.UseWebRoot(root);
        }
    }
}
