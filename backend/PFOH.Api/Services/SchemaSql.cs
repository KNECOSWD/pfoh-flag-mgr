namespace PFOH.Api.Services;

/// <summary>
/// EF Core formats every string passed to ExecuteSqlRaw with string.Format
/// before the text reaches the database. A brace in the SQL is a format item.
/// Doubling braces here leaves the statement that SQL Server runs unchanged.
/// </summary>
public static class SchemaSql
{
    internal static string ForExecuteSqlRaw(string sql) =>
        sql.Replace("{", "{{", StringComparison.Ordinal)
           .Replace("}", "}}", StringComparison.Ordinal);
}
