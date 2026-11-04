namespace PFOH.Api.Dtos;

public sealed record AdminActivityResponse(
    DateTime AsOfUtc,
    AccountCounts Accounts,
    WindowedStatuses ChangeRequests,
    WindowedStatuses FlagClaims,
    ReceiptCounts Receipts,
    IReadOnlyList<DailyActivityCount> Daily,
    IReadOnlyList<RegistrationDay> Registrations,
    PageViewsSection? PageViews);

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

public sealed record RegistrationDay(string Date, int Count);

public sealed record PageViewsSection(
    int Total,
    IReadOnlyList<DailyPageViews> Daily,
    IReadOnlyList<TopPageViews> TopPages);

public sealed record DailyPageViews(string Date, int PageViews);

public sealed record TopPageViews(string Name, int PageViews);
