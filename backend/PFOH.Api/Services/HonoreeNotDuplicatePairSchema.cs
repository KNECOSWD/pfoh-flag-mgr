using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Creates dbo.HonoreeNotDuplicatePairs. The create is skipped when the table
/// already exists, so startup can run it more than once. Production has no EF
/// migration history, so startup runs this SQL.
/// </summary>
public static class HonoreeNotDuplicatePairSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool ready;

    internal const string CreateSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeNotDuplicatePairs]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[HonoreeNotDuplicatePairs] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_HonoreeNotDuplicatePairs] PRIMARY KEY,
                [LowHonoreeId] int NOT NULL,
                [HighHonoreeId] int NOT NULL,
                [MarkedBy] nvarchar(255) NOT NULL,
                [MarkedUtc] datetime2 NOT NULL,
                CONSTRAINT [FK_HonoreeNotDuplicatePairs_LowHonoree] FOREIGN KEY ([LowHonoreeId]) REFERENCES [dbo].[Honorees] ([Id]),
                CONSTRAINT [FK_HonoreeNotDuplicatePairs_HighHonoree] FOREIGN KEY ([HighHonoreeId]) REFERENCES [dbo].[Honorees] ([Id]),
                CONSTRAINT [CK_HonoreeNotDuplicatePairs_Ordered] CHECK ([LowHonoreeId] < [HighHonoreeId])
            );
            CREATE UNIQUE INDEX [UX_HonoreeNotDuplicatePairs_Pair]
                ON [dbo].[HonoreeNotDuplicatePairs] ([LowHonoreeId], [HighHonoreeId]);
        END
        """;

    internal const string DropSql = """
        IF OBJECT_ID(N'[dbo].[HonoreeNotDuplicatePairs]', N'U') IS NOT NULL
        BEGIN
            DROP TABLE [dbo].[HonoreeNotDuplicatePairs];
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
