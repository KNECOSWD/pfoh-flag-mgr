using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Data;
using PFOH.Api.Models;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public sealed class SubmissionGuardSqlFactory : PfohApiFactory
{
    private readonly string? adminConnection;

    public string DatabaseName { get; } = "PfohGuardRace" + Guid.NewGuid().ToString("N");

    public SubmissionGuardSqlFactory()
    {
        var raw = Environment.GetEnvironmentVariable(SubmissionGuardSqlServerTests.ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var builder = new SqlConnectionStringBuilder(raw)
        {
            InitialCatalog = DatabaseName,
            TrustServerCertificate = true
        };
        SqlServerConnectionString = builder.ConnectionString;
        ExtraSettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = builder.ConnectionString
        };
        builder.InitialCatalog = "master";
        adminConnection = builder.ConnectionString;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || adminConnection is null)
        {
            return;
        }

        try
        {
            using var connection = new SqlConnection(adminConnection);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{DatabaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{DatabaseName}];
                END
                """;
            command.ExecuteNonQuery();
        }
        catch (SqlException)
        {
        }
    }
}

public class SubmissionGuardSqlServerTests(SubmissionGuardSqlFactory factory) : IClassFixture<SubmissionGuardSqlFactory>
{
    internal const string ConnectionStringVariable = "PFOH_SQLSERVER";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SqlServerFact]
    public async Task Eight_concurrent_nominations_with_one_key_return_the_same_receipt()
    {
        int branchId;
        int categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            await db.Database.EnsureCreatedAsync();
            var now = DateTime.UtcNow;
            var category = new ServiceBranchCategory
            {
                ServiceBranchCategoryName = "Armed forces",
                Description = "Armed forces",
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            };
            var branch = new ServiceBranch
            {
                ServiceBranchCategory = category,
                ServiceBranchName = "Army",
                Description = "Army",
                LogoFileName = string.Empty,
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            };
            db.SponsorCategories.Add(new SponsorCategory
            {
                SponsorCategoryName = "Family",
                Description = "Family",
                CreatedBy = "seed",
                ModifiedBy = "seed",
                CreatedDate = now,
                ModifiedDate = now
            });
            db.ServiceBranchCategories.Add(category);
            db.ServiceBranches.Add(branch);
            await db.SaveChangesAsync();
            branchId = branch.Id;
            categoryId = category.Id;
        }

        var client = factory.CreateClient();
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SendAsync(Nomination(key, branchId, categoryId))));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<CreatedDto>(Json)));
        var token = bodies[0]!.ReceiptToken;
        Assert.NotEqual(Guid.Empty, token);
        Assert.All(bodies, body => Assert.Equal(token, body!.ReceiptToken));

        await using var read = factory.Services.CreateAsyncScope();
        var saved = read.ServiceProvider.GetRequiredService<PfohDbContext>();
        Assert.Equal(1, await saved.FlagClaims.CountAsync(claim => claim.ClientRequestId == key));
        Assert.Equal(1, await saved.HonoreeChangeRequests.CountAsync());
        Assert.Equal(1, await saved.SubmissionReceipts.CountAsync());
    }

    private static HttpRequestMessage Nomination(Guid key, int branchId, int categoryId)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent("Cara"), "FirstName" },
            { new StringContent("Cole"), "LastName" },
            { new StringContent("Sergeant"), "Rank" },
            { new StringContent(branchId.ToString()), "ServiceBranchId" },
            { new StringContent(categoryId.ToString()), "ServiceBranchCategoryId" },
            { new StringContent("A clearly fake tribute."), "Description" },
            { new StringContent("2145550100"), "SubmitterPhoneNumber" },
            { new StringContent("family@example.test"), "SubmitterEmailAddress" },
            { new StringContent("false"), "Kia" }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/flag-claims/nominate") { Content = content };
        request.Headers.Add(TestAuthHandler.OidHeader, "family-user");
        request.Headers.Add(TestAuthHandler.EmailHeader, "family@example.test");
        request.Headers.Add(TestAuthHandler.NameHeader, "Family Member");
        request.Headers.Add("Idempotency-Key", key.ToString("D"));
        return request;
    }

    private sealed record CreatedDto(Guid? ReceiptToken);

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
            {
                Skip = "SQL Server is not available. Set PFOH_SQLSERVER to a connection string to run this test.";
            }
        }
    }
}

public class SameClaimSubmitSqlServerTests(SubmissionGuardSqlFactory factory) : IClassFixture<SubmissionGuardSqlFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SqlServerFact]
    public async Task Resubmit_on_sql_server_supersedes_the_same_claim_and_replay_keeps_one_receipt()
    {
        int claimId;
        int olderId;
        int draftId;
        int otherClaimId;
        int otherId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            await db.Database.EnsureCreatedAsync();
            var now = DateTime.UtcNow;
            var claim = new FlagClaim
            {
                FlagGridId = null,
                HonoreeId = null,
                ExternalUserObjectId = "resubmit-user",
                ExternalUserEmail = "resubmit@example.test",
                ExternalUserName = "Resubmit User",
                ClaimStatus = "Claimed",
                CreatedUtc = now.AddMinutes(-8)
            };
            claim.HonoreeChangeRequests.Add(Change("Submitted", now.AddMinutes(-8)));
            claim.HonoreeChangeRequests.Add(Change("Draft", now));
            var other = new FlagClaim
            {
                FlagGridId = null,
                HonoreeId = null,
                ExternalUserObjectId = "someone-else",
                ExternalUserEmail = "else@example.test",
                ExternalUserName = "Someone Else",
                ClaimStatus = "Submitted",
                CreatedUtc = now.AddMinutes(-3),
                SubmittedUtc = now.AddMinutes(-3)
            };
            other.HonoreeChangeRequests.Add(Change("Submitted", now.AddMinutes(-3)));
            db.FlagClaims.Add(claim);
            db.FlagClaims.Add(other);
            await db.SaveChangesAsync();
            claimId = claim.Id;
            olderId = claim.HonoreeChangeRequests.Single(row => row.RequestStatus == "Submitted").Id;
            draftId = claim.HonoreeChangeRequests.Single(row => row.RequestStatus == "Draft").Id;
            otherClaimId = other.Id;
            otherId = other.HonoreeChangeRequests.Single().Id;
        }

        var client = factory.CreateClient();
        var first = await client.SendAsync(Submit(claimId, "resubmit-user", "resubmit@example.test"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<CreatedDto>(Json);
        Assert.NotNull(created?.ReceiptToken);
        Assert.NotEqual(Guid.Empty, created.ReceiptToken);

        var again = await client.SendAsync(Submit(claimId, "resubmit-user", "resubmit@example.test"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replay = await again.Content.ReadFromJsonAsync<CreatedDto>(Json);
        Assert.Equal(created.ReceiptToken, replay!.ReceiptToken);

        var untouched = await client.SendAsync(Submit(otherClaimId, "someone-else", "else@example.test"));
        Assert.Equal(HttpStatusCode.BadRequest, untouched.StatusCode);

        await using var read = factory.Services.CreateAsyncScope();
        var saved = read.ServiceProvider.GetRequiredService<PfohDbContext>();
        var older = await saved.HonoreeChangeRequests.SingleAsync(row => row.Id == olderId);
        Assert.Equal(PendingReviewMaintenance.Superseded, older.RequestStatus);
        var otherRow = await saved.HonoreeChangeRequests.SingleAsync(row => row.Id == otherId);
        Assert.Equal("Submitted", otherRow.RequestStatus);
        var audit = Assert.Single(await saved.SubmissionAuditEvents.Where(row => row.ChangeRequestId == olderId).ToListAsync());
        Assert.Equal(SubmissionAuditEvent.Superseded, audit.Action);
        Assert.Equal(draftId, audit.KeeperChangeRequestId);
        var receipt = Assert.Single(await saved.SubmissionReceipts.Where(row => row.ChangeRequestId == draftId).ToListAsync());
        Assert.Equal(created.ReceiptToken, receipt.PublicToken);
        Assert.Equal(0, await saved.SubmissionReceipts.CountAsync(row => row.ChangeRequestId == otherId));
    }

    private static HonoreeChangeRequest Change(string status, DateTime timestamp) => new()
    {
        FlagGridId = null,
        HonoreeId = null,
        FirstName = "Ada",
        LastName = "Lovelace",
        Description = "A tribute",
        RequestStatus = status,
        CreatedUtc = timestamp,
        SubmittedUtc = status == "Submitted" ? timestamp : null
    };

    private static HttpRequestMessage Submit(int claimId, string oid, string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/flag-claims/{claimId}/submit");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);
        request.Headers.Add(TestAuthHandler.EmailHeader, email);
        request.Headers.Add(TestAuthHandler.NameHeader, "Submitter");
        return request;
    }

    private sealed record CreatedDto(Guid? ReceiptToken);

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SubmissionGuardSqlServerTests.ConnectionStringVariable)))
            {
                Skip = "SQL Server is not available. Set PFOH_SQLSERVER to a connection string to run this test.";
            }
        }
    }
}
