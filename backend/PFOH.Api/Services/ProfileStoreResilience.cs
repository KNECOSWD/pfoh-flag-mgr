using Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal;
using Polly;
using Polly.Retry;

namespace PFOH.Api.Services;

/// <summary>
/// Thrown while the profile store circuit is open. Callers return 503 with Retry-After.
/// The message is fixed so a SQL error that contains an object id is not repeated.
/// </summary>
public sealed class ProfileStoreUnavailableException : Exception
{
    public ProfileStoreUnavailableException(TimeSpan retryAfter, Exception? inner = null)
        : base("The profile store is unavailable.", inner)
    {
        RetryAfter = retryAfter < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}

/// <summary>
/// Transient SQL and sign-in identity failures, using the same detector as
/// EF Core <c>EnableRetryOnFailure</c> (<see cref="SqlServerTransientExceptionDetector"/>).
/// That list includes deadlock 1205 and the transient login failures (18401, 17197, 40623).
/// </summary>
internal static class ProfileStoreTransient
{
    internal static bool IsTransient(Exception exception)
    {
        if (exception is ProfileStoreUnavailableException)
        {
            return false;
        }

        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is ProfileStoreUnavailableException)
            {
                continue;
            }

#pragma warning disable EF1001 // Same transient list as EF Core EnableRetryOnFailure.
            var transient = SqlServerTransientExceptionDetector.ShouldRetryOn(current);
#pragma warning restore EF1001
            if (transient)
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return false;
    }
}

/// <summary>
/// Polly retry: exponential backoff with jitter. The break after retries are exhausted
/// matches the short circuit-breaker break in Microsoft.Extensions.Http.Resilience (5 seconds),
/// then one half-open probe.
/// </summary>
internal static class ProfileStoreResilience
{
    internal static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    internal static readonly RetryStrategyOptions RetryOptions = new()
    {
        ShouldHandle = new PredicateBuilder().Handle<Exception>(ProfileStoreTransient.IsTransient),
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(2),
        MaxDelay = TimeSpan.FromSeconds(8),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true
    };

    private static readonly ResiliencePipeline ProductionPipeline = new ResiliencePipelineBuilder()
        .AddRetry(RetryOptions)
        .Build();

    internal static readonly AsyncLocal<ResiliencePipeline?> PipelineForTests = new();

    internal static ResiliencePipeline Pipeline => PipelineForTests.Value ?? ProductionPipeline;

    internal static ResiliencePipeline ImmediateRetries()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = RetryOptions.ShouldHandle,
                MaxRetryAttempts = RetryOptions.MaxRetryAttempts,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false
            })
            .Build();
    }
}

public enum ProfileStoreCircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2
}

/// <summary>
/// Standard circuit breaker for the profile schema batch.
/// Closed: calls run, and a transient error is retried.
/// Open: callers fail fast until the short break elapses.
/// Half-open: the next caller after the break is the single probe.
/// </summary>
public sealed class ProfileStoreCircuit
{
    private int state = (int)ProfileStoreCircuitState.Closed;
    private long openUntilTicks;

    internal static ProfileStoreCircuit Process { get; } = new();

    internal Func<DateTime>? UtcNow { get; set; }

    internal TimeSpan Break { get; set; } = ProfileStoreResilience.BreakDuration;

    internal DateTime Now => UtcNow?.Invoke() ?? DateTime.UtcNow;

    public ProfileStoreCircuitState State => (ProfileStoreCircuitState)Volatile.Read(ref state);

    internal long RetryAfterTicks =>
        State == ProfileStoreCircuitState.Closed ? 0 : Interlocked.Read(ref openUntilTicks);

    internal bool IsBlocked(DateTime utcNow)
    {
        var current = State;
        if (current == ProfileStoreCircuitState.Closed)
        {
            return false;
        }

        if (current == ProfileStoreCircuitState.HalfOpen)
        {
            return true;
        }

        return utcNow.Ticks < Interlocked.Read(ref openUntilTicks);
    }

    /// <summary>
    /// Called only while the schema gate is held. A closed circuit proceeds.
    /// An open circuit whose break has elapsed becomes the half-open probe.
    /// Anyone else waits out the break.
    /// </summary>
    internal bool TryEnter(DateTime utcNow)
    {
        var current = State;
        if (current == ProfileStoreCircuitState.Closed)
        {
            return true;
        }

        if (current == ProfileStoreCircuitState.HalfOpen)
        {
            return false;
        }

        if (utcNow.Ticks < Interlocked.Read(ref openUntilTicks))
        {
            return false;
        }

        Volatile.Write(ref state, (int)ProfileStoreCircuitState.HalfOpen);
        return true;
    }

    internal void Open(DateTime utcNow)
    {
        Interlocked.Exchange(ref openUntilTicks, utcNow.Add(Break).Ticks);
        Volatile.Write(ref state, (int)ProfileStoreCircuitState.Open);
    }

    internal void Close()
    {
        Volatile.Write(ref state, (int)ProfileStoreCircuitState.Closed);
        Interlocked.Exchange(ref openUntilTicks, 0);
    }

    internal TimeSpan RetryAfter(DateTime utcNow)
    {
        var until = new DateTime(Interlocked.Read(ref openUntilTicks), DateTimeKind.Utc);
        var remaining = until - utcNow;
        return remaining > TimeSpan.Zero ? remaining : Break;
    }
}
