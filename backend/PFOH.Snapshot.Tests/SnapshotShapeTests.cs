using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PFOH.Snapshot;
using Xunit;

namespace PFOH.Snapshot.Tests;

public class SnapshotShapeTests
{
    private const string BioSentinel = "BIO_TEXT_SHOULD_NOT_LEAK";
    private const string PhotoSentinel = "photos/secret-file.jpg";
    private const string AddressSentinel = "123 Secret Street";
    private const string CommentSentinel = "COMMENT_SHOULD_NOT_LEAK";
    private const string PrintedBySentinel = "printed-by-person";

    [Fact]
    public async Task Dump_emits_counts_for_every_table_and_rows_only_for_allow_listed_columns()
    {
        var source = new FakeTables();
        var pulledAt = new DateTimeOffset(2026, 10, 22, 1, 45, 0, TimeSpan.Zero);
        var dump = await DbDumpBuilder.BuildAsync(source, pulledAt, CancellationToken.None);
        var json = Encoding.UTF8.GetString(SnapshotJson.ToUtf8(dump));

        Assert.Equal("2026-10-22T01:45:00Z", dump.PulledAt);
        Assert.Equal(3, dump.Tables["dbo.Sponsors"].Count);
        Assert.Empty(dump.Tables["dbo.Sponsors"].Rows);
        Assert.Equal(2, dump.Tables["dbo.Honorees"].Count);
        Assert.Equal(2, dump.Tables["dbo.Honorees"].Rows.Count);
        Assert.Equal(1, dump.Tables["dbo.CardPrintEvents"].Count);
        Assert.DoesNotContain("dbo.Sponsors", source.RowReads);
        Assert.Contains("dbo.Honorees", source.RowReads);
        Assert.Contains("dbo.CardPrintEvents", source.RowReads);

        Assert.DoesNotContain(BioSentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(PhotoSentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(AddressSentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(CommentSentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(PrintedBySentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Description\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PhotoFileName\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Awards\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ConflictsServed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Comment\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PrintedBy\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"City\"", json, StringComparison.Ordinal);

        var honoreeKeys = dump.Tables["dbo.Honorees"].Rows
            .SelectMany(row => row.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var allowed = SnapshotFieldSpec.OutputColumns("dbo.Honorees").ToHashSet(StringComparer.Ordinal);
        Assert.Subset(allowed, honoreeKeys);
        Assert.Contains("HasPhoto", honoreeKeys);
        Assert.Equal(new string(Enumerable.Repeat('N', 200).ToArray()), dump.Tables["dbo.Honorees"].Rows[0]["FirstName"]);

        Assert.DoesNotContain(dump.Columns, column => column.ColumnName is "Description" or "Comment" or "PrintedBy" or "City" or "PhotoFileName");
        Assert.DoesNotContain(dump.Tables["dbo.CardPrintEvents"].Rows[0].Keys, key => key is "Comment" or "PrintedBy");

        foreach (var (table, document) in dump.Tables)
        {
            var allowedKeys = SnapshotFieldSpec.OutputColumns(table).ToHashSet(StringComparer.Ordinal);
            foreach (var row in document.Rows)
            {
                Assert.Subset(allowedKeys, row.Keys.ToHashSet(StringComparer.Ordinal));
            }
        }

        foreach (var column in dump.Columns)
        {
            var allowedNames = SnapshotFieldSpec.OutputColumns(column.SchemaName + "." + column.TableName);
            Assert.Contains(column.ColumnName, allowedNames);
        }
    }

    [Fact]
    public async Task Honoree_presence_flags_follow_the_spec_rules()
    {
        var source = new FakeTables();
        var dump = await DbDumpBuilder.BuildAsync(source, DateTimeOffset.Parse("2026-10-22T01:45:00Z"), CancellationToken.None);
        var rows = dump.Tables["dbo.Honorees"].Rows;
        Assert.True((bool)rows[0]["HasPhoto"]!);
        Assert.True((bool)rows[0]["HasDescription"]!);
        Assert.False((bool)rows[0]["HasAwards"]!);
        Assert.False((bool)rows[0]["HasConflictsServed"]!);
        Assert.False((bool)rows[1]["HasPhoto"]!);
        Assert.False((bool)rows[1]["HasDescription"]!);
        Assert.False((bool)rows[1]["HasAwards"]!);
        Assert.False((bool)rows[1]["HasConflictsServed"]!);
    }

    [Theory]
    [InlineData("no information provided", false)]
    [InlineData("N/A", false)]
    [InlineData(" None ", false)]
    [InlineData("Unknown", false)]
    [InlineData("   ", false)]
    [InlineData("Navy Cross", true)]
    public void Meaningful_text_matches_the_empty_list(string value, bool expected)
    {
        Assert.Equal(expected, HonoreePresence.HasMeaningfulText(value));
    }

    [Theory]
    [InlineData("silhouette.png", false)]
    [InlineData(" Silhouette-default ", false)]
    [InlineData("", false)]
    [InlineData("honoree-12.jpg", true)]
    public void Photo_flag_rejects_blank_and_silhouette_names(string value, bool expected)
    {
        Assert.Equal(expected, HonoreePresence.HasPhoto(value));
    }

    [Fact]
    public void Binary_values_are_skipped_and_strings_truncate_at_200()
    {
        Assert.IsType<SnapshotProjection.Skip>(SnapshotProjection.Normalize(new byte[] { 1, 2, 3 }, "varbinary"));
        Assert.IsType<SnapshotProjection.Skip>(SnapshotProjection.Normalize("abc", "image"));
        Assert.Equal(200, ((string)SnapshotProjection.Normalize(new string('A', 240), "nvarchar")!).Length);
    }

    [Fact]
    public void Json_is_utf8_without_a_bom()
    {
        var bytes = SnapshotJson.ToUtf8(new DbDumpDocument
        {
            PulledAt = "2026-10-22T01:45:00Z",
            Columns = Array.Empty<ColumnDocument>(),
            Tables = new SortedDictionary<string, TableDocument>()
        });
        Assert.True(bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("{", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.TryGetProperty("pulledAt", out _));
        Assert.True(document.RootElement.TryGetProperty("columns", out _));
        Assert.True(document.RootElement.TryGetProperty("tables", out _));
    }

    [Fact]
    public void Directory_snapshot_keeps_only_listed_fields()
    {
        var document = DirectorySnapshotBuilder.Build(
            DateTimeOffset.Parse("2026-10-22T01:45:00Z"),
            [
                new DirectoryUserRecord
                {
                    Id = "user-1",
                    DisplayName = "Synthetic User",
                    Mail = "user@example.com",
                    OtherMails = ["other@example.com"],
                    Identities =
                    [
                        new DirectoryIdentityRecord { SignInType = "emailAddress", IssuerAssignedId = "user@example.com" }
                    ],
                    UserPrincipalName = "user@example.com",
                    CreatedDateTime = "2026-01-01T00:00:00Z",
                    AccountEnabled = true,
                    Phone = "+12145550100",
                    Organization = "Example Org",
                    SignInActivity = new DirectorySignInRecord
                    {
                        LastSignInDateTime = "2026-10-21T18:00:00Z",
                        LastSuccessfulSignInDateTime = "2026-10-21T18:00:00Z"
                    }
                }
            ]);
        var json = Encoding.UTF8.GetString(SnapshotJson.ToUtf8(document));
        using var parsed = JsonDocument.Parse(json);
        var user = parsed.RootElement.GetProperty("users")[0];
        var names = user.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SnapshotFieldSpec.DirectoryUserFields.ToHashSet(StringComparer.Ordinal), names);
        var identity = user.GetProperty("identities")[0];
        Assert.Equal(
            SnapshotFieldSpec.IdentityFields.ToHashSet(StringComparer.Ordinal),
            identity.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));
        Assert.DoesNotContain("lastNonInteractiveSignInDateTime", json, StringComparison.Ordinal);
        Assert.DoesNotContain("signInActivity", user.GetRawText(), StringComparison.Ordinal);
        var activity = parsed.RootElement.GetProperty("signInActivity")[0].GetProperty("signInActivity");
        Assert.Equal(
            SnapshotFieldSpec.SignInActivityFields.ToHashSet(StringComparer.Ordinal),
            activity.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));
        Assert.Equal("id,displayName,mail,otherMails,identities,userPrincipalName,createdDateTime,accountEnabled,extension_a29a0dc74c9b4cef9f783444e62907d3_Phone,extension_a29a0dc74c9b4cef9f783444e62907d3_Organization", SnapshotFieldSpec.DirectoryUserSelect);
        Assert.DoesNotContain("signInActivity", SnapshotFieldSpec.DirectoryUserSelect, StringComparison.Ordinal);
        Assert.Equal("id,signInActivity", SnapshotFieldSpec.SignInActivitySelect);
        Assert.False(json.StartsWith("\uFEFF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_writes_both_files_and_logs_counts_only()
    {
        var sink = new MemorySink();
        var logs = new List<string>();
        var job = new SnapshotJob(
            new FakeTables(),
            new FakeDirectory(),
            sink,
            new FixedTime(),
            new ListLogger(logs));
        var counts = await job.ExecuteAsync(CancellationToken.None);
        Assert.Equal(2, sink.Blobs.Count);
        Assert.True(sink.Blobs.ContainsKey("2026-10-22/db-dump.json"));
        Assert.True(sink.Blobs.ContainsKey("2026-10-22/directory-users.json"));
        Assert.NotEqual(0xEF, sink.Blobs["2026-10-22/db-dump.json"][0]);
        Assert.Equal(1, counts.DirectoryUsers);
        var logText = string.Join('\n', logs);
        Assert.DoesNotContain(BioSentinel, logText, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", logText, StringComparison.Ordinal);
        Assert.Contains("counted rows", logText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_identifiers_are_quoted_and_rejected_when_unexpected()
    {
        Assert.Equal("[OwnerObjectId]", SqlUserTableSource.Quote("OwnerObjectId"));
        Assert.Throws<InvalidOperationException>(() => SqlUserTableSource.Quote("Name; DROP"));
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 22, 1, 45, 0, TimeSpan.Zero);
    }

    private sealed class MemorySink : ISnapshotSink
    {
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

        public Task WriteAsync(string blobName, byte[] utf8Json, CancellationToken ct)
        {
            Blobs[blobName] = utf8Json;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectory : IDirectorySource
    {
        public Task<IReadOnlyList<DirectoryUserRecord>> ReadAsync(CancellationToken ct)
        {
            IReadOnlyList<DirectoryUserRecord> users =
            [
                new DirectoryUserRecord
                {
                    Id = "user-1",
                    Mail = "user@example.com",
                    DisplayName = "Synthetic User"
                }
            ];
            return Task.FromResult(users);
        }
    }

    private sealed class ListLogger(List<string> lines) : ILogger<SnapshotJob>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception));
        }
    }

    private sealed class FakeTables : IUserTableSource
    {
        public List<string> RowReads { get; } = new();

        public Task<IReadOnlyList<SnapshotColumn>> GetColumnsAsync(CancellationToken ct)
        {
            IReadOnlyList<SnapshotColumn> columns =
            [
                Col("dbo", "Honorees", "Id", "int", null, 1),
                Col("dbo", "Honorees", "FirstName", "nvarchar", 200, 2),
                Col("dbo", "Honorees", "FlagGridId", "int", null, 3),
                Col("dbo", "Honorees", "Rank", "nvarchar", 50, 4),
                Col("dbo", "Honorees", "MiddleName", "nvarchar", 100, 5),
                Col("dbo", "Honorees", "LastName", "nvarchar", 100, 6),
                Col("dbo", "Honorees", "Suffix", "nvarchar", 20, 7),
                Col("dbo", "Honorees", "IsActive", "bit", null, 8),
                Col("dbo", "Honorees", "CreatedBy", "nvarchar", 100, 9),
                Col("dbo", "Honorees", "CreatedDate", "datetime2", null, 10),
                Col("dbo", "Honorees", "DeletedDate", "datetime2", null, 11),
                Col("dbo", "Honorees", "ServiceBranchId", "int", null, 12),
                Col("dbo", "Honorees", "PhotoFileName", "nvarchar", 260, 13),
                Col("dbo", "Honorees", "Description", "nvarchar", -1, 14),
                Col("dbo", "Honorees", "Awards", "nvarchar", -1, 15),
                Col("dbo", "Honorees", "ConflictsServed", "nvarchar", -1, 16),
                Col("dbo", "CardPrintEvents", "Id", "int", null, 1),
                Col("dbo", "CardPrintEvents", "CardPrintBatchId", "uniqueidentifier", null, 2),
                Col("dbo", "CardPrintEvents", "PrintedUtc", "datetime2", null, 3),
                Col("dbo", "CardPrintEvents", "CardCount", "int", null, 4),
                Col("dbo", "CardPrintEvents", "PrintedBy", "nvarchar", 255, 5),
                Col("dbo", "CardPrintEvents", "Comment", "nvarchar", 500, 6),
                Col("dbo", "Sponsors", "Id", "int", null, 1),
                Col("dbo", "Sponsors", "City", "nvarchar", 100, 2)
            ];
            return Task.FromResult(columns);
        }

        public Task<long> GetCountAsync(string schema, string table, CancellationToken ct)
        {
            var key = schema + "." + table;
            long count = key switch
            {
                "dbo.Honorees" => 2,
                "dbo.CardPrintEvents" => 1,
                "dbo.Sponsors" => 3,
                _ => 0
            };
            return Task.FromResult(count);
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetRowsAsync(
            string schema,
            string table,
            IReadOnlyList<string> columns,
            CancellationToken ct)
        {
            var key = schema + "." + table;
            RowReads.Add(key);
            if (key == "dbo.Honorees")
            {
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                [
                    Row(columns, new Dictionary<string, object?>
                    {
                        ["Id"] = 1,
                        ["FirstName"] = new string('N', 240),
                        ["LastName"] = "Example",
                        ["IsActive"] = true,
                        ["PhotoFileName"] = PhotoSentinel,
                        ["Description"] = BioSentinel,
                        ["Awards"] = "none",
                        ["ConflictsServed"] = "  "
                    }),
                    Row(columns, new Dictionary<string, object?>
                    {
                        ["Id"] = 2,
                        ["FirstName"] = "Pat",
                        ["PhotoFileName"] = "silhouette.png",
                        ["Description"] = "N/A",
                        ["Awards"] = "Unknown",
                        ["ConflictsServed"] = "no information provided"
                    })
                ];
                return Task.FromResult(rows);
            }

            if (key == "dbo.CardPrintEvents")
            {
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                [
                    Row(columns, new Dictionary<string, object?>
                    {
                        ["Id"] = 7,
                        ["CardCount"] = 2,
                        ["PrintedBy"] = PrintedBySentinel,
                        ["Comment"] = CommentSentinel
                    })
                ];
                return Task.FromResult(rows);
            }

            if (key == "dbo.Sponsors")
            {
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                [
                    new Dictionary<string, object?> { ["City"] = AddressSentinel }
                ];
                return Task.FromResult(rows);
            }

            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        private static Dictionary<string, object?> Row(IReadOnlyList<string> columns, Dictionary<string, object?> values)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                row[column] = values.TryGetValue(column, out var value) ? value : null;
            }

            foreach (var extra in values)
            {
                row[extra.Key] = extra.Value;
            }

            return row;
        }

        private static SnapshotColumn Col(string schema, string table, string name, string type, int? length, int id)
        {
            return new SnapshotColumn
            {
                SchemaName = schema,
                TableName = table,
                ColumnName = name,
                TypeName = type,
                MaxLength = length,
                ColumnId = id
            };
        }
    }
}
