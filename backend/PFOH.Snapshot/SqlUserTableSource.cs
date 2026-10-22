using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace PFOH.Snapshot;

/// <summary>
/// Reads the snapshot views and the row-count view. It does not select base-table free text.
/// </summary>
public sealed partial class SqlUserTableSource(string connectionString) : IUserTableSource
{
    public async Task<IReadOnlyList<SnapshotColumn>> GetColumnsAsync(CancellationToken ct)
    {
        const string columnSql = """
            SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS AS c
            WHERE c.TABLE_SCHEMA = 'snapshot' AND c.TABLE_NAME <> 'UserTableRowCounts'
            ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
            """;
        const string tableSql = """
            SELECT SchemaName, TableName
            FROM snapshot.UserTableRowCounts
            """;
        var columns = new List<SnapshotColumn>();
        var counted = new List<(string Schema, string Table)>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new SqlCommand(columnSql, connection) { CommandTimeout = 120 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                columns.Add(new SnapshotColumn
                {
                    SchemaName = "dbo",
                    TableName = reader.GetString(0),
                    ColumnName = reader.GetString(1),
                    TypeName = reader.GetString(2),
                    MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    ColumnId = reader.GetInt32(4)
                });
            }
        }

        await using (var command = new SqlCommand(tableSql, connection) { CommandTimeout = 120 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                counted.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var seen = columns.Select(column => column.TableKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (schema, table) in counted)
        {
            var key = schema + "." + table;
            if (seen.Contains(key))
            {
                continue;
            }

            columns.Add(new SnapshotColumn
            {
                SchemaName = schema,
                TableName = table,
                ColumnName = "_",
                TypeName = "",
                MaxLength = null,
                ColumnId = 0
            });
            seen.Add(key);
        }

        return columns;
    }

    public async Task<long> GetCountAsync(string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT [RowCount]
            FROM snapshot.UserTableRowCounts
            WHERE SchemaName = @schema AND TableName = @table
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
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

        if (!string.Equals(schema, "dbo", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The snapshot views are defined for dbo tables.");
        }

        var sql = "SELECT " + string.Join(", ", columns.Select(Quote)) + " FROM [snapshot]." + Quote(table);
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
