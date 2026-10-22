using PFOH.Snapshot;

namespace PFOH.Snapshot.Function;

/// <summary>
/// Registered only while Graph settings are empty. The timer returns before this is called.
/// </summary>
public sealed class IncompleteGraphDirectorySource : IDirectorySource
{
    public Task<IReadOnlyList<DirectoryUserRecord>> ReadAsync(CancellationToken ct)
    {
        throw new InvalidOperationException(SnapshotGraphConfig.StartupMessage(SnapshotGraphConfig.Missing(_ => null)));
    }
}
