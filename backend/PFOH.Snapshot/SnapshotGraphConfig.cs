using Microsoft.Extensions.Logging;

namespace PFOH.Snapshot;

/// <summary>
/// Graph settings can be empty until the app registration and certificate exist.
/// The host logs that and the timer skips the run. It does not throw on a timer tick.
/// </summary>
public static class SnapshotGraphConfig
{
    public const string TenantKey = "Snapshot:GraphTenantId";
    public const string ClientKey = "Snapshot:GraphClientId";
    public const string CertificateKey = "Snapshot:GraphCertificateName";

    public static IReadOnlyList<string> Missing(Func<string, string?> read)
    {
        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(read(TenantKey)))
        {
            missing.Add(TenantKey);
        }

        if (string.IsNullOrWhiteSpace(read(ClientKey)))
        {
            missing.Add(ClientKey);
        }

        if (string.IsNullOrWhiteSpace(read(CertificateKey)))
        {
            missing.Add(CertificateKey);
        }

        return missing;
    }

    public static string StartupMessage(IReadOnlyList<string> missing)
    {
        return "Snapshot Graph configuration is incomplete. The function is up, and the timer will skip work until these settings are set: "
            + string.Join(", ", missing);
    }

    public static string SkipMessage(IReadOnlyList<string> missing)
    {
        return "Snapshot Graph configuration is incomplete. Skipping this run. Missing: " + string.Join(", ", missing);
    }
}

public static class SnapshotTimerGate
{
    public static async Task InvokeAsync(
        Func<string, string?> settings,
        ILogger logger,
        Func<CancellationToken, Task> execute,
        CancellationToken ct)
    {
        var missing = SnapshotGraphConfig.Missing(settings);
        if (missing.Count > 0)
        {
            logger.LogWarning("{Message}", SnapshotGraphConfig.SkipMessage(missing));
            return;
        }

        await execute(ct);
    }
}
