using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Schema and seed that run before the app listens. They use no execution-strategy
/// retry and a short connect timeout, so a down database cannot spend the request
/// retry budget nine times and approach App Service's 230 second start limit.
/// The request path keeps <see cref="ProfileSqlRetry"/>.
/// A hung endpoint costs one connect timeout plus about <see cref="PerStepOverhead"/>
/// per step, then about <see cref="HostOverhead"/> before the process is listening.
/// That expected time is <see cref="ExpectedWhenDatabaseHangs"/>. The nine connect
/// timeouts on their own are <see cref="NominalConnectBudget"/>.
/// </summary>
internal static class StartupDatabase
{
    internal const int StepCount = 9;

    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Handshake and failure logging beyond the connect timeout, measured at about
    /// 100 milliseconds on a hung endpoint.
    /// </summary>
    internal static readonly TimeSpan PerStepOverhead = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Process startup outside the nine database steps, about 1.1 seconds.
    /// </summary>
    internal static readonly TimeSpan HostOverhead = TimeSpan.FromMilliseconds(1100);

    /// <summary>
    /// Nine connect attempts and nothing else. This stays within the existing startup limit.
    /// </summary>
    internal static TimeSpan NominalConnectBudget =>
        TimeSpan.FromTicks(ConnectTimeout.Ticks * StepCount);

    /// <summary>
    /// Hung endpoint: each step waits for the connect timeout plus
    /// <see cref="PerStepOverhead"/>, then the host finishes starting.
    /// 9 * (4s + 100ms) + 1.1s = 38s.
    /// </summary>
    internal static TimeSpan ExpectedWhenDatabaseHangs =>
        TimeSpan.FromTicks(
            (ConnectTimeout.Ticks + PerStepOverhead.Ticks) * StepCount + HostOverhead.Ticks);

    internal static PfohDbContext Create(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = (int)ConnectTimeout.TotalSeconds,
            // Azure SQL does not block the pool after a failure. A hung endpoint
            // then pays the connect timeout once per startup step.
            PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock
        };
        var options = new DbContextOptionsBuilder<PfohDbContext>()
            .UseSqlServer(
                builder.ConnectionString,
                sql => sql.CommandTimeout((int)ConnectTimeout.TotalSeconds))
            .Options;
        return new PfohDbContext(options);
    }
}
