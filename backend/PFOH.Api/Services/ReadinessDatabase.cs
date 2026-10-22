using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Connectivity probe for <c>/health/ready</c>. It does not use the request
/// execution strategy, so a closed or hung database port answers from the
/// connect timeout instead of the 30 second request budget and its retries.
/// </summary>
internal static class ReadinessDatabase
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    internal static PfohDbContext Create(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = (int)ConnectTimeout.TotalSeconds,
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
