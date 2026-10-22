using System.Globalization;

namespace PFOH.Snapshot;

public static class DbDumpBuilder
{
    public static async Task<DbDumpDocument> BuildAsync(IUserTableSource source, DateTimeOffset pulledAt, CancellationToken ct)
    {
        var catalog = await source.GetColumnsAsync(ct);
        var tables = catalog
            .GroupBy(column => column.TableKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(column => column.ColumnId).ToList(), StringComparer.Ordinal);

        var columnDocuments = new List<ColumnDocument>();
        var tableDocuments = new SortedDictionary<string, TableDocument>(StringComparer.Ordinal);

        foreach (var (key, columns) in tables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var dot = key.IndexOf('.');
            var schema = dot < 0 ? "dbo" : key[..dot];
            var table = dot < 0 ? key : key[(dot + 1)..];
            var count = await source.GetCountAsync(schema, table, ct);
            var rows = new List<SortedDictionary<string, object?>>();

            if (SnapshotFieldSpec.IsListedTable(key))
            {
                var types = columns.ToDictionary(column => column.ColumnName, column => column, StringComparer.Ordinal);
                foreach (var name in SnapshotFieldSpec.OutputColumns(key))
                {
                    if (SnapshotFieldSpec.HonoreeDerivedFlags.Contains(name, StringComparer.Ordinal))
                    {
                        columnDocuments.Add(new ColumnDocument
                        {
                            SchemaName = schema,
                            TableName = table,
                            ColumnName = name,
                            TypeName = "bit",
                            MaxLength = null,
                            ColumnId = (columns.Count == 0 ? 0 : columns.Max(column => column.ColumnId)) + Array.IndexOf(SnapshotFieldSpec.HonoreeDerivedFlags, name) + 1
                        });
                        continue;
                    }

                    if (!types.TryGetValue(name, out var meta) || SnapshotProjection.IsBinaryType(meta.TypeName))
                    {
                        continue;
                    }

                    columnDocuments.Add(ToDocument(meta));
                }

                var read = SnapshotFieldSpec.OutputColumns(key)
                    .Where(name =>
                        SnapshotFieldSpec.HonoreeDerivedFlags.Contains(name, StringComparer.Ordinal)
                        || (types.ContainsKey(name) && !SnapshotProjection.IsBinaryType(types[name].TypeName)))
                    .ToArray();
                var rawRows = read.Length == 0
                    ? Array.Empty<IReadOnlyDictionary<string, object?>>()
                    : await source.GetRowsAsync(schema, table, read, ct);
                foreach (var raw in rawRows)
                {
                    rows.Add(ProjectRow(key, raw, types));
                }
            }

            tableDocuments[key] = new TableDocument { Count = count, Rows = rows };
        }

        return new DbDumpDocument
        {
            PulledAt = Format(pulledAt),
            Columns = columnDocuments,
            Tables = tableDocuments
        };
    }

    public static SortedDictionary<string, object?> ProjectRow(
        string tableKey,
        IReadOnlyDictionary<string, object?> raw,
        IReadOnlyDictionary<string, SnapshotColumn> types)
    {
        var projected = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in SnapshotFieldSpec.OutputColumns(tableKey))
        {
            if (name == "HasPhoto")
            {
                projected[name] = AsBool(Lookup(raw, name)) ?? HonoreePresence.HasPhoto(Lookup(raw, "PhotoFileName"));
                continue;
            }

            if (name == "HasDescription")
            {
                projected[name] = AsBool(Lookup(raw, name)) ?? HonoreePresence.HasDescription(Lookup(raw, "Description"));
                continue;
            }

            if (name == "HasAwards")
            {
                projected[name] = AsBool(Lookup(raw, name)) ?? HonoreePresence.HasAwards(Lookup(raw, "Awards"));
                continue;
            }

            if (name == "HasConflictsServed")
            {
                projected[name] = AsBool(Lookup(raw, name)) ?? HonoreePresence.HasConflictsServed(Lookup(raw, "ConflictsServed"));
                continue;
            }

            if (!raw.TryGetValue(name, out var value))
            {
                continue;
            }

            types.TryGetValue(name, out var meta);
            var normalized = SnapshotProjection.Normalize(value, meta?.TypeName);
            if (normalized is SnapshotProjection.Skip)
            {
                continue;
            }

            projected[name] = normalized;
        }

        return projected;
    }

    private static bool? AsBool(object? value)
    {
        return value is bool bit ? bit : null;
    }

    private static object? Lookup(IReadOnlyDictionary<string, object?> raw, string name)
    {
        return raw.TryGetValue(name, out var value) ? value : null;
    }

    private static ColumnDocument ToDocument(SnapshotColumn column)
    {
        return new ColumnDocument
        {
            SchemaName = column.SchemaName,
            TableName = column.TableName,
            ColumnName = column.ColumnName,
            TypeName = column.TypeName,
            MaxLength = column.MaxLength,
            ColumnId = column.ColumnId
        };
    }

    public static string Format(DateTimeOffset pulledAt)
    {
        return pulledAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
