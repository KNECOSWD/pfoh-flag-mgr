using System.Text.Json.Serialization;

namespace PFOH.Snapshot;

public sealed class SnapshotColumn
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string ColumnName { get; init; }
    public required string TypeName { get; init; }
    public int? MaxLength { get; init; }
    public required int ColumnId { get; init; }

    public string TableKey => SchemaName + "." + TableName;
}

public interface IUserTableSource
{
    Task<IReadOnlyList<SnapshotColumn>> GetColumnsAsync(CancellationToken ct);

    Task<long> GetCountAsync(string schema, string table, CancellationToken ct);

    /// <summary>Reads only the requested columns. Callers must not ask for unlisted free-text columns except Honoree presence sources.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetRowsAsync(
        string schema,
        string table,
        IReadOnlyList<string> columns,
        CancellationToken ct);
}

public interface IDirectorySource
{
    Task<IReadOnlyList<DirectoryUserRecord>> ReadAsync(CancellationToken ct);
}

public interface ISnapshotSink
{
    Task WriteAsync(string blobName, byte[] utf8Json, CancellationToken ct);
}

public sealed class DirectoryUserRecord
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Mail { get; init; }
    public IReadOnlyList<string> OtherMails { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DirectoryIdentityRecord> Identities { get; init; } = Array.Empty<DirectoryIdentityRecord>();
    public string? UserPrincipalName { get; init; }
    public string? CreatedDateTime { get; init; }
    public bool? AccountEnabled { get; init; }
    public string? Phone { get; init; }
    public string? Organization { get; init; }
    public DirectorySignInRecord? SignInActivity { get; init; }
}

public sealed class DirectoryIdentityRecord
{
    public string? SignInType { get; init; }
    public string? IssuerAssignedId { get; init; }
}

public sealed class DirectorySignInRecord
{
    public string? LastSignInDateTime { get; init; }
    public string? LastSuccessfulSignInDateTime { get; init; }
}

public sealed class DbDumpDocument
{
    [JsonPropertyName("pulledAt")]
    public required string PulledAt { get; init; }

    [JsonPropertyName("columns")]
    public required IReadOnlyList<ColumnDocument> Columns { get; init; }

    [JsonPropertyName("tables")]
    public required SortedDictionary<string, TableDocument> Tables { get; init; }
}

public sealed class ColumnDocument
{
    [JsonPropertyName("SchemaName")]
    public required string SchemaName { get; init; }

    [JsonPropertyName("TableName")]
    public required string TableName { get; init; }

    [JsonPropertyName("ColumnName")]
    public required string ColumnName { get; init; }

    [JsonPropertyName("TypeName")]
    public required string TypeName { get; init; }

    [JsonPropertyName("MaxLength")]
    public int? MaxLength { get; init; }

    [JsonPropertyName("ColumnId")]
    public required int ColumnId { get; init; }
}

public sealed class TableDocument
{
    [JsonPropertyName("count")]
    public required long Count { get; init; }

    [JsonPropertyName("rows")]
    public required IReadOnlyList<SortedDictionary<string, object?>> Rows { get; init; }
}

public sealed class DirectorySnapshotDocument
{
    [JsonPropertyName("pulledAt")]
    public required string PulledAt { get; init; }

    [JsonPropertyName("users")]
    public required IReadOnlyList<DirectoryUserDocument> Users { get; init; }

    [JsonPropertyName("signInActivity")]
    public required IReadOnlyList<SignInActivityDocument> SignInActivity { get; init; }
}

public sealed class DirectoryUserDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("mail")]
    public string? Mail { get; init; }

    [JsonPropertyName("otherMails")]
    public required IReadOnlyList<string> OtherMails { get; init; }

    [JsonPropertyName("identities")]
    public required IReadOnlyList<IdentityDocument> Identities { get; init; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; init; }

    [JsonPropertyName("createdDateTime")]
    public string? CreatedDateTime { get; init; }

    [JsonPropertyName("accountEnabled")]
    public bool? AccountEnabled { get; init; }

    [JsonPropertyName(SnapshotFieldSpec.PhoneExtension)]
    public string? Phone { get; init; }

    [JsonPropertyName(SnapshotFieldSpec.OrganizationExtension)]
    public string? Organization { get; init; }
}

public sealed class IdentityDocument
{
    [JsonPropertyName("signInType")]
    public string? SignInType { get; init; }

    [JsonPropertyName("issuerAssignedId")]
    public string? IssuerAssignedId { get; init; }
}

public sealed class SignInActivityDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("signInActivity")]
    public SignInActivityValueDocument? SignInActivity { get; init; }
}

public sealed class SignInActivityValueDocument
{
    [JsonPropertyName("lastSignInDateTime")]
    public string? LastSignInDateTime { get; init; }

    [JsonPropertyName("lastSuccessfulSignInDateTime")]
    public string? LastSuccessfulSignInDateTime { get; init; }
}

public sealed record SnapshotRunCounts(
    int TableCount,
    long CountedRows,
    int EmittedRows,
    int DirectoryUsers,
    int SignInActivityRows,
    int DbBytes,
    int DirectoryBytes);
