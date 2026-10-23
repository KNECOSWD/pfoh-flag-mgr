using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using Xunit;

namespace PFOH.Api.Tests;

public class DuplicateMergeFieldApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Groups_return_the_fields_the_review_screen_shows()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            (await db.Honorees.SingleAsync(honoree => honoree.Id == 1)).CreatedDate =
                new DateTime(2019, 6, 1, 15, 30, 0, DateTimeKind.Utc);
            (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).CreatedDate =
                new DateTime(2024, 11, 3, 8, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();

        var listed = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/duplicates/groups"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var groups = await listed.Content.ReadFromJsonAsync<List<GroupBody>>(Json);
        var group = Assert.Single(groups!);
        var left = Assert.Single(group.Honorees, honoree => honoree.Id == 1);
        Assert.Equal(2019, left.CreatedDate.Year);
        Assert.Equal(6, left.CreatedDate.Month);
        Assert.Equal(1, left.CreatedDate.Day);
        Assert.Equal(15, left.CreatedDate.Hour);
        Assert.Equal(30, left.CreatedDate.Minute);
        Assert.Equal("Jr.", left.Suffix);
        Assert.Equal("Photo bio", left.Description);
        Assert.Equal("United States Army", left.ServiceBranchName);
        Assert.Equal("1942 - 1945", left.ServiceYears);
        Assert.Equal("Bronze Star", left.Awards);
        Assert.Equal("john-1.jpg", left.PhotoFileName);
        Assert.Equal("/api/honorees/1/photo", left.PhotoUrl);
        Assert.Contains("John", left.Name);
        Assert.Contains("Jr.", left.Name);

        var right = Assert.Single(group.Honorees, honoree => honoree.Id == 2);
        Assert.Equal(2024, right.CreatedDate.Year);
        Assert.Equal(11, right.CreatedDate.Month);
        Assert.Equal(3, right.CreatedDate.Day);
        Assert.Equal("Sr.", right.Suffix);
        Assert.Equal("", right.PhotoFileName);
        Assert.Null(right.PhotoUrl);
    }

    [Fact]
    public async Task Custom_and_chosen_values_are_saved_and_claimants_carry_over()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var fields = SourceFields(survivor: 2, photoFrom: 1, awardsFrom: 2);
        fields.Remove("firstName");
        fields.Remove("middleName");
        fields.Remove("description");
        fields.Remove("serviceBranch");
        fields.Remove("serviceYears");
        fields.Remove("kia");
        fields.Remove("email");
        fields.Remove("phone");
        fields.Remove("sponsor");

        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string>
            {
                ["firstName"] = "  Patricia\nAnn\u0000  ",
                ["middleName"] = "   ",
                ["description"] = "Line one\r\nLine two\u0000",
                ["serviceBranch"] = "united states army",
                ["serviceYears"] = "1960 - 1964",
                ["kia"] = " Yes ",
                ["email"] = "  pat@example.com ",
                ["phone"] = " 214-555-0199 ",
                ["sponsor"] = "Ada Lovelace"
            }
        }));
        var body = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, body);
        var payload = JsonSerializer.Deserialize<MergeBody>(body, Json);
        Assert.NotNull(payload);
        Assert.Equal(2, payload.SurvivorId);
        Assert.Equal(new[] { "ada@example.com", "bea@example.com" }, payload.Claimants.Select(claimant => claimant.Email).OrderBy(email => email).ToArray());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(2, await db.Honorees.CountAsync());
        var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
        var other = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
        Assert.Equal("Patricia Ann", survivor.FirstName);
        Assert.Null(survivor.MiddleName);
        Assert.Equal("Line one\nLine two", survivor.Description);
        Assert.Equal(1, survivor.ServiceBranchId);
        Assert.Equal(1, survivor.ServiceBranchCategoryId);
        Assert.Equal(1960, survivor.StartYear);
        Assert.Equal(1964, survivor.EndYear);
        Assert.Equal("1960 - 1964", survivor.DatesUserEntry);
        Assert.True(survivor.KIA);
        Assert.Equal("pat@example.com", survivor.EmailAddress);
        Assert.Equal("214-555-0199", survivor.PhoneNumber);
        Assert.Equal(7, survivor.SponsorId);
        Assert.Equal("john-1.jpg", survivor.PhotoFileName);
        Assert.Equal("Purple Heart", survivor.Awards);
        Assert.Equal("Smith", survivor.LastName);
        Assert.True(survivor.IsActive);
        Assert.False(other.IsActive);
        Assert.Null(other.FlagGridId);
        Assert.Null(other.DeletedDate);
        var released = await db.FlagGrids.SingleAsync(grid => grid.Id == 11);
        Assert.Null(released.HonoreeId);
        var kept = await db.FlagGrids.SingleAsync(grid => grid.Id == 12);
        Assert.Equal(2, kept.HonoreeId);

        var claimants = await db.HonoreeClaims
            .Where(claim => claim.HonoreeId == 2 && claim.ClaimStatus == "Claimed")
            .OrderBy(claim => claim.Email)
            .ToListAsync();
        Assert.Equal(new[] { "ada@example.com", "bea@example.com" }, claimants.Select(claim => claim.Email).ToArray());
    }

    [Fact]
    public async Task Unknown_fields_bad_ids_and_invalid_custom_values_are_rejected()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();

        await RejectAsync(client, "Unknown field 'salutation'.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2).Concat(new[] { KeyValuePair.Create("salutation", 2) }).ToDictionary(pair => pair.Key, pair => pair.Value)
        });

        await RejectAsync(client, "Unknown field 'rankTitle'.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2),
            values = new Dictionary<string, string> { ["rankTitle"] = "Capt" }
        });

        var badId = SourceFields(2, 1, 2);
        badId["firstName"] = 99;
        await RejectAsync(client, "Each kept value has to come from one of the confirmed records.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = badId
        });

        var fields = SourceFields(2, 1, 2);
        fields.Remove("firstName");
        await RejectAsync(client, "First name must be 200 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["firstName"] = new string('A', 201) }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("suffix");
        await RejectAsync(client, "Suffix must be 100 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["suffix"] = new string('I', 101) }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("phone");
        await RejectAsync(client, "Phone must be 50 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["phone"] = new string('5', 51) }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("email");
        await RejectAsync(client, "Email is not a valid email address.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["email"] = "not-an-email" }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("description");
        await RejectAsync(client, "Honoree description is required.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["description"] = "   " }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("serviceYears");
        await RejectAsync(client, "Service years must be 500 characters or fewer.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["serviceYears"] = new string('1', 501) }
        });

        await RejectAsync(client, "A typed photo value is not accepted.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2),
            values = new Dictionary<string, string> { ["photo"] = "typed.jpg" }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("serviceBranch");
        await RejectAsync(client, "Type a service branch that already exists", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["serviceBranch"] = "Not A Real Branch" }
        });

        fields = SourceFields(2, 1, 2);
        fields.Remove("kia");
        await RejectAsync(client, "Killed in action must be Yes or No.", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["kia"] = "Maybe" }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
        var other = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
        Assert.Equal("John", survivor.FirstName);
        Assert.Equal("Kept bio", survivor.Description);
        Assert.True(survivor.IsActive);
        Assert.True(other.IsActive);
        Assert.Equal(2, await db.Honorees.CountAsync(honoree => honoree.IsActive && honoree.DeletedDate == null));
    }

    [Fact]
    public async Task Inactive_and_out_of_group_records_are_rejected()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            db.Honorees.Add(Honoree(5, "Grace", "Hopper", "Other bio", "", null, now));
            var inactive = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
            inactive.IsActive = false;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var inactiveMerge = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.Conflict, inactiveMerge.StatusCode);
        Assert.Contains("inactive or deleted", await inactiveMerge.Content.ReadAsStringAsync());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var restored = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
            restored.IsActive = true;
            await db.SaveChangesAsync();
        }

        var outside = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 2, 5 },
            fields = SourceFields(2, 2, 2)
        }));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        var body = await outside.Content.ReadAsStringAsync();
        Assert.Contains("not in that group", body);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Kept bio", (await after.Honorees.SingleAsync(honoree => honoree.Id == 2)).Description);
        Assert.True((await after.Honorees.SingleAsync(honoree => honoree.Id == 5)).IsActive);
        Assert.Equal(3, await after.Honorees.CountAsync(honoree => honoree.IsActive && honoree.DeletedDate == null));
    }

    [Fact]
    public async Task Custom_value_with_inner_double_spaces_is_saved_as_the_preview_text()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var fields = SourceFields(2, 1, 2);
        fields.Remove("firstName");
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["firstName"] = "Patricia  Ann" }
        }));
        var body = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, body);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
        Assert.Equal("Patricia Ann", survivor.FirstName);
    }

    [Fact]
    public async Task Flag_grid_custom_values_follow_assignment_rules()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            var deleted = Grid(21, "DD-1", now);
            deleted.DeletedDate = now;
            deleted.DeletedBy = "seed";
            var reserved = Grid(22, "RR-1", now);
            reserved.Reserved = true;
            db.FlagGrids.Add(deleted);
            db.FlagGrids.Add(reserved);
            db.FlagGrids.Add(Grid(23, "II-1", now));
            db.FlagGrids.Add(Grid(24, "OO-1", now));
            var inactive = Honoree(8, "Inactive", "Holder", "Inactive bio", "", 23, now);
            inactive.IsActive = false;
            var outside = Honoree(9, "Active", "Holder", "Outside bio", "", 24, now);
            db.Honorees.Add(inactive);
            db.Honorees.Add(outside);
            await db.SaveChangesAsync();
            db.FlagGrids.Single(grid => grid.Id == 23).HonoreeId = 8;
            db.FlagGrids.Single(grid => grid.Id == 24).HonoreeId = 9;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var fields = SourceFields(2, 1, 2);
        fields.Remove("flagGrid");

        await RejectAsync(client, "deleted", GridValue(fields, "DD-1"));
        await RejectAsync(client, "reserved", GridValue(fields, "RR-1"));
        await RejectAsync(client, "not part of this merge", GridValue(fields, "II-1"));
        await RejectAsync(client, "not part of this merge", GridValue(fields, "OO-1"));

        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", GridValue(fields, "AA-1")));
        var body = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, body);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var survivor = await after.Honorees.SingleAsync(honoree => honoree.Id == 2);
        var other = await after.Honorees.SingleAsync(honoree => honoree.Id == 1);
        Assert.Equal(11, survivor.FlagGridId);
        Assert.True(survivor.IsActive);
        Assert.False(other.IsActive);
        Assert.Null(other.FlagGridId);
        Assert.Equal(2, (await after.FlagGrids.SingleAsync(grid => grid.Id == 11)).HonoreeId);
        Assert.Null((await after.FlagGrids.SingleAsync(grid => grid.Id == 12)).HonoreeId);
        Assert.Equal(8, (await after.FlagGrids.SingleAsync(grid => grid.Id == 23)).HonoreeId);
        Assert.Equal(9, (await after.FlagGrids.SingleAsync(grid => grid.Id == 24)).HonoreeId);
        Assert.Equal(23, (await after.Honorees.SingleAsync(honoree => honoree.Id == 8)).FlagGridId);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 8)).IsActive);
        Assert.Equal(24, (await after.Honorees.SingleAsync(honoree => honoree.Id == 9)).FlagGridId);
        Assert.True((await after.Honorees.SingleAsync(honoree => honoree.Id == 9)).IsActive);

        var movedClaims = await after.FlagClaims.Where(claim => claim.Id == 31 || claim.Id == 32).ToListAsync();
        Assert.Equal(2, movedClaims.Count);
        Assert.All(movedClaims, claim => Assert.Equal(2, claim.HonoreeId));
        var claimants = await after.HonoreeClaims
            .Where(claim => claim.HonoreeId == 2 && claim.ClaimStatus == "Claimed")
            .OrderBy(claim => claim.Email)
            .ToListAsync();
        Assert.Equal(new[] { "ada@example.com", "bea@example.com" }, claimants.Select(claim => claim.Email).ToArray());
    }

    [Fact]
    public async Task A_merge_with_a_field_left_unchosen_returns_400()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var fields = SourceFields(2, 1, 2);
        fields.Remove("nickname");

        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Choose a value for every field", body);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("John", (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).FirstName);
        Assert.True((await db.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.True((await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).IsActive);
    }

    [Fact]
    public async Task Explicit_survivor_that_disagrees_with_the_flag_grid_is_rejected()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var fields = SourceFields(2, 1, 2);
        fields["flagGrid"] = 2;

        var mismatch = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 1,
            duplicateIds = new[] { 1, 2 },
            fields
        }));
        var mismatchBody = await mismatch.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("flag grid you selected", mismatchBody);

        fields["flagGrid"] = 99;
        var outside = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields
        }));
        var outsideBody = await outside.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Contains("included records", outsideBody);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(2, await db.Honorees.CountAsync(honoree => honoree.IsActive && honoree.DeletedDate == null));
        Assert.Equal("Kept bio", (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).Description);
    }

    [Fact]
    public async Task Combined_and_edited_awards_use_the_edit_path_rules()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var right = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
            right.Awards = "Purple Heart\nBronze  Star";
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var fields = SourceFields(2, 1, 2);
        fields.Remove("awards");
        var combined = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            combineAwards = true
        }));
        var combinedBody = await combined.Content.ReadAsStringAsync();
        Assert.True(combined.StatusCode == HttpStatusCode.OK, combinedBody);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Bronze Star\nPurple Heart", (await after.Honorees.SingleAsync(honoree => honoree.Id == 2)).Awards);
    }

    [Fact]
    public async Task An_edited_awards_value_is_saved()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var fields = SourceFields(2, 1, 2);
        fields.Remove("awards");
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields,
            values = new Dictionary<string, string> { ["awards"] = "  Medal of Honor\r\nSilver Star  " }
        }));
        var body = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, body);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Medal of Honor\nSilver Star", (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).Awards);
    }

    [Fact]
    public async Task Card_preview_is_admin_only_returns_a_pdf_and_writes_nothing()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        int honorees;
        int claims;
        int changes;
        int prints;
        int reprints;
        int gridHolders;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            honorees = await db.Honorees.CountAsync();
            claims = await db.FlagClaims.CountAsync();
            changes = await db.HonoreeChangeRequests.CountAsync();
            prints = await db.CardPrintEvents.CountAsync();
            reprints = await db.ReprintDuplicateReviews.CountAsync();
            gridHolders = await db.FlagGrids.CountAsync(grid => grid.HonoreeId != null);
        }

        var pdfs = factory.Files.Pdfs.Count;
        var photos = factory.Files.Photos.Count;
        var deletedPhotos = factory.Files.DeletedPendingPhotos.Count;

        var missingFields = SourceFields(2, 1, 2);
        missingFields.Remove("nickname");
        var missing = new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = missingFields
        };
        var previewMissing = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge-preview/card", missing));
        var mergeMissing = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", missing));
        var previewMissingBody = await previewMissing.Content.ReadAsStringAsync();
        var mergeMissingBody = await mergeMissing.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, previewMissing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mergeMissing.StatusCode);
        Assert.Contains("Choose a value for every field", previewMissingBody);
        Assert.Equal(mergeMissingBody, previewMissingBody);

        var mismatch = new
        {
            survivorId = 1,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        };
        var previewMismatch = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge-preview/card", mismatch));
        var mergeMismatch = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", mismatch));
        Assert.Equal(HttpStatusCode.BadRequest, previewMismatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mergeMismatch.StatusCode);
        Assert.Contains("flag grid you selected", await previewMismatch.Content.ReadAsStringAsync());
        Assert.Contains("flag grid you selected", await mergeMismatch.Content.ReadAsStringAsync());

        var member = new HttpRequestMessage(HttpMethod.Post, "/api/admin/duplicates/merge-preview/card")
        {
            Content = JsonContent.Create(new
            {
                survivorId = 2,
                duplicateIds = new[] { 1, 2 },
                fields = SourceFields(2, 1, 2)
            })
        };
        member.Headers.Add(TestAuthHandler.OidHeader, "member-1");
        member.Headers.Add(TestAuthHandler.EmailHeader, "member@example.com");
        member.Headers.Add(TestAuthHandler.NameHeader, "Member");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(member)).StatusCode);

        var anonymous = new HttpRequestMessage(HttpMethod.Post, "/api/admin/duplicates/merge-preview/card")
        {
            Content = JsonContent.Create(new
            {
                survivorId = 2,
                duplicateIds = new[] { 1, 2 },
                fields = SourceFields(2, 1, 2)
            })
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymous)).StatusCode);

        var previewFields = SourceFields(2, 1, 2);
        previewFields.Remove("firstName");
        var previewBody = new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = previewFields,
            values = new Dictionary<string, string> { ["firstName"] = "Patricia  Ann" }
        };
        var preview = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge-preview/card", previewBody));
        var previewError = preview.StatusCode == HttpStatusCode.OK ? "" : await preview.Content.ReadAsStringAsync();
        Assert.True(preview.StatusCode == HttpStatusCode.OK, previewError);
        Assert.Equal("application/pdf", preview.Content.Headers.ContentType?.MediaType);
        var pdf = await preview.Content.ReadAsByteArrayAsync();
        Assert.True(pdf.Length > 4);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.Equal(honorees, await db.Honorees.CountAsync());
            Assert.Equal(claims, await db.FlagClaims.CountAsync());
            Assert.Equal(changes, await db.HonoreeChangeRequests.CountAsync());
            Assert.Equal(prints, await db.CardPrintEvents.CountAsync());
            Assert.Equal(reprints, await db.ReprintDuplicateReviews.CountAsync());
            Assert.Equal(gridHolders, await db.FlagGrids.CountAsync(grid => grid.HonoreeId != null));
            Assert.Equal("John", (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).FirstName);
            Assert.True((await db.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
            Assert.Equal(12, (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).FlagGridId);
        }

        Assert.Equal(pdfs, factory.Files.Pdfs.Count);
        Assert.Equal(photos, factory.Files.Photos.Count);
        Assert.Equal(deletedPhotos, factory.Files.DeletedPendingPhotos.Count);

        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", previewBody));
        var mergedBody = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, mergedBody);
        await using var saved = factory.Services.CreateAsyncScope();
        var savedDb = saved.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Patricia Ann", (await savedDb.Honorees.SingleAsync(honoree => honoree.Id == 2)).FirstName);
    }

    [Fact]
    public async Task Pending_review_reports_the_open_duplicate_group_count()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();

        var pending = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/review/pending"));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal("1", pending.Headers.GetValues("X-Open-Duplicate-Groups").Single());
        var groups = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/duplicates/groups"));
        var listed = await groups.Content.ReadFromJsonAsync<List<GroupBody>>(Json);
        Assert.Single(listed!);

        var dismissed = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/not-duplicates", new
        {
            leftId = 1,
            rightId = 2
        }));
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        var after = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/review/pending"));
        Assert.Equal("0", after.Headers.GetValues("X-Open-Duplicate-Groups").Single());
        var empty = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/duplicates/groups"));
        Assert.Empty((await empty.Content.ReadFromJsonAsync<List<GroupBody>>(Json))!);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.HonoreeNotDuplicatePairs.RemoveRange(db.HonoreeNotDuplicatePairs);
            var inactive = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
            inactive.IsActive = false;
            await db.SaveChangesAsync();
        }

        var inactiveCount = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/review/pending"));
        Assert.Equal("0", inactiveCount.Headers.GetValues("X-Open-Duplicate-Groups").Single());

        var member = new HttpRequestMessage(HttpMethod.Get, "/api/admin/review/pending");
        member.Headers.Add(TestAuthHandler.OidHeader, "member-1");
        member.Headers.Add(TestAuthHandler.EmailHeader, "member@example.com");
        member.Headers.Add(TestAuthHandler.NameHeader, "Member");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(member)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/review/pending")).StatusCode);
    }

    [Fact]
    public async Task A_merge_writes_one_audit_row_and_undo_restores_every_moved_value()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var now = DateTime.UtcNow;
            db.HonoreeClaims.Add(new HonoreeClaim
            {
                Id = 71,
                HonoreeId = 1,
                UserId = "person-a",
                ClaimedUtc = now,
                Email = "ada@example.com",
                DisplayName = "Ada",
                ClaimStatus = "Claimed",
                SourceFlagClaimId = 31
            });
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 81,
                FlagClaimId = 31,
                HonoreeId = 1,
                FlagGridId = 11,
                FirstName = "John",
                LastName = "Smith",
                RequestStatus = "Approved",
                Awards = "Bronze Star",
                PhotoFileName = "john-1.jpg"
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 1)
        }));
        var mergedBody = await merged.Content.ReadAsStringAsync();
        Assert.True(merged.StatusCode == HttpStatusCode.OK, mergedBody);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var audit = Assert.Single(await db.HonoreeMergeAudits.ToListAsync());
            Assert.Equal(2, audit.SurvivorId);
            Assert.Equal("admin@example.com", audit.MergedBy);
            Assert.NotEqual(default, audit.MergedUtc);
            Assert.Contains("Purple Heart", audit.BeforeState);
            Assert.Contains("john-1.jpg", audit.BeforeState);
            Assert.Contains("\"id\":81", audit.BeforeState);
            Assert.Equal("John", (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).FirstName);
            Assert.Equal("john-1.jpg", (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).PhotoFileName);
            Assert.Equal("Bronze Star", (await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).Awards);
            Assert.False((await db.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
            Assert.Null((await db.Honorees.SingleAsync(honoree => honoree.Id == 1)).FlagGridId);
            Assert.Null((await db.FlagGrids.SingleAsync(grid => grid.Id == 11)).HonoreeId);
            Assert.Equal(2, (await db.FlagClaims.SingleAsync(claim => claim.Id == 31)).HonoreeId);
            var change = await db.HonoreeChangeRequests.SingleAsync(item => item.Id == 81);
            Assert.Equal(1, change.HonoreeId);
            Assert.Equal(11, change.FlagGridId);
            Assert.Contains("\"honorees\"", audit.AfterState);
            Assert.NotEqual("{}", audit.AfterState);
        }

        var history = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/duplicates/merges"));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var listed = await history.Content.ReadFromJsonAsync<List<HistoryBody>>(Json);
        var item = Assert.Single(listed!);
        Assert.Equal("John Q Smith Sr.", item.SurvivorName);
        Assert.Equal("John Q Smith Jr.", Assert.Single(item.Absorbed).Name);
        Assert.Equal("admin@example.com", item.MergedBy);
        Assert.Null(item.UndoneUtc);

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{item.Id}/undo"));
        var undoneBody = await undone.Content.ReadAsStringAsync();
        Assert.True(undone.StatusCode == HttpStatusCode.OK, undoneBody);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
            var absorbed = await db.Honorees.SingleAsync(honoree => honoree.Id == 1);
            Assert.Equal("", survivor.PhotoFileName);
            Assert.Equal("Purple Heart", survivor.Awards);
            Assert.Equal("Kept bio", survivor.Description);
            Assert.Equal(12, survivor.FlagGridId);
            Assert.True(survivor.IsActive);
            Assert.True(absorbed.IsActive);
            Assert.Equal(11, absorbed.FlagGridId);
            Assert.Equal("john-1.jpg", absorbed.PhotoFileName);
            Assert.Equal("Bronze Star", absorbed.Awards);
            Assert.Equal("Photo bio", absorbed.Description);
            Assert.Equal(1, (await db.FlagGrids.SingleAsync(grid => grid.Id == 11)).HonoreeId);
            Assert.Equal(2, (await db.FlagGrids.SingleAsync(grid => grid.Id == 12)).HonoreeId);
            var claim = await db.FlagClaims.SingleAsync(item => item.Id == 31);
            Assert.Equal(1, claim.HonoreeId);
            Assert.Equal(11, claim.FlagGridId);
            var ownership = await db.HonoreeClaims.SingleAsync(item => item.Id == 71);
            Assert.Equal(1, ownership.HonoreeId);
            Assert.Equal("Claimed", ownership.ClaimStatus);
            Assert.Equal("ada@example.com", ownership.Email);
            Assert.DoesNotContain(await db.HonoreeClaims.ToListAsync(), item => item.HonoreeId == 2 && item.UserId == "person-a");
            var change = await db.HonoreeChangeRequests.SingleAsync(item => item.Id == 81);
            Assert.Equal(1, change.HonoreeId);
            Assert.Equal(11, change.FlagGridId);
            var audit = await db.HonoreeMergeAudits.SingleAsync();
            Assert.Equal("admin@example.com", audit.UndoneBy);
            Assert.NotNull(audit.UndoneUtc);
        }

        var twice = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{item.Id}/undo"));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("already undone", await twice.Content.ReadAsStringAsync());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.True((await db.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
            Assert.True((await db.Honorees.SingleAsync(honoree => honoree.Id == 2)).IsActive);
        }
    }

    [Fact]
    public async Task Undo_is_blocked_when_the_survivor_was_edited_after_the_merge()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var survivor = await db.Honorees.SingleAsync(honoree => honoree.Id == 2);
            survivor.FirstName = "Edited";
            survivor.ModifiedDate = DateTime.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync();
        }

        int auditId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            auditId = (await db.HonoreeMergeAudits.SingleAsync(row => row.MergedBy == "admin@example.com")).Id;
        }

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{auditId}/undo"));
        Assert.Equal(HttpStatusCode.Conflict, undone.StatusCode);
        Assert.Contains("edited after the merge", await undone.Content.ReadAsStringAsync());

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("Edited", (await after.Honorees.SingleAsync(honoree => honoree.Id == 2)).FirstName);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync()).UndoneUtc);
    }

    [Fact]
    public async Task Undo_is_blocked_when_a_released_grid_was_reassigned()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.Honorees.Add(Honoree(3, "Other", "Person", "Later record", "", null, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var grid = await db.FlagGrids.SingleAsync(item => item.Id == 11);
            Assert.Null(grid.HonoreeId);
            var other = await db.Honorees.SingleAsync(honoree => honoree.Id == 3);
            other.FlagGridId = 11;
            other.ModifiedBy = "later";
            other.ModifiedDate = DateTime.UtcNow;
            grid.HonoreeId = 3;
            grid.ModifiedBy = "later";
            grid.ModifiedDate = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        int auditId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            auditId = (await db.HonoreeMergeAudits.SingleAsync()).Id;
        }

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{auditId}/undo"));
        var text = await undone.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, undone.StatusCode);
        Assert.Contains("AA-1", text);
        Assert.Contains("assigned after the merge", text);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(3, (await after.FlagGrids.SingleAsync(grid => grid.Id == 11)).HonoreeId);
        Assert.Equal(11, (await after.Honorees.SingleAsync(honoree => honoree.Id == 3)).FlagGridId);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).FlagGridId);
        Assert.Equal(12, (await after.Honorees.SingleAsync(honoree => honoree.Id == 2)).FlagGridId);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync()).UndoneUtc);
    }

    [Fact]
    public async Task Undo_is_blocked_when_a_change_request_was_edited_after_the_merge()
    {
        await ResetAsync();
        await SeedPairAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 81,
                FlagClaimId = 31,
                HonoreeId = 1,
                FlagGridId = 11,
                FirstName = "John",
                LastName = "Smith",
                RequestStatus = "Approved"
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 1)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var change = await db.HonoreeChangeRequests.SingleAsync(item => item.Id == 81);
            change.HonoreeId = 2;
            change.FlagGridId = 12;
            await db.SaveChangesAsync();
        }

        int auditId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            auditId = (await db.HonoreeMergeAudits.SingleAsync()).Id;
        }

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{auditId}/undo"));
        var text = await undone.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, undone.StatusCode);
        Assert.Contains("change request 81", text);
        Assert.Contains("edited after the merge", text);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var kept = await after.HonoreeChangeRequests.SingleAsync(item => item.Id == 81);
        Assert.Equal(2, kept.HonoreeId);
        Assert.Equal(12, kept.FlagGridId);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync()).UndoneUtc);
    }

    [Fact]
    public async Task Undo_is_blocked_when_a_claim_was_edited_after_the_merge()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var claim = await db.FlagClaims.SingleAsync(item => item.Id == 31);
            claim.ClaimStatus = "Cancelled";
            claim.ExternalUserEmail = "changed@example.com";
            await db.SaveChangesAsync();
        }

        int auditId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            auditId = (await db.HonoreeMergeAudits.SingleAsync()).Id;
        }

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{auditId}/undo"));
        var text = await undone.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, undone.StatusCode);
        Assert.Contains("flag claim 31", text);
        Assert.Contains("edited after the merge", text);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        var kept = await after.FlagClaims.SingleAsync(item => item.Id == 31);
        Assert.Equal("Cancelled", kept.ClaimStatus);
        Assert.Equal("changed@example.com", kept.ExternalUserEmail);
        Assert.Equal(2, kept.HonoreeId);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync()).UndoneUtc);
    }

    [Fact]
    public async Task Undo_is_blocked_when_a_freed_grid_was_claimed_after_the_merge()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            Assert.Null((await db.FlagGrids.SingleAsync(grid => grid.Id == 11)).HonoreeId);
            db.FlagClaims.Add(new FlagClaim
            {
                Id = 41,
                FlagGridId = 11,
                HonoreeId = null,
                ExternalUserObjectId = "person-c",
                ExternalUserEmail = "cara@example.com",
                ExternalUserName = "Cara",
                ClaimStatus = "Claimed",
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        int auditId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            auditId = (await db.HonoreeMergeAudits.SingleAsync()).Id;
        }

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{auditId}/undo"));
        var text = await undone.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, undone.StatusCode);
        Assert.Contains("flag claim 41", text);
        Assert.Contains("AA-1", text);
        Assert.Contains("added", text);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(11, (await after.FlagClaims.SingleAsync(claim => claim.Id == 41)).FlagGridId);
        Assert.Null((await after.FlagClaims.SingleAsync(claim => claim.Id == 41)).HonoreeId);
        Assert.Null((await after.FlagGrids.SingleAsync(grid => grid.Id == 11)).HonoreeId);
        Assert.Null((await after.FlagClaims.SingleAsync(claim => claim.Id == 31)).FlagGridId);
        Assert.Equal(2, (await after.FlagClaims.SingleAsync(claim => claim.Id == 31)).HonoreeId);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).FlagGridId);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync()).UndoneUtc);
    }

    [Fact]
    public async Task Undo_is_blocked_when_a_change_request_was_added_after_the_merge()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.HonoreeChangeRequests.Add(new HonoreeChangeRequest
            {
                Id = 91,
                FlagClaimId = 31,
                HonoreeId = 1,
                FirstName = "John",
                LastName = "Smith",
                RequestStatus = "Submitted"
            });
            await db.SaveChangesAsync();
        }

        int auditId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            auditId = (await db.HonoreeMergeAudits.SingleAsync()).Id;
        }

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{auditId}/undo"));
        var text = await undone.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, undone.StatusCode);
        Assert.Contains("change request 91", text);
        Assert.Contains("added after the merge", text);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(1, (await after.HonoreeChangeRequests.SingleAsync(item => item.Id == 91)).HonoreeId);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).FlagGridId);
        Assert.True((await after.Honorees.SingleAsync(honoree => honoree.Id == 2)).IsActive);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync()).UndoneUtc);
    }

    [Fact]
    public async Task Undo_is_blocked_when_the_post_merge_state_was_not_recorded()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        int auditId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var audit = await db.HonoreeMergeAudits.SingleAsync();
            audit.AfterState = "{}";
            await db.SaveChangesAsync();
            auditId = audit.Id;
        }

        var undone = await client.SendAsync(Admin(HttpMethod.Post, $"/api/admin/duplicates/merges/{auditId}/undo"));
        var text = await undone.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, undone.StatusCode);
        Assert.Contains("post-merge state was not recorded", text);

        await using var check = factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal("{}", (await after.HonoreeMergeAudits.SingleAsync()).AfterState);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync()).UndoneUtc);
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).FlagGridId);
        Assert.Equal(12, (await after.Honorees.SingleAsync(honoree => honoree.Id == 2)).FlagGridId);
    }

    [Fact]
    public async Task Merge_history_and_undo_are_admin_only()
    {
        await ResetAsync();
        await SeedPairAsync();
        var client = factory.CreateClient();
        var merged = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = SourceFields(2, 1, 2)
        }));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            db.HonoreeMergeAudits.Add(new HonoreeMergeAudit
            {
                SurvivorId = 2,
                AbsorbedHonoreeIds = "[1]",
                BeforeState = db.HonoreeMergeAudits.Single().BeforeState,
                AfterSurvivorState = db.HonoreeMergeAudits.Single().AfterSurvivorState,
                CreatedHonoreeClaimIds = "[]",
                MergedBy = "earlier@example.com",
                MergedUtc = DateTime.UtcNow.AddDays(-1),
                SurvivorModifiedUtc = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var history = await client.SendAsync(Admin(HttpMethod.Get, "/api/admin/duplicates/merges"));
        var listed = await history.Content.ReadFromJsonAsync<List<HistoryBody>>(Json);
        Assert.Equal(2, listed!.Count);
        Assert.Equal("admin@example.com", listed[0].MergedBy);
        Assert.Equal("earlier@example.com", listed[1].MergedBy);

        int auditId;
        await using (var lookup = factory.Services.CreateAsyncScope())
        {
            var db = lookup.ServiceProvider.GetRequiredService<PfohDbContext>();
            auditId = (await db.HonoreeMergeAudits.SingleAsync(row => row.MergedBy == "admin@example.com")).Id;
        }

        foreach (var url in new[] { "/api/admin/duplicates/merges", $"/api/admin/duplicates/merges/{auditId}/undo" })
        {
            var member = new HttpRequestMessage(url.EndsWith("undo") ? HttpMethod.Post : HttpMethod.Get, url);
            member.Headers.Add(TestAuthHandler.OidHeader, "member-1");
            member.Headers.Add(TestAuthHandler.EmailHeader, "member@example.com");
            member.Headers.Add(TestAuthHandler.NameHeader, "Member");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(member)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(member.Method, url))).StatusCode);
        }

        await using var afterScope = factory.Services.CreateAsyncScope();
        var after = afterScope.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.False((await after.Honorees.SingleAsync(honoree => honoree.Id == 1)).IsActive);
        Assert.Null((await after.HonoreeMergeAudits.SingleAsync(row => row.MergedBy == "admin@example.com")).UndoneUtc);
    }

    private static object GridValue(Dictionary<string, int> fields, string gridName)
    {
        var copy = new Dictionary<string, int>(fields) { ["flagGrid"] = 2 };
        return new
        {
            survivorId = 2,
            duplicateIds = new[] { 1, 2 },
            fields = copy,
            values = new Dictionary<string, string> { ["flagGrid"] = gridName }
        };
    }

    private async Task RejectAsync(HttpClient client, string expected, object body)
    {
        var response = await client.SendAsync(Admin(HttpMethod.Post, "/api/admin/duplicates/merge", body));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        Assert.Contains(expected, text);
    }

    private async Task SeedPairAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        var now = DateTime.UtcNow;
        db.ServiceBranchCategories.Add(new ServiceBranchCategory
        {
            Id = 1,
            ServiceBranchCategoryName = "Armed forces",
            Description = "Armed forces",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.ServiceBranches.Add(new ServiceBranch
        {
            Id = 1,
            ServiceBranchCategoryId = 1,
            ServiceBranchName = "United States Army",
            Description = "Army",
            LogoFileName = "army.png",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.SponsorCategories.Add(new SponsorCategory
        {
            Id = 1,
            SponsorCategoryName = "Family",
            Description = "Family",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.Sponsors.Add(new Sponsor
        {
            Id = 7,
            SponsorCategoryId = 1,
            FirstName = "Ada",
            LastName = "Lovelace",
            Description = string.Empty,
            PhoneNumber = string.Empty,
            EmailAddress = "ada-sponsor@example.com",
            StreetAddress = string.Empty,
            City = string.Empty,
            State = string.Empty,
            ZipCode = string.Empty,
            Salutation = string.Empty,
            IsActive = true,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.FlagGrids.Add(Grid(11, "AA-1", now));
        db.FlagGrids.Add(Grid(12, "AA-2", now));
        var left = Honoree(1, "John", "Smith", "Photo bio", "john-1.jpg", 11, now);
        left.MiddleName = "Q";
        left.Suffix = "Jr.";
        left.Awards = "Bronze Star";
        left.DatesUserEntry = "1942 - 1945";
        left.StartYear = 1942;
        left.EndYear = 1945;
        left.PhoneNumber = "214-555-0101";
        left.EmailAddress = "one@example.com";
        var right = Honoree(2, "John", "Smith", "Kept bio", "", 12, now);
        right.MiddleName = "Q";
        right.Suffix = "Sr.";
        right.Awards = "Purple Heart";
        right.DatesUserEntry = "1950 - 1953";
        right.StartYear = 1950;
        right.EndYear = 1953;
        right.KIA = true;
        right.PhoneNumber = "214-555-0102";
        right.EmailAddress = "two@example.com";
        db.Honorees.Add(left);
        db.Honorees.Add(right);
        await db.SaveChangesAsync();
        db.FlagGrids.Single(grid => grid.Id == 11).HonoreeId = 1;
        db.FlagGrids.Single(grid => grid.Id == 12).HonoreeId = 2;
        db.FlagClaims.Add(Claim(31, 1, 11, "person-a", "ada@example.com", "Ada", now));
        db.FlagClaims.Add(Claim(32, 2, 12, "person-b", "bea@example.com", "Bea", now));
        await db.SaveChangesAsync();
    }

    private async Task ResetAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
        db.Database.EnsureCreated();
        db.HonoreeMergeAudits.RemoveRange(db.HonoreeMergeAudits);
        db.ReprintDuplicateReviews.RemoveRange(db.ReprintDuplicateReviews);
        db.HonoreeNotDuplicatePairs.RemoveRange(db.HonoreeNotDuplicatePairs);
        db.HonoreeClaims.RemoveRange(db.HonoreeClaims);
        db.HonoreeChangeRequests.RemoveRange(db.HonoreeChangeRequests);
        db.FlagClaims.RemoveRange(db.FlagClaims);
        db.Honorees.RemoveRange(db.Honorees);
        db.FlagGrids.RemoveRange(db.FlagGrids);
        db.Sponsors.RemoveRange(db.Sponsors);
        db.SponsorCategories.RemoveRange(db.SponsorCategories);
        db.ServiceBranches.RemoveRange(db.ServiceBranches);
        db.ServiceBranchCategories.RemoveRange(db.ServiceBranchCategories);
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, int> SourceFields(int survivor, int photoFrom, int awardsFrom) => new()
    {
        ["firstName"] = survivor,
        ["middleName"] = survivor,
        ["lastName"] = survivor,
        ["suffix"] = survivor,
        ["nickname"] = survivor,
        ["rank"] = survivor,
        ["serviceBranch"] = survivor,
        ["serviceYears"] = survivor,
        ["conflictsServed"] = survivor,
        ["awards"] = awardsFrom,
        ["description"] = survivor,
        ["kia"] = survivor,
        ["photo"] = photoFrom,
        ["flagGrid"] = survivor,
        ["phone"] = survivor,
        ["email"] = survivor,
        ["sponsor"] = survivor
    };

    private static FlagGrid Grid(int id, string name, DateTime now) => new()
    {
        Id = id,
        FlagGridName = name,
        Notes = string.Empty,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = now,
        ModifiedDate = now
    };

    private static Honoree Honoree(int id, string first, string last, string description, string photo, int? gridId, DateTime now) => new()
    {
        Id = id,
        FirstName = first,
        LastName = last,
        Rank = "Private",
        Description = description,
        PhotoFileName = photo,
        FlagGridId = gridId,
        ServiceBranchId = 1,
        ServiceBranchCategoryId = 1,
        Salutation = string.Empty,
        NameUserEntry = string.Empty,
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
    };

    private static FlagClaim Claim(int id, int honoreeId, int gridId, string userId, string email, string name, DateTime now) => new()
    {
        Id = id,
        HonoreeId = honoreeId,
        FlagGridId = gridId,
        ExternalUserObjectId = userId,
        ExternalUserEmail = email,
        ExternalUserName = name,
        ClaimStatus = "Claimed",
        CreatedUtc = now
    };

    private static HttpRequestMessage Admin(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.OidHeader, "admin-1");
        request.Headers.Add(TestAuthHandler.EmailHeader, "admin@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Admin");
        request.Headers.Add(TestAuthHandler.RolesHeader, "PFOH.Admin");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record HistoryBody(int Id, string SurvivorName, List<AbsorbedBody> Absorbed, string MergedBy, DateTime? UndoneUtc);
    private sealed record AbsorbedBody(int Id, string Name);
    private sealed record GroupBody(List<HonoreeBody> Honorees);
    private sealed record HonoreeBody(
        int Id,
        string Name,
        string? Suffix,
        string Description,
        string? ServiceBranchName,
        string ServiceYears,
        string Awards,
        string PhotoFileName,
        string? PhotoUrl,
        DateTime CreatedDate);
    private sealed record MergeBody(int SurvivorId, List<ClaimantBody> Claimants);
    private sealed record ClaimantBody(string Email);
}
