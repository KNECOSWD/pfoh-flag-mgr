using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Reports Unhealthy while the profile store circuit is open or half-open, or while
/// a connectivity probe fails. A closed circuit with a successful probe is Healthy.
/// The SQL Server probe uses <see cref="ReadinessDatabase"/>, not the retrying
/// request context, and the token is linked to the health request.
/// </summary>
public sealed class ProfileStoreHealthCheck(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ProfileStoreCircuit circuit,
    ILogger<ProfileStoreHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (circuit.State != ProfileStoreCircuitState.Closed)
        {
            return Unhealthy();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadinessDatabase.ConnectTimeout);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            var sqlServer = db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;
            if (sqlServer)
            {
                var connectionString = configuration.GetConnectionString("DefaultConnection");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return Unhealthy();
                }

                await using var probe = ReadinessDatabase.Create(connectionString);
                if (!await probe.Database.CanConnectAsync(timeout.Token))
                {
                    return Unhealthy();
                }
            }
            else if (!await db.Database.CanConnectAsync(timeout.Token))
            {
                return Unhealthy();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Profile store health check failed. ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
                ex.GetType().FullName,
                UserProfileService.SqlErrorNumber(ex));
            return Unhealthy();
        }

        return HealthCheckResult.Healthy();
    }

    private static HealthCheckResult Unhealthy() =>
        HealthCheckResult.Unhealthy("The profile store is unavailable.");
}
