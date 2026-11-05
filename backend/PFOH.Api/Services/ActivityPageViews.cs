using System.Globalization;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Caching.Memory;
using PFOH.Api.Dtos;

namespace PFOH.Api.Services;

public sealed record ActivityPageViewRow(DateTimeOffset Day, string Name, int PageViews);

public interface IActivityPageViewClient
{
    Task<IReadOnlyList<ActivityPageViewRow>> QueryAsync(string resourceId, CancellationToken cancellationToken);
}

public static class ActivityPageViewQuery
{
    public const string SinceText = "2026-11-04T23:18:00Z";

    public static readonly DateTimeOffset Since = DateTimeOffset.Parse(
        SinceText,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public const string Kql =
        """
        pageViews
        | where timestamp >= datetime(2026-11-04T23:18:00Z)
        | summarize views = sum(itemCount) by day = bin(timestamp, 1d), name
        | project day, name, views
        | order by day asc
        """;

    public static TokenCredential CreateCredential()
        => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"))
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
}

public static class ActivityNameScrub
{
    private static readonly Regex GuidSegment = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PublicCode = new(
        @"^PFOH-[A-Za-z0-9]{4,12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Opaque = new(@"^[A-Za-z0-9_-]{20,}$", RegexOptions.Compiled);

    private static readonly Regex GuidInText = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CodeInText = new(
        @"PFOH-[A-Za-z0-9]{4,12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RouteTokenInText = new(
        @"(\/(?:reactivate|submission|receipt|pending)\/)[^/?#\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OpaqueInText = new(@"\/[A-Za-z0-9_-]{20,}", RegexOptions.Compiled);

    private static readonly Regex UrlInText = new(@"https?:\/\/[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> TokenRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "reactivate", "submission", "receipt", "pending"
    };

    public static string Scrub(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length == 0) return text;
        if (text.StartsWith('/') || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = RedactTarget(text);
        }

        text = UrlInText.Replace(text, match => RedactTarget(match.Value));
        text = RouteTokenInText.Replace(text, "$1{token}");
        text = GuidInText.Replace(text, "{token}");
        text = CodeInText.Replace(text, "{token}");
        text = OpaqueInText.Replace(text, "/{token}");
        return text;
    }

    public static bool IsAdminPage(string? value)
    {
        var path = PathOf(Scrub(value));
        return path.Equals("/admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase);
    }

    public static PageViewsSection Shape(IReadOnlyList<ActivityPageViewRow> rows)
    {
        var daily = new Dictionary<string, int>(StringComparer.Ordinal);
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var row in rows)
        {
            if (row.PageViews <= 0) continue;
            var name = PagePath(row.Name);
            if (string.IsNullOrWhiteSpace(name) || IsAdminPage(name)) continue;
            var day = row.Day.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            daily[day] = daily.GetValueOrDefault(day) + row.PageViews;
            pages[name] = pages.GetValueOrDefault(name) + row.PageViews;
            total += row.PageViews;
        }

        var dailyList = daily
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DailyPageViews(pair.Key, pair.Value))
            .ToList();
        var top = pages
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(pair => new TopPageViews(pair.Key, pair.Value))
            .ToList();
        return new PageViewsSection(total, dailyList, top);
    }

    private static string RedactTarget(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var origin = string.Empty;
        var path = raw;
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                origin = uri.GetLeftPart(UriPartial.Authority);
                path = uri.AbsolutePath;
            }
            else
            {
                path = raw.Split('?', '#')[0];
            }
        }
        else
        {
            path = raw.Split('?', '#')[0];
        }

        var parts = path.Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            var segment = parts[index];
            if (string.IsNullOrEmpty(segment)) continue;
            var decoded = Uri.UnescapeDataString(segment);
            var previous = index > 0 ? parts[index - 1] : string.Empty;
            if (IsTokenSegment(decoded, previous)) parts[index] = "{token}";
        }

        var redacted = string.Join('/', parts);
        return origin.Length == 0 ? redacted : origin + redacted;
    }

    private static bool IsTokenSegment(string segment, string previous)
        => TokenRoutes.Contains(previous)
            || GuidSegment.IsMatch(segment)
            || PublicCode.IsMatch(segment)
            || Opaque.IsMatch(segment);

    private static readonly Regex SchemeHost = new(@"^https?://[^/?#]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string PagePath(string? value)
    {
        var text = SchemeHost.Replace(Scrub(value), "");
        return PathOf(text.Length == 0 ? "/" : text);
    }

    private static string PathOf(string value)
    {
        var path = value;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
        }

        path = path.Split('?', '#')[0];
        if (!path.StartsWith('/')) path = "/" + path;
        if (path.Length > 1) path = path.TrimEnd('/');
        return path.Length == 0 ? "/" : path;
    }
}

public sealed class AzureActivityPageViewClient : IActivityPageViewClient
{
    private readonly Lazy<LogsQueryClient> _client = new(CreateClient);

    public async Task<IReadOnlyList<ActivityPageViewRow>> QueryAsync(string resourceId, CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow;
        if (end <= ActivityPageViewQuery.Since) end = ActivityPageViewQuery.Since.AddMinutes(1);
        var response = await _client.Value.QueryResourceAsync(
            new ResourceIdentifier(resourceId),
            ActivityPageViewQuery.Kql,
            new QueryTimeRange(ActivityPageViewQuery.Since, end),
            cancellationToken: cancellationToken);
        var table = response.Value.Table;
        if (table.Rows.Count == 0) return Array.Empty<ActivityPageViewRow>();

        var rows = new List<ActivityPageViewRow>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            rows.Add(new ActivityPageViewRow(ReadDay(row), ReadName(row), ReadViews(row)));
        }

        return rows;
    }

    private static LogsQueryClient CreateClient()
    {
        var options = new LogsQueryClientOptions();
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(8);
        return new LogsQueryClient(ActivityPageViewQuery.CreateCredential(), options);
    }

    private static DateTimeOffset ReadDay(LogsTableRow row)
    {
        var value = row[0];
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? ActivityPageViewQuery.SinceText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        };
    }

    private static string ReadName(LogsTableRow row) => row.GetString(1) ?? string.Empty;

    private static int ReadViews(LogsTableRow row)
    {
        var value = row[2];
        return value switch
        {
            int number => number,
            long number => (int)number,
            double number => (int)number,
            decimal number => (int)number,
            _ => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
        };
    }
}

public sealed class ActivityPageViews
{
    public const string CacheKey = "pfoh.activity.page-views";

    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    private readonly IActivityPageViewClient _client;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ActivityPageViews> _logger;
    private int _warned;

    public ActivityPageViews(
        IActivityPageViewClient client,
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<ActivityPageViews> logger)
    {
        _client = client;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PageViewsSection?> ReadAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey, out CachedViews? cached) && cached is not null)
        {
            return cached.Value;
        }

        var resourceId = _configuration["ActivityTelemetry:AppInsightsResourceId"];
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            WarnOnce(null, "ActivityTelemetry:AppInsightsResourceId is empty. Page views are hidden.");
            Remember(null);
            return null;
        }

        try
        {
            var rows = await _client.QueryAsync(resourceId, cancellationToken);
            var shaped = ActivityNameScrub.Shape(rows);
            Remember(shaped);
            return shaped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WarnOnce(ex, "Activity page views could not be loaded.");
            Remember(null);
            return null;
        }
    }

    private void Remember(PageViewsSection? value)
    {
        _cache.Set(CacheKey, new CachedViews { Value = value }, CacheFor);
    }

    private void WarnOnce(Exception? exception, string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 1) return;
        if (exception is null) _logger.LogWarning(message);
        else _logger.LogWarning(exception, message);
    }

    private sealed class CachedViews
    {
        public PageViewsSection? Value { get; init; }
    }
}
