using Microsoft.Extensions.Logging;

namespace PFOH.Snapshot;

/// <summary>
/// Builds the two snapshot files and writes them. Logs counts only.
/// </summary>
public sealed class SnapshotJob(
    IUserTableSource tables,
    IDirectorySource directory,
    ISnapshotSink sink,
    TimeProvider time,
    ILogger<SnapshotJob> logger)
{
    public async Task<SnapshotRunCounts> ExecuteAsync(CancellationToken ct)
    {
        var pulledAt = time.GetUtcNow();
        var dump = await DbDumpBuilder.BuildAsync(tables, pulledAt, ct);
        var users = await directory.ReadAsync(ct);
        var directoryDocument = DirectorySnapshotBuilder.Build(pulledAt, users);
        var dbBytes = SnapshotJson.ToUtf8(dump);
        var directoryBytes = SnapshotJson.ToUtf8(directoryDocument);
        var folder = pulledAt.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await sink.WriteAsync(folder + "/db-dump.json", dbBytes, ct);
        await sink.WriteAsync(folder + "/directory-users.json", directoryBytes, ct);

        var emitted = dump.Tables.Values.Sum(table => table.Rows.Count);
        var counts = new SnapshotRunCounts(
            dump.Tables.Count,
            dump.Tables.Values.Sum(table => table.Count),
            emitted,
            directoryDocument.Users.Count,
            directoryDocument.SignInActivity.Count,
            dbBytes.Length,
            directoryBytes.Length);
        logger.LogInformation(
            "Metrics snapshot wrote {TableCount} tables, {CountedRows} counted rows, {EmittedRows} emitted rows, {DirectoryUsers} directory users, {SignInActivityRows} sign-in rows, {DbBytes} database bytes, and {DirectoryBytes} directory bytes.",
            counts.TableCount,
            counts.CountedRows,
            counts.EmittedRows,
            counts.DirectoryUsers,
            counts.SignInActivityRows,
            counts.DbBytes,
            counts.DirectoryBytes);
        return counts;
    }
}
