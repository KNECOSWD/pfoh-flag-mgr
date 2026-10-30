using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates the reactivation token and request tables. Production has no EF
/// migration history, so startup runs this SQL. The migration runs the same text.
/// </summary>
public static class ReactivationSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeReactivationTokens]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[HonoreeReactivationTokens] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_HonoreeReactivationTokens] PRIMARY KEY,
                [HonoreeId] int NOT NULL,
                [TokenHash] varbinary(32) NOT NULL,
                [RequesterEmail] nvarchar(255) NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                [ExpiresUtc] datetime2 NOT NULL,
                [UsedUtc] datetime2 NULL,
                [RevokedUtc] datetime2 NULL,
                [CreatedBy] nvarchar(255) NOT NULL,
                CONSTRAINT [FK_HonoreeReactivationTokens_Honorees_HonoreeId] FOREIGN KEY ([HonoreeId]) REFERENCES [dbo].[Honorees] ([Id])
            );
            CREATE UNIQUE INDEX [UX_HonoreeReactivationTokens_TokenHash]
                ON [dbo].[HonoreeReactivationTokens] ([TokenHash]);
            CREATE UNIQUE INDEX [UX_HonoreeReactivationTokens_OneActive]
                ON [dbo].[HonoreeReactivationTokens] ([HonoreeId])
                WHERE [UsedUtc] IS NULL AND [RevokedUtc] IS NULL;
        END
        IF OBJECT_ID(N'[dbo].[HonoreeReactivationRequests]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[HonoreeReactivationRequests] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_HonoreeReactivationRequests] PRIMARY KEY,
                [HonoreeId] int NOT NULL,
                [ChangeRequestId] int NOT NULL,
                [TokenId] int NOT NULL,
                [RequesterNote] nvarchar(2000) NULL,
                [LinkUsedUtc] datetime2 NOT NULL,
                [CreatedUtc] datetime2 NOT NULL,
                CONSTRAINT [FK_HonoreeReactivationRequests_Honorees_HonoreeId] FOREIGN KEY ([HonoreeId]) REFERENCES [dbo].[Honorees] ([Id]),
                CONSTRAINT [FK_HonoreeReactivationRequests_ChangeRequest] FOREIGN KEY ([ChangeRequestId]) REFERENCES [dbo].[HonoreeChangeRequests] ([Id]),
                CONSTRAINT [FK_HonoreeReactivationRequests_Token] FOREIGN KEY ([TokenId]) REFERENCES [dbo].[HonoreeReactivationTokens] ([Id]),
                CONSTRAINT [UQ_HonoreeReactivationRequests_ChangeRequestId] UNIQUE ([ChangeRequestId]),
                CONSTRAINT [UQ_HonoreeReactivationRequests_TokenId] UNIQUE ([TokenId])
            );
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeReactivationRequests]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[HonoreeReactivationRequests];
        END
        IF OBJECT_ID(N'[dbo].[HonoreeReactivationTokens]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[HonoreeReactivationTokens];
        END
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
            ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
