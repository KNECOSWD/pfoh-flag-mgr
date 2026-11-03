using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Additive tables for the submission receipt token and the supersede audit.
/// Also adds a nullable column on dbo.HonoreeChangeRequests. Production has
/// no EF migration history, so startup runs this SQL. The migration runs the
/// same text. Nothing here drops or rewrites existing rows.
/// </summary>
public static class SubmissionGuardSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        SET LOCK_TIMEOUT 5000;
        IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[SubmissionReceipts] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SubmissionReceipts] PRIMARY KEY,
                [ChangeRequestId] int NOT NULL,
                [PublicToken] uniqueidentifier NOT NULL,
                [PublicCode] nvarchar(16) NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [FK_SubmissionReceipts_HonoreeChangeRequests_ChangeRequestId]
                    FOREIGN KEY ([ChangeRequestId]) REFERENCES [dbo].[HonoreeChangeRequests] ([Id])
            );
            CREATE UNIQUE INDEX [UX_SubmissionReceipts_ChangeRequestId]
                ON [dbo].[SubmissionReceipts] ([ChangeRequestId]);
            CREATE UNIQUE INDEX [UX_SubmissionReceipts_PublicToken]
                ON [dbo].[SubmissionReceipts] ([PublicToken]);
            CREATE UNIQUE INDEX [UX_SubmissionReceipts_PublicCode]
                ON [dbo].[SubmissionReceipts] ([PublicCode]);
        END
        IF OBJECT_ID(N'[dbo].[SubmissionAuditEvents]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[SubmissionAuditEvents] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SubmissionAuditEvents] PRIMARY KEY,
                [Action] nvarchar(32) NOT NULL,
                [ChangeRequestId] int NOT NULL,
                [KeeperChangeRequestId] int NULL,
                [ActorEmail] nvarchar(256) NOT NULL,
                [ActorObjectId] nvarchar(128) NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [Note] nvarchar(500) NULL
            );
            CREATE INDEX [IX_SubmissionAuditEvents_ChangeRequestId]
                ON [dbo].[SubmissionAuditEvents] ([ChangeRequestId], [CreatedUtc]);
        END
        IF COL_LENGTH(N'dbo.HonoreeChangeRequests', N'CorrectsChangeRequestId') IS NULL
            ALTER TABLE [dbo].[HonoreeChangeRequests] ADD [CorrectsChangeRequestId] int NULL;
        """;

    internal const string AddPublicCodeSql = """
        SET LOCK_TIMEOUT 5000;
        IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.SubmissionReceipts', N'PublicCode') IS NULL
            ALTER TABLE [dbo].[SubmissionReceipts] ADD [PublicCode] nvarchar(16) NULL;
        """;

    internal const string AddPublicCodeIndexSql = """
        SET LOCK_TIMEOUT 5000;
        IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NOT NULL
           AND NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_SubmissionReceipts_PublicCode'
                  AND object_id = OBJECT_ID(N'[dbo].[SubmissionReceipts]'))
            EXEC(N'CREATE UNIQUE INDEX [UX_SubmissionReceipts_PublicCode] ON [dbo].[SubmissionReceipts]([PublicCode]) WHERE [PublicCode] IS NOT NULL');
        """;

    internal const string DropSql = """
        IF COL_LENGTH(N'dbo.HonoreeChangeRequests', N'CorrectsChangeRequestId') IS NOT NULL
            ALTER TABLE [dbo].[HonoreeChangeRequests] DROP COLUMN [CorrectsChangeRequestId];
        IF OBJECT_ID(N'[dbo].[SubmissionAuditEvents]', N'U') IS NOT NULL
            DROP TABLE [dbo].[SubmissionAuditEvents];
        IF OBJECT_ID(N'[dbo].[SubmissionReceipts]', N'U') IS NOT NULL
            DROP TABLE [dbo].[SubmissionReceipts];
        """;

    public static async Task EnsureAsync(PfohDbContext db, CancellationToken ct)
    {
        if (ready || !db.Database.IsRelational())
        {
            return;
        }

        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (ready)
            {
                return;
            }

            await db.Database.ExecuteSqlRawAsync(CreateSql, ct);
            await db.Database.ExecuteSqlRawAsync(AddPublicCodeSql, ct);
            await db.Database.ExecuteSqlRawAsync(AddPublicCodeIndexSql, ct);
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
