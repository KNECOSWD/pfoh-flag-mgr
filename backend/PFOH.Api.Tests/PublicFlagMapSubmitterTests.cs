using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Text;
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
        var quotaNames = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India" };
        var quota = quotaNames.Select(name => Sponsor("Quotaaa", name)).ToList();
        db.Sponsors.AddRange(new[] { submitter, donor, accent, dormant, wide }.Concat(quota));
        db.SaveChanges();

        var visibleGrid = Grid("C-01");
        var claimedGrid = Grid("C-02");
        var accentGrid = Grid("C-03");
        var dormantGrid = Grid("C-04");
        var bulkGrids = Enumerable.Range(1, 11).Select(index => Grid($"W-{index:00}")).ToList();
        var quotaGrids = quotaNames.Select((name, index) => Grid($"QA-{index + 1:00}")).ToList();
        db.FlagGrids.AddRange(new[] { visibleGrid, claimedGrid, accentGrid, dormantGrid }.Concat(bulkGrids).Concat(quotaGrids));
        db.SaveChanges();

        var visible = Honoree("Visible", "Hero", visibleGrid, submitter, active: true);
        var claimed = Honoree("Claimed", "Flag", claimedGrid, donor, active: true);
        var accented = Honoree("Accented", "Hero", accentGrid, accent, active: true);
        var sleeping = Honoree("Sleeping", "Record", dormantGrid, dormant, active: false);
        var bulk = bulkGrids.Select((grid, index) => Honoree($"Bulk{index}", "Row", grid, wide, active: true)).ToList();
        var listed = quotaGrids.Select((grid, index) => Honoree("Listed", quotaNames[index], grid, quota[index], active: true)).ToList();
        db.Honorees.AddRange(new[] { visible, claimed, accented, sleeping }.Concat(bulk).Concat(listed));
        db.SaveChanges();

        db.FlagClaims.Add(new FlagClaim
        {
            HonoreeId = claimed.Id,
            FlagGridId = claimedGrid.Id,
            ExternalUserObjectId = CanaryOid,
            ExternalUserEmail = CanaryEmail,
            ExternalUserName = $"{CanaryEmail} {ClaimFirst} user_42 {ClaimLast}",
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

        AssertNoStore(matched);
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
        AssertNoStore(response);
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
        AssertNoStore(response);
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
        AssertNoStore(limited);

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
        Assert.Equal(("Casey", "Claimer"), PublicFlagMap.SplitDisplayName($"Casey {CanaryEmail} user_42 Claimer"));
        Assert.Equal(("", ""), PublicFlagMap.SplitDisplayName(CanaryEmail));
        Assert.False(PublicFlagMap.NameMatches("user_name", "Claimer", "user", "claimer"));
        Assert.False(PublicFlagMap.IsPrivateNameToken("Mary-Anne"));
        Assert.True(PublicFlagMap.IsPrivateNameToken("jane.doe"));
    }

    [Fact]
    public async Task An_email_token_search_matches_the_unknown_name_response()
    {
        var email = await Send(factory, "198.51.100.51", CanaryEmail, "Claimer");
        var handle = await Send(factory, "198.51.100.52", "user_name", "Claimer");
        var digits = await Send(factory, "198.51.100.53", CanaryFirst, "Canaryln2");
        var unknown = await Send(factory, "198.51.100.54", "Nobody", "Zzzzzzz");
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, email.StatusCode);
        Assert.Equal(unknownBody, await email.Content.ReadAsStringAsync());
        Assert.Equal(unknownBody, await handle.Content.ReadAsStringAsync());
        Assert.Equal(unknownBody, await digits.Content.ReadAsStringAsync());
        Assert.Equal("No flags found", Message(unknownBody));
        AssertNoStore(email);
        AssertNoStore(unknown);
        AssertNoCanary(unknownBody);
    }

    [Fact]
    public async Task Canary_names_never_appear_in_any_log()
    {
        factory.Logs.Clear();
        using var posted = await Send(factory, "198.51.100.61", CanaryFirst, CanaryLast);
        using var queried = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/public/flag-map/by-submitter?first={Uri.EscapeDataString(CanaryEmail)}&last={Uri.EscapeDataString(CanaryLast)}");
        queried.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.62");
        using var got = await factory.CreateClient().SendAsync(queried);

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, got.StatusCode);
        AssertNoStore(posted);
        AssertNoStore(got);

        var logs = string.Join('\n', factory.Logs);
        Assert.DoesNotContain(CanaryFirst, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryLast, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryEmail, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryPhone, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryOid, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryStreet, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClaimFirst, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClaimLast, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("first=", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("last=", logs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Logs, line => line.Contains("outcome ok", StringComparison.Ordinal) && line.Contains("198.51.100.61", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Submitter_suggestions_show_the_display_name_and_no_account_details()
    {
        factory.Logs.Clear();
        using var suggestions = await SendSuggestions(factory, "198.51.100.80", CanaryFirst);
        var body = await suggestions.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, suggestions.StatusCode);
        using var json = JsonDocument.Parse(body);
        var rows = json.RootElement.GetProperty("suggestions").EnumerateArray().ToList();
        Assert.Contains(rows, row => row.GetProperty("label").GetString() == $"All flags submitted by {CanaryFirst} {CanaryLast}");
        var token = rows.Single(row => row.GetProperty("label").GetString()!.Contains(CanaryFirst, StringComparison.Ordinal)).GetProperty("token").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", token);
        Assert.DoesNotContain(CanaryFirst, token, StringComparison.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            Assert.Equal(2, row.EnumerateObject().Count());
        }

        AssertNoAccountDetails(body);
        AssertNoStore(suggestions);
        var logs = string.Join('\n', factory.Logs);
        Assert.DoesNotContain(CanaryFirst, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryLast, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryEmail, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, logs, StringComparison.Ordinal);

        using var flags = await SendToken(factory, "198.51.100.81", token);
        var flagBody = await flags.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, flags.StatusCode);
        Assert.Contains("Visible Hero", flagBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Claimed Flag", flagBody, StringComparison.Ordinal);
        AssertNoAccountDetails(flagBody);
        AssertNoStore(flags);
        Assert.DoesNotContain(CanaryFirst, string.Join('\n', factory.Logs), StringComparison.OrdinalIgnoreCase);

        using var claimer = await SendSuggestions(factory, "198.51.100.82", ClaimFirst);
        var claimerBody = await claimer.Content.ReadAsStringAsync();
        Assert.Contains($"All flags submitted by {ClaimFirst} {ClaimLast}", claimerBody, StringComparison.Ordinal);
        AssertNoAccountDetails(claimerBody);

        using var wide = await SendSuggestions(factory, "198.51.100.83", "Wide");
        using var wideJson = JsonDocument.Parse(await wide.Content.ReadAsStringAsync());
        var wideToken = wideJson.RootElement.GetProperty("suggestions").EnumerateArray().Single().GetProperty("token").GetString();
        using var wideFlags = await SendToken(factory, "198.51.100.84", wideToken!);
        using var wideFlagJson = JsonDocument.Parse(await wideFlags.Content.ReadAsStringAsync());
        Assert.Equal(11, wideFlagJson.RootElement.GetProperty("flags").GetArrayLength());
        AssertNoStore(wideFlags);
    }

    [Fact]
    public async Task Submitter_suggestions_require_three_letters_and_cap_the_list()
    {
        using var shortQuery = await SendSuggestions(factory, "198.51.100.85", "Ca");
        var shortBody = await shortQuery.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, shortQuery.StatusCode);
        Assert.DoesNotContain("Ca", shortBody, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryFirst, shortBody, StringComparison.Ordinal);
        AssertNoAccountDetails(shortBody);
        AssertNoStore(shortQuery);

        using var buried = await SendSuggestions(factory, "198.51.100.86", "nary");
        using var buriedJson = JsonDocument.Parse(await buried.Content.ReadAsStringAsync());
        Assert.Equal(0, buriedJson.RootElement.GetProperty("suggestions").GetArrayLength());

        using var email = await SendSuggestions(factory, "198.51.100.87", CanaryEmail);
        var emailBody = await email.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, email.StatusCode);
        Assert.DoesNotContain(CanaryEmail, emailBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", emailBody, StringComparison.Ordinal);

        using var capped = await SendSuggestions(factory, "198.51.100.88", "Quotaaa");
        var cappedBody = await capped.Content.ReadAsStringAsync();
        using var cappedJson = JsonDocument.Parse(cappedBody);
        Assert.Equal(PublicFlagMap.SubmitterSuggestionLimit, cappedJson.RootElement.GetProperty("suggestions").GetArrayLength());
        Assert.DoesNotContain("India", cappedBody, StringComparison.Ordinal);
        AssertNoAccountDetails(cappedBody);
        AssertNoStore(capped);

        using var missing = await SendToken(factory, "198.51.100.89", "not-a-real-token");
        var missingBody = await missing.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain("not-a-real-token", missingBody, StringComparison.Ordinal);
        AssertNoStore(missing);
    }

    [Fact]
    public async Task Submitter_suggestions_stay_limited_for_an_exempt_address_and_are_not_logged()
    {
        factory.Logs.Clear();
        const string exempt = "203.0.113.77";
        for (var i = 0; i < 20; i++)
        {
            using var response = await SendSuggestions(factory, exempt, "Nobodyzz");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await SendSuggestions(factory, exempt, CanaryFirst);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var limitedBody = await limited.Content.ReadAsStringAsync();
        Assert.DoesNotContain(CanaryFirst, limitedBody, StringComparison.OrdinalIgnoreCase);
        AssertNoStore(limited);

        using var queried = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/public/flag-map/submitter-suggestions?query={Uri.EscapeDataString(CanaryEmail)}");
        queried.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.90");
        using var got = await factory.CreateClient().SendAsync(queried);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, got.StatusCode);
        AssertNoStore(got);

        var logs = string.Join('\n', factory.Logs);
        Assert.DoesNotContain(CanaryFirst, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryEmail, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query=", logs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Logs, line => line.Contains("outcome", StringComparison.Ordinal) && line.Contains(exempt, StringComparison.Ordinal));

        using var map = await SendPath(factory, exempt, "/api/public/flag-map/sections");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, map.StatusCode);
    }

    [Fact]
    public async Task Submitter_suggestions_answer_from_the_cached_name_index()
    {
        var tokens = new List<PublicFlagMap.SubmitterIdentity>(8000);
        for (var i = 0; i < 8000; i++)
        {
            tokens.Add(new PublicFlagMap.SubmitterIdentity($"first{i:0000}", $"last{i:0000}", $"First{i:0000} Last{i:0000}"));
        }

        tokens.Add(new PublicFlagMap.SubmitterIdentity("matthew", "sullivan", "Matthew Sullivan"));
        var index = SubmitterTokenIndex.Build(tokens);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var matched = index.Match("sul", PublicFlagMap.SubmitterSuggestionLimit);
        watch.Stop();
        Assert.Contains(matched, name => name.Display == "Matthew Sullivan");
        Assert.True(watch.ElapsedMilliseconds < 30, $"name token index took {watch.Elapsed.TotalMilliseconds:0.00} ms");

        using var warm = await SendSuggestions(factory, "198.51.100.210", CanaryFirst);
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        var http = System.Diagnostics.Stopwatch.StartNew();
        using var timed = await SendSuggestions(factory, "198.51.100.211", CanaryFirst);
        http.Stop();
        Assert.Equal(HttpStatusCode.OK, timed.StatusCode);
        var body = await timed.Content.ReadAsStringAsync();
        Assert.Contains($"All flags submitted by {CanaryFirst} {CanaryLast}", body, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryEmail, body, StringComparison.OrdinalIgnoreCase);
        AssertNoStore(timed);
        Assert.True(http.ElapsedMilliseconds < 300, $"cached submitter suggestions took {http.Elapsed.TotalMilliseconds:0.00} ms");
        Console.WriteLine($"SUBMITTER_SUGGESTION_INDEX_MS={watch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"SUBMITTER_SUGGESTION_HTTP_MS={http.Elapsed.TotalMilliseconds:0.00}");
    }

    private static void AssertNoAccountDetails(string body)
    {
        Assert.DoesNotContain(CanaryEmail, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryPhone, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryOid, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CanaryStreet, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sponsorId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("externalUser", body, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<HttpResponseMessage> SendSuggestions(Factory factory, string ip, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/flag-map/submitter-suggestions")
        {
            Content = JsonContent.Create(new { query })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return factory.CreateClient().SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendToken(Factory factory, string ip, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/flag-map/submitter-flags")
        {
            Content = JsonContent.Create(new { token })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return factory.CreateClient().SendAsync(request);
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

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains("ETag"));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static Task<HttpResponseMessage> Send(Factory factory, string ip, string first, string last)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/flag-map/by-submitter")
        {
            Content = JsonContent.Create(new { first, last })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return factory.CreateClient().SendAsync(request);
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
                    ["PublicFlagMap:UnlimitedIps:0"] = "203.0.113.77",
                    ["Logging:LogLevel:Default"] = "Information",
                    ["Logging:LogLevel:Microsoft"] = "Information",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
                    ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information"
                });
            });
            builder.ConfigureLogging(logging =>
            {
                logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
                logging.AddProvider(new ListLoggerProvider(Logs));
            });
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
            var text = new StringBuilder();
            text.Append(category);
            text.Append(' ');
            text.Append(formatter(state, exception));
            if (state is not string && state is IEnumerable items)
            {
                foreach (var item in items)
                {
                    text.Append(' ');
                    text.Append(item);
                }
            }

            if (exception is not null)
            {
                text.Append(' ');
                text.Append(exception);
            }

            lock (logs)
            {
                logs.Add(text.ToString());
            }
        }
    }
}
