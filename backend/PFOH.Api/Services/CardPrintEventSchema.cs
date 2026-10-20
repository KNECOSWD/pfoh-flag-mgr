using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.CardPrintEvents when it is missing.
/// Production already has the honoree tables and no EF migration history, so a
/// full migration would try to recreate those tables. These statements only
/// create or drop the print-event table. The EF migration
/// <c>AddCardPrintEvents</c> runs the same SQL when a database is updated through EF.
/// Existing card rows, including CardPrintedUtc and CardPrintBatchId, are not altered.
/// </summary>
public static class CardPrintEventSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[CardPrintEvents]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[CardPrintEvents] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CardPrintEvents] PRIMARY KEY,
                [CardPrintBatchId] uniqueidentifier NOT NULL,
                [PrintedBy] nvarchar(255) NOT NULL,
                [PrintedUtc] datetime2 NOT NULL,
                [CardCount] int NOT NULL,
                [Comment] nvarchar(500) NULL,
                CONSTRAINT [UQ_CardPrintEvents_CardPrintBatchId] UNIQUE ([CardPrintBatchId])
            );
            CREATE INDEX [IX_CardPrintEvents_PrintedUtc] ON [dbo].[CardPrintEvents] ([PrintedUtc]);
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[CardPrintEvents]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[CardPrintEvents];
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
