namespace PFOH.Api.Services;

/// <summary>
/// Request-path SQL retry. <see cref="MaxRetryDelay"/> is one second so a
/// non-throttling transient error waits between attempts. EF Core 8.0.12
/// caps the exponential base delay at this value, then
/// <c>SqlServerRetryingExecutionStrategy</c> adds a fixed 5 seconds for
/// throttling errors. The base delays are 0, 1, and 1 seconds, so throttling
/// waits 5, 6, and 6 seconds (17 seconds total), under the 19 second budget.
/// A non-throttling error such as deadlock 1205 waits 0, 1, and 1 seconds.
/// </summary>
internal static class ProfileSqlRetry
{
    internal const int MaxRetryCount = 3;

    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan TotalWaitBudget = TimeSpan.FromSeconds(19);
}
