using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;

namespace PFOH.Api.Services;

/// <summary>
/// UTC calendar windows. Last 7 days is today and the six days before it.
/// Last 30 days is today and the 29 days before it. The end is exclusive.
/// </summary>
public static class AdminActivityWindows
{
    public static (DateTime Start7, DateTime Start30, DateTime End) For(DateTime utcNow)
    {
        var utc = utcNow.Kind == DateTimeKind.Local
            ? utcNow.ToUniversalTime()
            : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var today = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        return (today.AddDays(-6), today.AddDays(-29), today.AddDays(1));
    }
}

public static class AdminActivityQuery
{
    public static async Task<AdminActivityResponse> ReadAsync(PfohDbContext db, DateTime utcNow, CancellationToken ct)
    {
        var (start7, start30, end) = AdminActivityWindows.For(utcNow);

        var accountStampsAll = await db.UserProfiles
            .AsNoTracking()
            .Select(row => row.CreatedUtc)
            .ToListAsync(ct);
        var accountAllTime = accountStampsAll.Count;

        var changeRows = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(row => row.CreatedUtc >= start30 && row.CreatedUtc < end)
            .Select(row => new { row.CreatedUtc, row.RequestStatus })
            .ToListAsync(ct);
        var changeStamps = changeRows
            .Select(row => new ActivityStamp { At = row.CreatedUtc, Status = row.RequestStatus })
            .ToList();

        var claimRows = await db.FlagClaims
            .AsNoTracking()
            .Where(row => row.CreatedUtc >= start30 && row.CreatedUtc < end)
            .Select(row => new { row.CreatedUtc, row.ClaimStatus })
            .ToListAsync(ct);
        var claimStamps = claimRows
            .Select(row => new ActivityStamp { At = row.CreatedUtc, Status = row.ClaimStatus })
            .ToList();

        var receiptStamps = await db.SubmissionReceipts
            .AsNoTracking()
            .Where(row => row.CreatedUtc >= start30 && row.CreatedUtc < end)
            .Select(row => row.CreatedUtc)
            .ToListAsync(ct);

        var accountsByDay = CountByDay(accountStampsAll, start30, end);
        var changesByDay = CountByDay(changeStamps.Select(row => row.At), start30, end);
        var claimsByDay = CountByDay(claimStamps.Select(row => row.At), start30, end);
        var receiptsByDay = CountByDay(receiptStamps, start30, end);

        var daily = new List<DailyActivityCount>(30);
        for (var day = start30; day < end; day = day.AddDays(1))
        {
            var key = day.Date;
            daily.Add(new DailyActivityCount(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                accountsByDay.GetValueOrDefault(key),
                changesByDay.GetValueOrDefault(key),
                claimsByDay.GetValueOrDefault(key),
                receiptsByDay.GetValueOrDefault(key)));
        }

        return new AdminActivityResponse(
            DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            new AccountCounts(
                CountInWindow(accountStampsAll, start7, end),
                CountInWindow(accountStampsAll, start30, end),
                accountAllTime),
            new WindowedStatuses(StatusWindowFor(changeStamps, start7, end), StatusWindowFor(changeStamps, start30, end)),
            new WindowedStatuses(StatusWindowFor(claimStamps, start7, end), StatusWindowFor(claimStamps, start30, end)),
            new ReceiptCounts(CountInWindow(receiptStamps, start7, end), receiptStamps.Count),
            daily,
            RegistrationSeries(accountStampsAll, end),
            null);
    }

    private static List<RegistrationDay> RegistrationSeries(IReadOnlyList<DateTime> stamps, DateTime end)
    {
        DateTime? first = null;
        foreach (var stamp in stamps)
        {
            if (stamp >= end) continue;
            var day = stamp.Date;
            if (first is null || day < first) first = day;
        }

        if (first is null) return new List<RegistrationDay>();

        var counts = CountByDay(stamps, first.Value, end);
        var series = new List<RegistrationDay>();
        for (var day = DateTime.SpecifyKind(first.Value, DateTimeKind.Utc); day < end; day = day.AddDays(1))
        {
            series.Add(new RegistrationDay(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                counts.GetValueOrDefault(day.Date)));
        }

        return series;
    }

    private static int CountInWindow(IEnumerable<DateTime> instants, DateTime start, DateTime end)
    {
        var count = 0;
        foreach (var instant in instants)
        {
            if (instant >= start && instant < end)
            {
                count++;
            }
        }

        return count;
    }

    private static Dictionary<DateTime, int> CountByDay(IEnumerable<DateTime> instants, DateTime start, DateTime end)
    {
        var counts = new Dictionary<DateTime, int>();
        foreach (var instant in instants)
        {
            if (instant < start || instant >= end)
            {
                continue;
            }

            var day = instant.Date;
            counts[day] = counts.GetValueOrDefault(day) + 1;
        }

        return counts;
    }

    private static StatusWindow StatusWindowFor(IReadOnlyList<ActivityStamp> rows, DateTime start, DateTime end)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.At < start || row.At >= end)
            {
                continue;
            }

            var status = row.Status ?? string.Empty;
            counts[status] = counts.GetValueOrDefault(status) + 1;
        }

        var byStatus = counts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new StatusCount(pair.Key, pair.Value))
            .ToList();
        return new StatusWindow(byStatus.Sum(item => item.Count), byStatus);
    }

    private sealed class ActivityStamp
    {
        public DateTime At { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
