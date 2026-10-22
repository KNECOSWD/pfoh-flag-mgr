using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PFOH.Snapshot;

namespace PFOH.Snapshot.Function;

/// <summary>
/// Logs incomplete Graph settings when the host starts. It does not throw.
/// </summary>
public sealed class SnapshotGraphStartupCheck(IConfiguration configuration, ILogger<SnapshotGraphStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = SnapshotGraphConfig.Missing(key => configuration[key]);
        if (missing.Count > 0)
        {
            logger.LogError("{Message}", SnapshotGraphConfig.StartupMessage(missing));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
