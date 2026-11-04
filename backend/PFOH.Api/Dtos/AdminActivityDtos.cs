namespace PFOH.Api.Dtos;

public sealed record AdminActivityResponse(
    DateTime AsOfUtc,
    AccountCounts Accounts,
    WindowedStatuses ChangeRequests,
    WindowedStatuses FlagClaims,
    ReceiptCounts Receipts,
    IReadOnlyList<DailyActivityCount> Daily);

public sealed record AccountCounts(int Last7Days, int Last30Days, int AllTime);

public sealed record WindowedStatuses(StatusWindow Last7Days, StatusWindow Last30Days);

public sealed record StatusWindow(int Total, IReadOnlyList<StatusCount> ByStatus);

public sealed record StatusCount(string Status, int Count);

public sealed record ReceiptCounts(int Last7Days, int Last30Days);

public sealed record DailyActivityCount(
    string Date,
    int Accounts,
    int ChangeRequests,
    int FlagClaims,
    int Receipts);
