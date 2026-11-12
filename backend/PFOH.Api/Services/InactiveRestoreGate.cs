namespace PFOH.Api.Services;

/// <summary>
/// Test hook that runs before a restore takes its transaction. Production leaves
/// <see cref="BeforeRestore"/> null.
/// </summary>
public sealed class InactiveRestoreGate
{
    public Func<CancellationToken, Task>? BeforeRestore { get; set; }
}
