using Microsoft.Extensions.Logging;
using Xunit;

namespace PFOH.Snapshot.Tests;

public class SnapshotGraphConfigTests
{
    [Fact]
    public async Task Empty_graph_config_skips_the_run()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var executed = 0;

        await SnapshotTimerGate.InvokeAsync(
            _ => " ",
            new CaptureLogger(lines),
            _ =>
            {
                executed++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, executed);
        var warning = Assert.Single(lines);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Skipping this run", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Snapshot:GraphTenantId", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Snapshot:GraphClientId", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Snapshot:GraphCertificateName", warning.Message, StringComparison.Ordinal);
        Assert.Contains("The function is up", SnapshotGraphConfig.StartupMessage(SnapshotGraphConfig.Missing(_ => null)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filled_graph_config_runs()
    {
        var lines = new List<(LogLevel Level, string Message)>();
        var executed = 0;
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SnapshotGraphConfig.TenantKey] = "77f89427-b4a7-43ed-92fc-3eb5bd63d0f1",
            [SnapshotGraphConfig.ClientKey] = "graph-app",
            [SnapshotGraphConfig.CertificateKey] = "pfoh-snapshot-graph"
        };

        await SnapshotTimerGate.InvokeAsync(
            key => settings[key],
            new CaptureLogger(lines),
            _ =>
            {
                executed++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, executed);
        Assert.Empty(lines);
    }

    private sealed class CaptureLogger(List<(LogLevel Level, string Message)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
