using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Reports Unhealthy while the profile store circuit is open or half-open, or while
/// a connectivity probe fails. A closed circuit with a successful probe is Healthy.
/// </summary>
public sealed class ProfileStoreHealthCheck(
    IServiceScopeFactory scopes,
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

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken))
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
