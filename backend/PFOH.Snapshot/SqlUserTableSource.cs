using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace PFOH.Snapshot;

/// <summary>
/// Reads catalog metadata and allow-listed columns. Unlisted tables are counted and not selected.
/// </summary>
public sealed partial class SqlUserTableSource(string connectionString) : IUserTableSource
{
    public async Task<IReadOnlyList<SnapshotColumn>> GetColumnsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS AS c
            INNER JOIN INFORMATION_SCHEMA.TABLES AS t
                ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """;
        var columns = new List<SnapshotColumn>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new SnapshotColumn
            {
                SchemaName = reader.GetString(0),
                TableName = reader.GetString(1),
                ColumnName = reader.GetString(2),
                TypeName = reader.GetString(3),
                MaxLength = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                ColumnId = reader.GetInt32(5)
            });
        }

        return columns;
    }

    public async Task<long> GetCountAsync(string schema, string table, CancellationToken ct)
    {
        var sql = "SELECT COUNT_BIG(*) FROM " + Quote(schema) + "." + Quote(table);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetRowsAsync(
        string schema,
        string table,
        IReadOnlyList<string> columns,
        CancellationToken ct)
    {
        if (columns.Count == 0)
        {
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        var sql = "SELECT " + string.Join(", ", columns.Select(Quote)) + " FROM " + Quote(schema) + "." + Quote(table);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    internal static string Quote(string identifier)
    {
        if (!Identifier().IsMatch(identifier))
        {
            throw new InvalidOperationException("The snapshot refused an unexpected SQL identifier.");
        }

        return "[" + identifier + "]";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
}
