using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PFOH.Snapshot;

/// <summary>
/// Daily timer, nightly until <see cref="SnapshotScheduleOptions.MonthlyFromUtcDate"/>, then the 1st of the month.
/// </summary>
public sealed class SnapshotScheduleOptions
{
    public const string SectionName = "Snapshot";

    public string MonthlyFromUtcDate { get; set; } = "";
}

public sealed class SnapshotScheduleOptionsValidator : IValidateOptions<SnapshotScheduleOptions>
{
    public ValidateOptionsResult Validate(string? name, SnapshotScheduleOptions options)
    {
        if (SnapshotCadence.TryParse(options.MonthlyFromUtcDate, out _, out var error))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(error!);
    }
}

public static class SnapshotCadence
{
    public const string SkipMessage = "snapshot skipped: monthly mode";

    public static bool TryParse(string? value, out DateOnly date, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = default;
            error = "Snapshot:MonthlyFromUtcDate is empty. Set a UTC date in yyyy-MM-dd.";
            return false;
        }

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            error = "Snapshot:MonthlyFromUtcDate is invalid. Set a UTC date in yyyy-MM-dd.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ShouldRun(DateOnly utcDate, DateOnly monthlyFrom)
    {
        return utcDate < monthlyFrom || utcDate.Day == 1;
    }

    public static async Task InvokeAsync(
        TimeProvider time,
        SnapshotScheduleOptions options,
        ILogger logger,
        Func<CancellationToken, Task> execute,
        CancellationToken ct)
    {
        if (!TryParse(options.MonthlyFromUtcDate, out var monthlyFrom, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var utcDate = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (!ShouldRun(utcDate, monthlyFrom))
        {
            logger.LogInformation(SkipMessage);
            return;
        }

        await execute(ct);
    }
}
