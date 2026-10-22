namespace PFOH.Snapshot;

/// <summary>
/// Single allow-list for the nightly snapshot. Counts are taken for every user table.
/// Row values are emitted only for the tables and columns listed here, plus the
/// derived Honoree presence flags. Graph user reads use <see cref="DirectoryUserSelect"/>
/// and nothing else. <see cref="SignInActivitySelect"/> is the only extra select,
/// and it exists only to fill the signInActivity array.
/// </summary>
public static class SnapshotFieldSpec
{
    public const int StringMaxLength = 200;

    public const string PhoneExtension = "extension_a29a0dc74c9b4cef9f783444e62907d3_Phone";
    public const string OrganizationExtension = "extension_a29a0dc74c9b4cef9f783444e62907d3_Organization";

    public static readonly string[] DirectoryUserFields =
    [
        "id",
        "displayName",
        "mail",
        "otherMails",
        "identities",
        "userPrincipalName",
        "createdDateTime",
        "accountEnabled",
        PhoneExtension,
        OrganizationExtension
    ];

    public static readonly string[] IdentityFields = ["signInType", "issuerAssignedId"];

    public static readonly string[] SignInActivityFields = ["lastSignInDateTime", "lastSuccessfulSignInDateTime"];

    /// <summary>Joined $select for directory users. Does not include signInActivity.</summary>
    public static string DirectoryUserSelect { get; } = string.Join(',', DirectoryUserFields);

    /// <summary>
    /// Separate $select. AuditLog.Read.All is required only because Graph hides signInActivity without it.
    /// </summary>
    public const string SignInActivitySelect = "id,signInActivity";

    public static readonly IReadOnlyDictionary<string, string[]> TableColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["dbo.FlagClaims"] =
        [
            "Id", "FlagGridId", "HonoreeId", "ExternalUserObjectId", "ExternalUserEmail", "ExternalUserName",
            "ClaimStatus", "CreatedUtc", "SubmittedUtc", "ApprovedUtc", "RejectedUtc", "ApprovedBy", "RejectedBy"
        ],
        ["dbo.HonoreeClaims"] = ["Id", "HonoreeId", "Email", "ClaimStatus"],
        ["dbo.HonoreeChangeRequests"] =
        [
            "Id", "FlagClaimId", "FlagGridId", "HonoreeId", "RequestStatus",
            "CreatedUtc", "SubmittedUtc", "ReviewedUtc", "ReviewedBy"
        ],
        ["dbo.Honorees"] =
        [
            "Id", "FlagGridId", "Rank", "FirstName", "MiddleName", "LastName", "Suffix",
            "IsActive", "CreatedBy", "CreatedDate", "DeletedDate", "ServiceBranchId"
        ],
        ["dbo.FlagGrids"] = ["Id", "HonoreeId", "FlagGridName", "Reserved", "CreatedBy", "CreatedDate", "DeletedDate"],
        ["dbo.ServiceBranches"] = ["Id", "ServiceBranchName", "DeletedDate"],
        ["dbo.UserProfiles"] = ["Id", "OwnerObjectId", "Email", "CreatedUtc", "Phone", "Organization"],
        ["dbo.CardPrintEvents"] = ["Id", "CardPrintBatchId", "PrintedUtc", "CardCount"]
    };

    public static readonly string[] HonoreeDerivedFlags = ["HasPhoto", "HasDescription", "HasAwards", "HasConflictsServed"];

    public static readonly string[] HonoreeSourceColumns = ["PhotoFileName", "Description", "Awards", "ConflictsServed"];

    public static bool IsListedTable(string key) => TableColumns.ContainsKey(key);

    public static IReadOnlyList<string> OutputColumns(string key)
    {
        if (!TableColumns.TryGetValue(key, out var columns))
        {
            return Array.Empty<string>();
        }

        if (key == "dbo.Honorees")
        {
            return columns.Concat(HonoreeDerivedFlags).ToArray();
        }

        return columns;
    }

    public static IReadOnlyList<string> ReadColumns(string key)
    {
        if (!TableColumns.TryGetValue(key, out var columns))
        {
            return Array.Empty<string>();
        }

        if (key == "dbo.Honorees")
        {
            return columns.Concat(HonoreeSourceColumns).ToArray();
        }

        return columns;
    }
}
