namespace PFOH.Api.Services;

/// <summary>
/// Request-path SQL retry. <see cref="MaxRetryDelay"/> is zero because
/// SqlServerRetryingExecutionStrategy adds a fixed 5 seconds for throttling
/// errors after it applies this cap. A 5 second cap therefore waits about
/// 5, 6, and 8 seconds. Zero keeps each of those waits at 5 seconds,
/// 15 seconds in total, under the previous ~19 seconds.
/// </summary>
internal static class ProfileSqlRetry
{
    internal const int MaxRetryCount = 3;

    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.Zero;

    internal static readonly TimeSpan ThrottledWaitCap = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan TotalWaitBudget = TimeSpan.FromSeconds(19);
}
