using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace PFOH.Snapshot.Tests;

public class SnapshotCadenceTests
{
    private static readonly DateOnly Cutover = new(2026, 12, 1);

    [Theory]
    [InlineData("2026-11-29T01:45:00Z", true)]
    [InlineData("2026-11-30T01:45:00Z", true)]
    [InlineData("2026-12-01T01:45:00Z", true)]
    [InlineData("2026-12-02T01:45:00Z", false)]
    [InlineData("2027-01-01T01:45:00Z", true)]
    public async Task Utc_date_decides_the_run(string instant, bool runs)
    {
        var lines = new List<string>();
        var executed = 0;
        var clock = new FixedClock(DateTimeOffset.Parse(instant));
        var options = new SnapshotScheduleOptions { MonthlyFromUtcDate = "2026-12-01" };

        await SnapshotCadence.InvokeAsync(
            clock,
            options,
            new CaptureLogger(lines),
            _ =>
            {
                executed++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(runs ? 1 : 0, executed);
        if (runs)
        {
            Assert.DoesNotContain(SnapshotCadence.SkipMessage, lines);
        }
        else
        {
            Assert.Equal(SnapshotCadence.SkipMessage, Assert.Single(lines));
        }

        var utcDate = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        Assert.Equal(runs, SnapshotCadence.ShouldRun(utcDate, Cutover));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026/12/01")]
    [InlineData("not-a-date")]
    public void Empty_or_invalid_date_fails_at_startup(string value)
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Snapshot:MonthlyFromUtcDate"] = value
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddOptions<SnapshotScheduleOptions>()
                    .Bind(context.Configuration.GetSection(SnapshotScheduleOptions.SectionName))
                    .ValidateOnStart();
                services.AddSingleton<IValidateOptions<SnapshotScheduleOptions>, SnapshotScheduleOptionsValidator>();
            })
            .Build();

        var error = Assert.Throws<OptionsValidationException>(() => host.Start());
        Assert.Contains("Snapshot:MonthlyFromUtcDate", error.Message, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class CaptureLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception));
        }
    }
}
