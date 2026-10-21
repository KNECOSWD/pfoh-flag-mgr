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

        Assert.Equal(2, statements.Count);
        Assert.All(statements, statement =>
        {
            var formatted = string.Format(statement.PassedToExecuteSqlRaw, Array.Empty<object>());
            Assert.Equal(statement.IntendedSql, formatted);
            Assert.Contains("DEFAULT N'{}'", formatted, StringComparison.Ordinal);
        });

        Assert.Contains("DEFAULT N'{}'", HonoreeMergeAuditSchema.CreateSql, StringComparison.Ordinal);
        Assert.Contains("DEFAULT N'{}'", HonoreeMergeAuditSchema.AddColumnSql, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() =>
            string.Format(HonoreeMergeAuditSchema.CreateSql, Array.Empty<object>()));
        Assert.Throws<FormatException>(() =>
            string.Format(HonoreeMergeAuditSchema.AddColumnSql, Array.Empty<object>()));
    }

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
            var calls = ExecuteCall.Matches(ensure);
            Assert.NotEmpty(calls);

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
