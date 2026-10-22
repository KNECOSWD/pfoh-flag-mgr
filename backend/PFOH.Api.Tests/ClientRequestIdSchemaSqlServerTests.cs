using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

/// <summary>
/// Runs the ClientRequestId startup batches against SQL Server.
/// Skipped unless PFOH_SQLSERVER is a connection string. CI does not set it.
/// </summary>
public class ClientRequestIdSchemaSqlServerTests
{
    private const string ConnectionStringVariable = "PFOH_SQLSERVER";

    [SqlServerFact]
    public async Task Startup_schema_repairs_pre_existing_tables_and_endpoint_queries()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var admin = ForDatabase(connectionString!, "master");
        var freshName = "PfohCridFresh" + Guid.NewGuid().ToString("N");
        var existingName = "PfohCridExisting" + Guid.NewGuid().ToString("N");

        try
        {
            await ExecuteAdminAsync(admin, $"CREATE DATABASE [{freshName}]");

            await using (var fresh = NewContext(connectionString!, freshName))
            {
                await RunStartupSchemaAsync(fresh);
                await AssertClientRequestIdAsync(fresh, "dbo.HonoreeMergeAudits", "UX_HonoreeMergeAudits_ClientRequestId");
                var flagClaims = await ScalarAsync(fresh, "SELECT OBJECT_ID(N'dbo.FlagClaims', N'U')");
                Assert.True(flagClaims is null or DBNull);

                await RunStartupSchemaAsync(fresh);
                await AssertClientRequestIdAsync(fresh, "dbo.HonoreeMergeAudits", "UX_HonoreeMergeAudits_ClientRequestId");
            }

            await using (var existing = NewContext(connectionString!, existingName))
            {
                await existing.Database.EnsureCreatedAsync();
                await existing.Database.ExecuteSqlRawAsync(HonoreeSearchViewSql);

                await RunStartupSchemaAsync(existing);
                await AssertClientRequestIdAsync(existing, "dbo.FlagClaims", "UX_FlagClaims_ClientRequestId");
                await AssertClientRequestIdAsync(existing, "dbo.HonoreeMergeAudits", "UX_HonoreeMergeAudits_ClientRequestId");

                await DropIndexAsync(existing, "dbo.FlagClaims", "UX_FlagClaims_ClientRequestId");
                await DropIndexAsync(existing, "dbo.HonoreeMergeAudits", "UX_HonoreeMergeAudits_ClientRequestId");
                await RunStartupSchemaAsync(existing);
                await AssertClientRequestIdAsync(existing, "dbo.FlagClaims", "UX_FlagClaims_ClientRequestId");
                await AssertClientRequestIdAsync(existing, "dbo.HonoreeMergeAudits", "UX_HonoreeMergeAudits_ClientRequestId");

                await DropClientRequestIdAsync(existing, "dbo.FlagClaims", "UX_FlagClaims_ClientRequestId");
                await DropClientRequestIdAsync(existing, "dbo.HonoreeMergeAudits", "UX_HonoreeMergeAudits_ClientRequestId");
                Assert.Equal(0, await ColumnCountAsync(existing, "dbo.FlagClaims"));
                Assert.Equal(0, await ColumnCountAsync(existing, "dbo.HonoreeMergeAudits"));

                await existing.Database.ExecuteSqlRawAsync(PreSchemaFlagClaimSql);
                await existing.Database.ExecuteSqlRawAsync(SchemaSql.ForExecuteSqlRaw(PreSchemaMergeAuditSql));

                await RunStartupSchemaAsync(existing);
                await AssertClientRequestIdAsync(existing, "dbo.FlagClaims", "UX_FlagClaims_ClientRequestId");
                await AssertClientRequestIdAsync(existing, "dbo.HonoreeMergeAudits", "UX_HonoreeMergeAudits_ClientRequestId");

                await RunStartupSchemaAsync(existing);
                await AssertEndpointQueriesAsync(existing);
            }
        }
        finally
        {
            ResetSchemaReady();
            SqlConnection.ClearAllPools();
            await DropDatabaseAsync(admin, freshName);
            await DropDatabaseAsync(admin, existingName);
        }
    }

    private static async Task RunStartupSchemaAsync(PfohDbContext db)
    {
        ResetSchemaReady();
        await PendingSubmissionGridSchema.EnsureAsync(db, CancellationToken.None);
        await HonoreeMergeAuditSchema.EnsureAsync(db, CancellationToken.None);
    }

    private static async Task AssertEndpointQueriesAsync(PfohDbContext db)
    {
        await HonoreeClaimStore.SyncAsync(db, CancellationToken.None);
        var honorees = await DuplicateReviewGroups.LoadActiveAsync(db, CancellationToken.None);
        var suppressed = await DuplicateReviewGroups.LoadSuppressedAsync(db, CancellationToken.None);
        _ = DuplicateReviewGroups.Match(honorees, suppressed);
        if (honorees.Count > 0)
        {
            var ids = honorees.Select(honoree => honoree.Id).ToList();
            _ = await db.HonoreeClaims
                .AsNoTracking()
                .Where(claim => ids.Contains(claim.HonoreeId) && !HonoreeClaimStore.HiddenStatuses.Contains(claim.ClaimStatus))
                .ToListAsync();
        }

        var mine = await db.FlagClaims
            .AsNoTracking()
            .Include(claim => claim.FlagGrid)
            .Include(claim => claim.Honoree)
            .Include(claim => claim.HonoreeChangeRequests)
            .Where(claim =>
                claim.ExternalUserObjectId == "user-1" &&
                claim.ClaimStatus != "AdminDirectEdit" &&
                claim.ClaimStatus != "AdminDirectEditCompleted" &&
                claim.ClaimStatus != "Unclaimed" &&
                claim.ClaimStatus != "Cancelled")
            .OrderByDescending(claim => claim.CreatedUtc)
            .ToListAsync();
        var claim = Assert.Single(mine);
        Assert.Null(claim.ClientRequestId);

        var claimantSources = await db.FlagClaims
            .AsNoTracking()
            .Include(claimRow => claimRow.HonoreeChangeRequests)
            .Where(claimRow => claimRow.Id == claim.Id)
            .ToListAsync();
        Assert.Null(Assert.Single(claimantSources).ClientRequestId);

        var approved = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(request => request.FlagClaim)
            .Include(request => request.FlagGrid)
            .Include(request => request.ServiceBranch)
            .Where(request =>
                request.RequestStatus == "Approved" &&
                request.RequiresCardReprint &&
                request.CardPrintedUtc == null)
            .OrderBy(request => request.ReviewedUtc ?? request.SubmittedUtc ?? request.CreatedUtc)
            .ToListAsync();
        Assert.Empty(approved);
        var honoreeIds = approved
            .Where(request => request.HonoreeId.HasValue)
            .Select(request => request.HonoreeId!.Value)
            .Distinct()
            .ToList();
        _ = await db.HonoreeSearchResults
            .AsNoTracking()
            .Where(honoree => honoreeIds.Contains(honoree.Id))
            .ToDictionaryAsync(honoree => honoree.Id, honoree => honoree.PDFUrl);

        var audits = await db.HonoreeMergeAudits.AsNoTracking().ToListAsync();
        Assert.Null(Assert.Single(audits).ClientRequestId);
    }

    private static async Task AssertClientRequestIdAsync(PfohDbContext db, string table, string index)
    {
        Assert.Equal(1, await ColumnCountAsync(db, table));
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.is_nullable, TYPE_NAME(c.user_type_id), i.is_unique, i.has_filter, i.filter_definition, col.name
            FROM sys.columns c
            INNER JOIN sys.indexes i ON i.object_id = c.object_id
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
            INNER JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE c.object_id = OBJECT_ID(@table)
              AND c.name = N'ClientRequestId'
              AND i.name = @index
            """;
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "@table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);
        var indexParameter = command.CreateParameter();
        indexParameter.ParameterName = "@index";
        indexParameter.Value = index;
        command.Parameters.Add(indexParameter);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{table} is missing {index} on ClientRequestId.");
        Assert.True(reader.GetBoolean(0), $"{table}.ClientRequestId should be nullable.");
        Assert.Equal("uniqueidentifier", reader.GetString(1));
        Assert.True(reader.GetBoolean(2), $"{index} should be unique.");
        Assert.True(reader.GetBoolean(3), $"{index} should be filtered.");
        var filter = reader.GetString(4);
        Assert.Contains("ClientRequestId", filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NOT NULL", filter, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ClientRequestId", reader.GetString(5));
        Assert.False(await reader.ReadAsync(), $"{index} should cover only ClientRequestId.");
    }

    private static async Task<int> ColumnCountAsync(PfohDbContext db, string table)
    {
        var value = await ScalarAsync(db, $"""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'{table}')
              AND name = N'ClientRequestId'
            """);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task DropIndexAsync(PfohDbContext db, string table, string index)
    {
        var quotedTable = QuoteTable(table);
        var sql = $"""
            IF EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{table}')
                  AND name = N'{index}')
                DROP INDEX [{index}] ON {quotedTable};
            """;
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task DropClientRequestIdAsync(PfohDbContext db, string table, string index)
    {
        await DropIndexAsync(db, table, index);
        var quotedTable = QuoteTable(table);
        var sql = $"""
            IF COL_LENGTH(N'{table}', N'ClientRequestId') IS NOT NULL
                ALTER TABLE {quotedTable} DROP COLUMN [ClientRequestId];
            """;
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static string QuoteTable(string table)
    {
        var parts = table.Split('.');
        Assert.Equal(2, parts.Length);
        return $"[{parts[0]}].[{parts[1]}]";
    }

    private static async Task<object?> ScalarAsync(PfohDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void ResetSchemaReady()
    {
        ResetReady(typeof(PendingSubmissionGridSchema));
        ResetReady(typeof(HonoreeMergeAuditSchema));
    }

    private static void ResetReady(Type type)
    {
        var field = type.GetField("ready", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        field!.SetValue(null, false);
    }

    private static PfohDbContext NewContext(string connectionString, string database)
    {
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseSqlServer(ForDatabase(connectionString, database))
            .Options;
        return new PfohDbContext(options);
    }

    private static string ForDatabase(string connectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = database,
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string database)
    {
        try
        {
            await ExecuteAdminAsync(
                adminConnectionString,
                $"""
                IF DB_ID(N'{database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{database}];
                END
                """);
        }
        catch (SqlException)
        {
        }
    }

    private const string PreSchemaFlagClaimSql = """
        INSERT INTO [dbo].[FlagClaims] (
            [ExternalUserObjectId], [ExternalUserEmail], [ClaimStatus], [CreatedUtc])
        VALUES (N'user-1', N'user@example.com', N'Claimed', SYSUTCDATETIME());
        """;

    private const string PreSchemaMergeAuditSql = """
        INSERT INTO [dbo].[HonoreeMergeAudits] (
            [SurvivorId], [AbsorbedHonoreeIds], [BeforeState], [AfterSurvivorState], [AfterState],
            [CreatedHonoreeClaimIds], [MergedBy], [MergedUtc], [SurvivorModifiedUtc])
        VALUES (1, N'[]', N'{}', N'{}', N'{}', N'[]', N'admin', SYSUTCDATETIME(), SYSUTCDATETIME());
        """;

    private const string HonoreeSearchViewSql = """
        CREATE OR ALTER VIEW [dbo].[vwHonorees] AS
        SELECT
            CAST(0 AS int) AS [Id],
            CAST(N'' AS nvarchar(max)) AS [FirstName],
            CAST(NULL AS nvarchar(max)) AS [MiddleName],
            CAST(N'' AS nvarchar(max)) AS [LastName],
            CAST(N'' AS nvarchar(max)) AS [FullName],
            CAST(NULL AS nvarchar(max)) AS [FlagLocation],
            CAST(NULL AS nvarchar(max)) AS [Nickname],
            CAST(N'' AS nvarchar(max)) AS [Awards],
            CAST(N'' AS nvarchar(max)) AS [EmailAddress],
            CAST(0 AS bit) AS [IsActive],
            CAST(NULL AS nvarchar(max)) AS [ImageUrl],
            CAST(NULL AS nvarchar(max)) AS [PDFUrl],
            CAST(N'' AS nvarchar(max)) AS [Rank],
            CAST(0 AS bit) AS [KIA],
            CAST(N'' AS nvarchar(max)) AS [PhoneNumber],
            CAST(NULL AS nvarchar(max)) AS [Suffix],
            CAST(NULL AS int) AS [ServiceBranchId],
            CAST(NULL AS nvarchar(max)) AS [ServiceBranchName],
            CAST(NULL AS nvarchar(max)) AS [FlagGrid],
            CAST(NULL AS int) AS [SponsorId],
            CAST(NULL AS nvarchar(max)) AS [SponsorName]
        WHERE 1 = 0;
        """;

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
