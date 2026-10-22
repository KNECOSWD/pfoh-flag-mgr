using System.Reflection;
using System.Text.RegularExpressions;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

/// <summary>
/// EF Core 8 formats ExecuteSqlRaw text before it reaches SQL Server.
/// Every startup statement must survive that formatting and come back as the
/// SQL the schema constant describes.
/// </summary>
public class StartupSchemaSqlTests
{
    private static readonly Regex ExecuteCall = new(
        @"ExecuteSqlRaw(?:Async)?\s*\(\s*(?<arg>SchemaSql\.ForExecuteSqlRaw\([^)]+\)|[^,\r\n)]+)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// ExecuteCall stops an argument at the first newline, so a call whose
    /// argument starts on the next line can be missed. The counts below are
    /// the calls each startup schema actually makes.
    /// </summary>
    private static readonly Dictionary<string, int> ExpectedExecuteCalls = new(StringComparer.Ordinal)
    {
        ["CardPrintEventSchema"] = 1,
        ["FlagRemovalQueueSchema"] = 1,
        ["HonoreeClaimSchema"] = 3,
        ["HonoreeMergeAuditSchema"] = 4,
        ["HonoreeNotDuplicatePairSchema"] = 1,
        ["PendingSubmissionGridSchema"] = 4,
        ["ReprintDuplicateReviewSchema"] = 1,
        ["UserProfileSchema"] = 4
    };

    [Fact]
    public void Every_startup_schema_statement_survives_ef_formatting()
    {
        var statements = StartupStatements();
        Assert.NotEmpty(statements);

        foreach (var statement in statements)
        {
            string formatted;
            try
            {
                formatted = string.Format(statement.PassedToExecuteSqlRaw, Array.Empty<object>());
            }
            catch (FormatException ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{statement.Source} passes SQL that EF Core cannot format: {ex.Message}");
            }

            Assert.Equal(statement.IntendedSql, formatted);
        }
    }

    [Fact]
    public void Merge_audit_executed_sql_keeps_the_empty_object_default()
    {
        var statements = StartupStatements()
            .Where(statement => statement.Source.Contains("HonoreeMergeAuditSchema", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(4, statements.Count);
        var defaults = statements
            .Where(statement => statement.IntendedSql.Contains("DEFAULT N'{}'", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, defaults.Count);
        Assert.All(defaults, statement =>
        {
            var formatted = string.Format(statement.PassedToExecuteSqlRaw, Array.Empty<object>());
            Assert.Equal(statement.IntendedSql, formatted);
        });
        Assert.Contains(
            statements,
            statement => statement.IntendedSql.Contains("ClientRequestId", StringComparison.Ordinal)
                && statement.IntendedSql.Contains("IS NOT NULL", StringComparison.Ordinal));

        Assert.Contains("DEFAULT N'{}'", HonoreeMergeAuditSchema.CreateSql, StringComparison.Ordinal);
        Assert.Contains("DEFAULT N'{}'", HonoreeMergeAuditSchema.AddColumnSql, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() =>
            string.Format(HonoreeMergeAuditSchema.CreateSql, Array.Empty<object>()));
        Assert.Throws<FormatException>(() =>
            string.Format(HonoreeMergeAuditSchema.AddColumnSql, Array.Empty<object>()));
    }

    /// <summary>
    /// SQL Server compiles a batch before any statement runs. ALTER TABLE ADD
    /// and a later reference to that column in the same batch fail with error
    /// 207 when the table already exists, and the ALTER is rolled back with
    /// the rest of the batch. A reference inside EXEC is its own batch.
    /// </summary>
    [Fact]
    public void No_startup_batch_adds_a_column_and_references_it_outside_exec()
    {
        var statements = StartupStatements();
        var offenders = new List<string>();
        foreach (var statement in statements)
        {
            var columns = ColumnsAddedAndReferencedOutsideExec(statement.IntendedSql);
            if (columns.Count > 0)
            {
                offenders.Add($"{statement.Source}: {string.Join(", ", columns)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A startup batch adds a column and references it outside EXEC, which SQL Server rejects with error 207: "
            + string.Join("; ", offenders));

        Assert.Contains(
            statements,
            statement => statement.IntendedSql.Contains(
                "ALTER TABLE [dbo].[FlagClaims] ADD [ClientRequestId]",
                StringComparison.Ordinal));
        Assert.Contains(
            statements,
            statement => statement.IntendedSql.Contains(
                "CREATE UNIQUE INDEX [UX_FlagClaims_ClientRequestId]",
                StringComparison.Ordinal)
                && !statement.IntendedSql.Contains("COL_LENGTH", StringComparison.Ordinal));
        Assert.Contains(
            statements,
            statement => statement.IntendedSql.Contains(
                "ALTER TABLE [dbo].[HonoreeMergeAudits] ADD [ClientRequestId]",
                StringComparison.Ordinal));
        Assert.Contains(
            statements,
            statement => statement.IntendedSql.Contains(
                "CREATE UNIQUE INDEX [UX_HonoreeMergeAudits_ClientRequestId]",
                StringComparison.Ordinal)
                && !statement.IntendedSql.Contains("COL_LENGTH", StringComparison.Ordinal));
    }

    [Fact]
    public void Column_reference_detector_flags_an_add_and_index_in_one_batch()
    {
        const string bad = """
            ALTER TABLE [dbo].[FlagClaims] ADD [ClientRequestId] uniqueidentifier NULL;
            CREATE UNIQUE INDEX [UX_FlagClaims_ClientRequestId]
                ON [dbo].[FlagClaims]([ClientRequestId])
                WHERE [ClientRequestId] IS NOT NULL;
            """;
        Assert.Equal(["ClientRequestId"], ColumnsAddedAndReferencedOutsideExec(bad));

        const string deferred = """
            IF COL_LENGTH(N'dbo.FlagClaims', N'ClientRequestId') IS NULL
                ALTER TABLE [dbo].[FlagClaims] ADD [ClientRequestId] uniqueidentifier NULL;
            EXEC(N'CREATE UNIQUE INDEX [UX_FlagClaims_ClientRequestId] ON [dbo].[FlagClaims]([ClientRequestId]) WHERE [ClientRequestId] IS NOT NULL');
            """;
        Assert.Empty(ColumnsAddedAndReferencedOutsideExec(deferred));
    }

    private static List<string> ColumnsAddedAndReferencedOutsideExec(string sql)
    {
        var masked = MaskStringsAndComments(sql);
        var spans = ColumnAddStatementSpans(masked);
        if (spans.Count == 0)
        {
            return [];
        }

        var visible = masked.ToCharArray();
        foreach (var span in spans)
        {
            for (var i = span.Start; i < span.End; i++)
            {
                visible[i] = ' ';
            }
        }

        var visibleSql = new string(visible);
        var hits = new List<string>();
        foreach (var span in spans)
        {
            foreach (var column in span.Columns)
            {
                if (ContainsIdentifier(visibleSql, column)
                    && !hits.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    hits.Add(column);
                }
            }
        }

        return hits;
    }

    private static List<ColumnAddSpan> ColumnAddStatementSpans(string masked)
    {
        var spans = new List<ColumnAddSpan>();
        foreach (Match alter in AlterTable.Matches(masked))
        {
            var statementEnd = StatementEnd(masked, alter.Index);
            var cursor = alter.Index;
            while (cursor < statementEnd)
            {
                var add = AddKeyword.Match(masked, cursor, statementEnd - cursor);
                if (!add.Success)
                {
                    break;
                }

                var afterAdd = add.Index + add.Length;
                if (NextWordIs(masked, afterAdd, statementEnd, "CONSTRAINT"))
                {
                    cursor = afterAdd;
                    continue;
                }

                var columns = ReadAddedColumnNames(masked, afterAdd, statementEnd);
                if (columns.Count > 0)
                {
                    spans.Add(new ColumnAddSpan(alter.Index, statementEnd, columns));
                }

                break;
            }
        }

        return spans;
    }

    private static int StatementEnd(string sql, int start)
    {
        var depth = 0;
        for (var i = start; i < sql.Length; i++)
        {
            if (sql[i] == '(')
            {
                depth++;
            }
            else if (sql[i] == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (sql[i] == ';' && depth == 0)
            {
                return i + 1;
            }
        }

        return sql.Length;
    }

    private static List<string> ReadAddedColumnNames(string sql, int start, int statementEnd)
    {
        var columns = new List<string>();
        var cursor = start;
        while (cursor < statementEnd)
        {
            cursor = SkipWhitespace(sql, cursor, statementEnd);
            if (cursor >= statementEnd || !TryReadIdentifier(sql, cursor, out var name, out var afterName))
            {
                break;
            }

            columns.Add(name);
            cursor = afterName;
            var depth = 0;
            while (cursor < statementEnd)
            {
                if (sql[cursor] == '(')
                {
                    depth++;
                }
                else if (sql[cursor] == ')')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (sql[cursor] == ',' && depth == 0)
                {
                    cursor++;
                    break;
                }
                else if (sql[cursor] == ';' && depth == 0)
                {
                    return columns;
                }

                cursor++;
            }
        }

        return columns;
    }

    private static bool ContainsIdentifier(string sql, string name)
    {
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                if (end < 0)
                {
                    return false;
                }

                var ident = sql.Substring(i + 1, end - i - 1).Replace("]]", "]", StringComparison.Ordinal);
                if (ident.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                i = end;
                continue;
            }

            if (!IsIdentStart(sql[i]))
            {
                continue;
            }

            var j = i + 1;
            while (j < sql.Length && IsIdentPart(sql[j]))
            {
                j++;
            }

            if (sql.Substring(i, j - i).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            i = j - 1;
        }

        return false;
    }

    private static bool NextWordIs(string sql, int start, int end, string word)
    {
        start = SkipWhitespace(sql, start, end);
        if (!TryReadIdentifier(sql, start, out var ident, out _))
        {
            return false;
        }

        return ident.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadIdentifier(string sql, int start, out string name, out int after)
    {
        name = string.Empty;
        after = start;
        if (start >= sql.Length)
        {
            return false;
        }

        if (sql[start] == '[')
        {
            var end = sql.IndexOf(']', start + 1);
            if (end < 0)
            {
                return false;
            }

            name = sql.Substring(start + 1, end - start - 1).Replace("]]", "]", StringComparison.Ordinal);
            after = end + 1;
            return name.Length > 0;
        }

        if (!IsIdentStart(sql[start]))
        {
            return false;
        }

        var j = start + 1;
        while (j < sql.Length && IsIdentPart(sql[j]))
        {
            j++;
        }

        name = sql.Substring(start, j - start);
        after = j;
        return true;
    }

    private static int SkipWhitespace(string sql, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(sql[start]))
        {
            start++;
        }

        return start;
    }

    private static bool IsIdentStart(char value) =>
        char.IsLetter(value) || value is '_' or '@' or '#';

    private static bool IsIdentPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '@' or '#' or '$';

    /// <summary>
    /// Blanks comments and string literals so a column name that only appears
    /// inside EXEC(N'...') or COL_LENGTH(..., N'Column') is not a reference.
    /// EXEC compiles that string as its own batch.
    /// </summary>
    private static string MaskStringsAndComments(string sql)
    {
        var chars = sql.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '-' && i + 1 < chars.Length && chars[i + 1] == '-')
            {
                BlankUntil(chars, i, static (text, index) => index >= text.Length || text[index] == '\n');
                continue;
            }

            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? chars.Length : close + 2;
                for (var blank = i; blank < end; blank++)
                {
                    chars[blank] = ' ';
                }

                i = end - 1;
                continue;
            }

            var stringStart = chars[i] == '\''
                || ((chars[i] == 'N' || chars[i] == 'n') && i + 1 < chars.Length && chars[i + 1] == '\'');
            if (!stringStart)
            {
                continue;
            }

            var quote = chars[i] == '\'' ? i : i + 1;
            var cursor = quote + 1;
            while (cursor < chars.Length)
            {
                if (chars[cursor] == '\'' && cursor + 1 < chars.Length && chars[cursor + 1] == '\'')
                {
                    cursor += 2;
                    continue;
                }

                if (chars[cursor] == '\'')
                {
                    cursor++;
                    break;
                }

                cursor++;
            }

            for (var k = i; k < cursor; k++)
            {
                chars[k] = ' ';
            }

            i = cursor - 1;
        }

        return new string(chars);
    }

    private static void BlankUntil(char[] chars, int start, Func<char[], int, bool> done)
    {
        var i = start;
        while (i < chars.Length && !done(chars, i))
        {
            chars[i] = ' ';
            i++;
        }
    }

    private static readonly Regex AlterTable = new(
        @"\bALTER\s+TABLE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AddKeyword = new(
        @"\bADD\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record ColumnAddSpan(int Start, int End, List<string> Columns);

    private static List<StartupStatement> StartupStatements()
    {
        var services = Path.Combine(RepoRoot(), "backend", "PFOH.Api", "Services");
        var files = Directory.GetFiles(services, "*Schema.cs");
        Assert.NotEmpty(files);

        var statements = new List<StartupStatement>();
        foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var source = File.ReadAllText(file);
            var className = Regex.Match(source, @"class\s+(?<name>\w+Schema)\b").Groups["name"].Value;
            Assert.False(string.IsNullOrEmpty(className), file);
            if (!source.Contains("EnsureAsync", StringComparison.Ordinal))
            {
                continue;
            }

            var schemaType = typeof(HonoreeMergeAuditSchema).Assembly.GetType($"PFOH.Api.Services.{className}");
            Assert.NotNull(schemaType);

            var ensure = source.Split("EnsureAsync", 2)[1];
            Assert.True(
                ExpectedExecuteCalls.TryGetValue(className, out var expected),
                $"{className} has no expected ExecuteSqlRaw count.");
            var mentioned = Regex.Matches(ensure, @"\bExecuteSqlRaw(?:Async)?\b").Count;
            Assert.Equal(expected, mentioned);
            var calls = ExecuteCall.Matches(ensure);
            Assert.Equal(expected, calls.Count);
            Assert.True(
                Regex.Matches(ensure, @"ExecuteSqlRaw(?:Async)?\s*\(\s*\r?\n").Count == 0,
                $"{className} splits ExecuteSqlRaw across lines, which the startup SQL guard does not see.");

            foreach (Match call in calls)
            {
                var arg = call.Groups["arg"].Value.Trim();
                string passed;
                string intended;
                const string escape = "SchemaSql.ForExecuteSqlRaw(";
                if (arg.StartsWith(escape, StringComparison.Ordinal) && arg.EndsWith(")", StringComparison.Ordinal))
                {
                    intended = ResolveConst(schemaType!, arg[escape.Length..^1].Trim());
                    passed = SchemaSql.ForExecuteSqlRaw(intended);
                }
                else
                {
                    intended = ResolveConst(schemaType!, arg);
                    passed = intended;
                }

                statements.Add(new StartupStatement(Path.GetFileName(file) + " " + arg, passed, intended));
            }
        }

        return statements;
    }

    private static string ResolveConst(Type schemaType, string expression)
    {
        var parts = expression.Split('.');
        Type type;
        string name;
        if (parts.Length == 1)
        {
            type = schemaType;
            name = parts[0];
        }
        else if (parts.Length == 2)
        {
            type = schemaType.Assembly.GetType($"PFOH.Api.Services.{parts[0]}")
                ?? throw new InvalidOperationException($"Unknown schema type {parts[0]}.");
            name = parts[1];
        }
        else
        {
            throw new InvalidOperationException($"Cannot resolve startup SQL {expression}.");
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var value = field?.GetValue(null) as string;
        if (value is null)
        {
            throw new InvalidOperationException($"Startup SQL {expression} is not a string constant.");
        }

        return value;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }

    private sealed record StartupStatement(string Source, string PassedToExecuteSqlRaw, string IntendedSql);
}
