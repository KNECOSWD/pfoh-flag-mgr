using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Schema and seed that run before the app listens. They use no execution-strategy
/// retry and a short connect timeout, so a down database cannot spend the request
/// retry budget nine times and approach App Service's 230 second start limit.
/// The request path keeps <see cref="ProfileSqlRetry"/>.
/// </summary>
internal static class StartupDatabase
{
    internal const int StepCount = 9;

    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Nine steps, one attempt each, each bounded by <see cref="ConnectTimeout"/>.
    /// </summary>
    internal static TimeSpan WorstCaseWhenDatabaseIsDown =>
        TimeSpan.FromTicks(ConnectTimeout.Ticks * StepCount);

    internal static PfohDbContext Create(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = (int)ConnectTimeout.TotalSeconds
        };
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseSqlServer(
                builder.ConnectionString,
                sql => sql.CommandTimeout((int)ConnectTimeout.TotalSeconds))
            .Options;
        return new PfohDbContext(options);
    }
}
