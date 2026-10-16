using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class PublicFlagMapSubmitterTests : IClassFixture<PublicFlagMapSubmitterTests.Factory>
{
    private const string CanaryFirst = "Canaryfn";
    private const string CanaryLast = "Canaryln";
    private const string CanaryEmail = "canary-email@secret.test";
    private const string CanaryPhone = "555-0199-CANARY";
    private const string CanaryOid = "canary-oid";
    private const string CanaryStreet = "1 Canary Lane";
    private const string ClaimFirst = "Casey";
    private const string ClaimLast = "Claimer";

    private readonly Factory factory;

    public PublicFlagMapSubmitterTests(Factory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        if (db.FlagGrids.Any())
        {
            return;
        }

        var submitter = Sponsor(CanaryFirst, CanaryLast);
        var donor = Sponsor("Donoronly", "Patrononly");
        var accent = Sponsor("José", "García");
        var dormant = Sponsor("Dormant", "Knownname");
        var wide = Sponsor("Wide", "Net");
        db.Sponsors.AddRange(submitter, donor, accent, dormant, wide);
        db.SaveChanges();

        var visibleGrid = Grid("C-01");
        var claimedGrid = Grid("C-02");
        var accentGrid = Grid("C-03");
        var dormantGrid = Grid("C-04");
        var bulkGrids = Enumerable.Range(1, 11).Select(index => Grid($"W-{index:00}")).ToList();
        db.FlagGrids.AddRange(new[] { visibleGrid, claimedGrid, accentGrid, dormantGrid }.Concat(bulkGrids));
        db.SaveChanges();

        var visible = Honoree("Visible", "Hero", visibleGrid, submitter, active: true);
        var claimed = Honoree("Claimed", "Flag", claimedGrid, donor, active: true);
        var accented = Honoree("Accented", "Hero", accentGrid, accent, active: true);
        var sleeping = Honoree("Sleeping", "Record", dormantGrid, dormant, active: false);
        var bulk = bulkGrids.Select((grid, index) => Honoree($"Bulk{index}", "Row", grid, wide, active: true)).ToList();
        db.Honorees.AddRange(new[] { visible, claimed, accented, sleeping }.Concat(bulk));
        db.SaveChanges();

        db.FlagClaims.Add(new FlagClaim
        {
            HonoreeId = claimed.Id,
            FlagGridId = claimedGrid.Id,
            ExternalUserObjectId = CanaryOid,
            ExternalUserEmail = CanaryEmail,
            ExternalUserName = $"{ClaimFirst} {ClaimLast}",
            ClaimStatus = "Claimed"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Submitter_and_claimer_names_never_appear_in_a_response()
    {
        var matched = await Send(factory, "198.51.100.10", CanaryFirst, CanaryLast);
        var claimed = await Send(factory, "198.51.100.11", ClaimFirst, ClaimLast);
        var index = await factory.CreateClient().GetAsync("/api/public/flag-map/search-index");
        var section = await factory.CreateClient().GetAsync("/api/public/flag-map/sections/C");
        var bodies = new[]
        {
            await matched.Content.ReadAsStringAsync(),
            await claimed.Content.ReadAsStringAsync(),
            await index.Content.ReadAsStringAsync(),
            await section.Content.ReadAsStringAsync()
        };

        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        Assert.Contains("Visible Hero", bodies[0], StringComparison.Ordinal);
        Assert.Contains("Claimed Flag", bodies[1], StringComparison.Ordinal);
        foreach (var body in bodies)
        {
            AssertNoCanary(body);
        }

        Assert.False(matched.Headers.Contains("ETag"));
        Assert.False(matched.Headers.CacheControl?.ToString().Length > 0);
        Assert.DoesNotContain("Canary", string.Join('\n', factory.Logs), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Logs, line => line.Contains("outcome ok", StringComparison.Ordinal) && line.Contains("198.51.100.10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Short_inputs_are_rejected_without_echoing_the_name()
    {
        var response = await Send(factory, "198.51.100.12", "C", ClaimLast);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(ClaimLast, body, StringComparison.Ordinal);
        AssertNoCanary(body);
    }

    [Fact]
    public async Task Contains_style_partial_matches_fail_and_prefixes_match()
    {
        var contains = await Send(factory, "198.51.100.13", "nary", CanaryLast);
        var lastPartial = await Send(factory, "198.51.100.14", CanaryFirst, "Canar");
        var prefix = await Send(factory, "198.51.100.15", "Can", CanaryLast);
        var accent = await Send(factory, "198.51.100.16", "jose", "garcia");
        var accentContains = await Send(factory, "198.51.100.17", "ose", "García");

        Assert.Equal("No flags found", Message(await contains.Content.ReadAsStringAsync()));
        Assert.Equal("No flags found", Message(await lastPartial.Content.ReadAsStringAsync()));
        Assert.Contains("Visible Hero", await prefix.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Accented Hero", await accent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("No flags found", Message(await accentContains.Content.ReadAsStringAsync()));
        AssertNoCanary(await prefix.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_match_is_the_same_for_a_known_and_an_unknown_name()
    {
        var known = await Send(factory, "198.51.100.18", "Dormant", "Knownname");
        var unknown = await Send(factory, "198.51.100.19", "Nobody", "Zzzzzzz");
        var knownBody = await known.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        Assert.Equal(knownBody, unknownBody);
        Assert.Equal("No flags found", Message(knownBody));
        Assert.DoesNotContain("Sleeping", knownBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Dormant", knownBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Nobody", unknownBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_ten_matches_ask_for_a_refinement_and_return_no_list()
    {
        var response = await Send(factory, "198.51.100.20", "Wide", "Net");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("refine").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("flags", out _));
        Assert.DoesNotContain("Bulk", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Wide", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Net", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exempt_ip_is_still_limited_on_the_submitter_lookup()
    {
        const string exempt = "203.0.113.77";
        for (var i = 0; i < 10; i++)
        {
            using var response = await Send(factory, exempt, "Nobody", "Zzzzzzz");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await Send(factory, exempt, "Nobody", "Zzzzzzz");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var map = await SendPath(factory, exempt, "/api/public/flag-map/sections");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, map.StatusCode);
    }

    [Fact]
    public void Accent_and_prefix_rules_do_not_use_contains()
    {
        var first = PublicFlagMap.NormalizeName("José");
        var last = PublicFlagMap.NormalizeName("García");
        Assert.True(PublicFlagMap.NameMatches("José", "García", first, last));
        Assert.True(PublicFlagMap.NameMatches("José", "García", "jos", last));
        Assert.False(PublicFlagMap.NameMatches("José", "García", "ose", last));
        Assert.False(PublicFlagMap.NameMatches("José", "García", first, "garc"));
    }

    private static string Message(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("message").GetString() ?? "";
    }

    private static void AssertNoCanary(string body)
    {
        Assert.DoesNotContain(CanaryFirst, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryLast, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryEmail, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryPhone, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryOid, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryStreet, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClaimFirst, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClaimLast, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Donoronly", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Patrononly", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submitter", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claimer", body, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<HttpResponseMessage> Send(Factory factory, string ip, string first, string last)
    {
        var path = $"/api/public/flag-map/by-submitter?first={Uri.EscapeDataString(first)}&last={Uri.EscapeDataString(last)}";
        return SendPath(factory, ip, path);
    }

    private static Task<HttpResponseMessage> SendPath(Factory factory, string ip, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return factory.CreateClient().SendAsync(request);
    }

    private static Sponsor Sponsor(string first, string last) => new()
    {
        FirstName = first,
        LastName = last,
        EmailAddress = CanaryEmail,
        PhoneNumber = CanaryPhone,
        StreetAddress = CanaryStreet,
        IsActive = true,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = DateTime.UtcNow,
        ModifiedDate = DateTime.UtcNow
    };

    private static FlagGrid Grid(string name) => new()
    {
        FlagGridName = name,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = DateTime.UtcNow,
        ModifiedDate = DateTime.UtcNow
    };

    private static Honoree Honoree(string first, string last, FlagGrid grid, Sponsor sponsor, bool active) => new()
    {
        FirstName = first,
        LastName = last,
        IsActive = active,
        FlagGridId = grid.Id,
        SponsorId = sponsor.Id,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = DateTime.UtcNow,
        ModifiedDate = DateTime.UtcNow
    };

    public sealed class Factory : PfohApiFactory
    {
        public List<string> Logs { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PublicFlagMap:UnlimitedIps:0"] = "203.0.113.77"
                });
            });
            builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(Logs)));
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class ListLoggerProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, logs);
        public void Dispose() { }
    }

    private sealed class ListLogger(string category, List<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!category.Contains("PublicFlagMapController", StringComparison.Ordinal))
            {
                return;
            }

            lock (logs)
            {
                logs.Add(formatter(state, exception));
            }
        }
    }
}
