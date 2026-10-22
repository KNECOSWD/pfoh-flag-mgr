using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace PFOH.Snapshot.Function;

/// <summary>
/// Timer only. A manual run uses the Functions admin endpoint
/// POST /admin/functions/SnapshotNightly with the host key. There is no HTTP trigger.
/// 01:45 UTC is 8:45 PM Colombia time. Colombia is UTC-5 all year.
/// The Functions NCRONTAB is UTC, so the schedule does not depend on WEBSITE_TIME_ZONE.
/// </summary>
public sealed class SnapshotNightly(SnapshotJob job, ILogger<SnapshotNightly> logger)
{
    [Function("SnapshotNightly")]
    public async Task Run([TimerTrigger("0 45 1 * * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Metrics snapshot timer started. Next={Next}.", timer.ScheduleStatus?.Next);
        await job.ExecuteAsync(ct);
    }
}
