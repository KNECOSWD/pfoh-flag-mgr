using System.Globalization;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.Extensions.Configuration;

namespace PFOH.Api.Services;

public static class TelemetryIngestion
{
    public const double DefaultMaxTelemetryItemsPerSecond = 2;
    public const int DefaultSuccessfulDependencyDropBelowMilliseconds = 100;
    public const string SamplingExcludedTypes = "Exception;Event";
    public const string MaxTelemetryItemsPerSecondKey = "ApplicationInsights:Sampling:MaxTelemetryItemsPerSecond";
    public const string SuccessfulDependencyDropBelowMillisecondsKey = "ApplicationInsights:Dependency:SuccessfulDropBelowMilliseconds";

    public static double ReadMaxTelemetryItemsPerSecond(IConfiguration configuration)
    {
        return ReadNonNegativeDouble(configuration[MaxTelemetryItemsPerSecondKey], DefaultMaxTelemetryItemsPerSecond);
    }

    public static TimeSpan ReadSuccessfulDependencyDropBelow(IConfiguration configuration)
    {
        var milliseconds = ReadNonNegativeDouble(
            configuration[SuccessfulDependencyDropBelowMillisecondsKey],
            DefaultSuccessfulDependencyDropBelowMilliseconds);
        if (milliseconds > int.MaxValue)
        {
            milliseconds = int.MaxValue;
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public static void ConfigureProcessorChain(
        TelemetryProcessorChainBuilder builder,
        double maxTelemetryItemsPerSecond,
        TimeSpan successfulDependencyDropBelow)
    {
        // Processors run in the order they are added. Drop probes and fast
        // successful dependencies before adaptive sampling counts them.
        builder.Use(next => new TelemetryVolumeProcessor(next, successfulDependencyDropBelow));
        builder.UseAdaptiveSampling(
            maxTelemetryItemsPerSecond,
            excludedTypes: SamplingExcludedTypes);
    }

    internal static bool IsUnsuccessfulOrServerFailure(bool? success, string? resultCode)
    {
        if (success == false)
        {
            return true;
        }

        return IsHttpServerError(resultCode);
    }

    internal static bool IsHttpServerError(string? resultCode)
    {
        if (string.IsNullOrWhiteSpace(resultCode))
        {
            return false;
        }

        var value = resultCode.Trim();
        var length = 0;
        while (length < value.Length && char.IsDigit(value[length]))
        {
            length++;
        }

        if (length != 3)
        {
            return false;
        }

        if (length < value.Length && char.IsLetterOrDigit(value[length]))
        {
            return false;
        }

        return int.TryParse(value.AsSpan(0, length), NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            && status is >= 500 and <= 599;
    }

    internal static bool IsSuccessfulProbeRequest(RequestTelemetry request)
    {
        if (IsUnsuccessfulOrServerFailure(request.Success, request.ResponseCode))
        {
            return false;
        }

        return IsProbePath(request.Url?.AbsolutePath) || IsProbePath(request.Name);
    }

    internal static bool IsProbePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();
        var query = value.IndexOf('?');
        if (query >= 0)
        {
            value = value[..query];
        }

        var space = value.LastIndexOf(' ');
        if (space >= 0)
        {
            value = value[(space + 1)..];
        }

        value = value.TrimEnd('/');
        if (value.Length == 0)
        {
            return false;
        }

        if (!value.StartsWith('/'))
        {
            value = "/" + value;
        }

        return value.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/robots933456.txt", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsFastSuccessfulSqlOrHttp(DependencyTelemetry dependency, TimeSpan threshold)
    {
        if (dependency.Success != true)
        {
            return false;
        }

        if (IsUnsuccessfulOrServerFailure(dependency.Success, dependency.ResultCode))
        {
            return false;
        }

        if (!IsSqlOrHttp(dependency.Type))
        {
            return false;
        }

        return dependency.Duration < threshold;
    }

    internal static bool IsSqlOrHttp(string? type)
    {
        return type is not null
            && (type.Equals("SQL", StringComparison.OrdinalIgnoreCase)
                || type.Equals("HTTP", StringComparison.OrdinalIgnoreCase));
    }

    private static double ReadNonNegativeDouble(string? raw, double fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value)
            || double.IsInfinity(value)
            || value < 0)
        {
            return fallback;
        }

        return value;
    }
}

/// <summary>
/// Keeps exceptions, failed requests, and failed dependencies at 100%.
/// Initializers run before sampling. The SDK ignores an item that already
/// has <see cref="ISupportSampling.SamplingPercentage"/> set to 100.
/// </summary>
public sealed class FailureRetentionTelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        switch (telemetry)
        {
            case ExceptionTelemetry exception:
                Retain(exception);
                break;
            case RequestTelemetry request when TelemetryIngestion.IsUnsuccessfulOrServerFailure(request.Success, request.ResponseCode):
                Retain(request);
                break;
            case DependencyTelemetry dependency when TelemetryIngestion.IsUnsuccessfulOrServerFailure(dependency.Success, dependency.ResultCode):
                Retain(dependency);
                break;
        }
    }

    private static void Retain(ISupportSampling item)
    {
        item.SamplingPercentage = 100;
    }
}

/// <summary>
/// Drops successful health and platform probes, and successful SQL or HTTP
/// dependencies faster than the configured duration. Everything else is passed on.
/// </summary>
public sealed class TelemetryVolumeProcessor(ITelemetryProcessor next, TimeSpan successfulDependencyDropBelow) : ITelemetryProcessor
{
    public TimeSpan SuccessfulDependencyDropBelow { get; } = successfulDependencyDropBelow;

    public void Process(ITelemetry item)
    {
        if (item is RequestTelemetry request && TelemetryIngestion.IsSuccessfulProbeRequest(request))
        {
            return;
        }

        if (item is DependencyTelemetry dependency
            && TelemetryIngestion.IsFastSuccessfulSqlOrHttp(dependency, SuccessfulDependencyDropBelow))
        {
            return;
        }

        next.Process(item);
    }
}
