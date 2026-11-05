using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using PFOH.Api.Dtos;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class ActivityPageViewsTests
{
    private const string ResourceId = "/subscriptions/66be41e4-8fd8-4302-b155-6ce0e508675a/resourceGroups/PLANO_FLAGS_OF_HONOR/providers/microsoft.insights/components/pfoh-flag-manager-app";

    [Fact]
    public void Appsettings_names_the_activity_component()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "PFOH.Api", "appsettings.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var json = File.ReadAllText(Path.Combine(dir.FullName, "backend", "PFOH.Api", "appsettings.json"));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(ResourceId, document.RootElement.GetProperty("ActivityTelemetry").GetProperty("AppInsightsResourceId").GetString());
    }

    [Fact]
    public void Query_counts_page_views_and_does_not_ask_for_visitors()
    {
        Assert.Contains("sum(itemCount)", ActivityPageViewQuery.Kql, StringComparison.Ordinal);
        Assert.Contains("datetime(2026-11-04T23:18:00Z)", ActivityPageViewQuery.Kql, StringComparison.Ordinal);
        Assert.DoesNotContain("user_Id", ActivityPageViewQuery.Kql, StringComparison.Ordinal);
        Assert.DoesNotContain("dcount", ActivityPageViewQuery.Kql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TimeSpan.FromMinutes(15), ActivityPageViews.CacheFor);
    }

    [Fact]
    public async Task Fake_client_shapes_scrubbed_counts_and_caches_the_hit()
    {
        var client = new FakeClient
        {
            Rows =
            [
                new ActivityPageViewRow(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), "/submission/3f2504e0-4f89-41d3-9a0c-0305e82c3301", 4),
                new ActivityPageViewRow(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), "/find", 2),
                new ActivityPageViewRow(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), "/admin/review", 9),
                new ActivityPageViewRow(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), "/receipt/PFOH-7K3Q9X", 1),
                new ActivityPageViewRow(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), "/submission/3f2504e0-4f89-41d3-9a0c-0305e82c3301?code=PFOH-7K3Q9X", 3)
            ]
        };
        var service = Service(client, ResourceId, new MemoryCache(new MemoryCacheOptions()), out var logger);

        var first = await service.ReadAsync(CancellationToken.None);
        var second = await service.ReadAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(1, client.Calls);
        Assert.Equal(first.Total, second!.Total);
        Assert.Equal(10, first.Total);
        Assert.Equal(new[] { "2026-11-04", "2026-11-05" }, first.Daily.Select(day => day.Date));
        Assert.Equal(7, first.Daily[0].PageViews);
        Assert.Equal(3, first.Daily[1].PageViews);
        Assert.Equal("/submission/{token}", first.TopPages[0].Name);
        Assert.Equal(7, first.TopPages[0].PageViews);
        Assert.Equal("/find", first.TopPages[1].Name);
        Assert.DoesNotContain(first.TopPages, page => page.Name.Contains("/admin", StringComparison.OrdinalIgnoreCase));
        var json = JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("user_Id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("3f2504e0", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PFOH-7K3Q9X", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, logger.Warnings);
    }

    [Fact]
    public void Top_pages_merge_a_full_url_with_the_same_path()
    {
        var shaped = ActivityNameScrub.Shape(
        [
            new ActivityPageViewRow(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), "https://host/receipt/{token}", 2),
            new ActivityPageViewRow(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), "/receipt/{token}", 5),
            new ActivityPageViewRow(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), "https://planoflagsofhonor.org/receipt/PFOH-7K3Q9X", 1)
        ]);

        Assert.Equal(8, shaped.Total);
        Assert.Single(shaped.TopPages);
        Assert.Equal("/receipt/{token}", shaped.TopPages[0].Name);
        Assert.Equal(8, shaped.TopPages[0].PageViews);
    }

    [Fact]
    public async Task Failure_and_missing_config_return_null_and_warn_once()
    {
        var client = new FakeClient { Error = new InvalidOperationException("403") };
        var cache = new NoStoreCache();
        var service = Service(client, ResourceId, cache, out var logger);

        Assert.Null(await service.ReadAsync(CancellationToken.None));
        Assert.Null(await service.ReadAsync(CancellationToken.None));
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, logger.Warnings);

        var quiet = new FakeClient();
        var missing = Service(quiet, "", new MemoryCache(new MemoryCacheOptions()), out var missingLog);
        Assert.Null(await missing.ReadAsync(CancellationToken.None));
        Assert.Null(await missing.ReadAsync(CancellationToken.None));
        Assert.Equal(0, quiet.Calls);
        Assert.Equal(1, missingLog.Warnings);
    }

    private static ActivityPageViews Service(FakeClient client, string resourceId, IMemoryCache cache, out ListLogger logger)
    {
        logger = new ListLogger();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActivityTelemetry:AppInsightsResourceId"] = resourceId
        }).Build();
        return new ActivityPageViews(client, configuration, cache, logger);
    }

    private sealed class FakeClient : IActivityPageViewClient
    {
        public int Calls { get; private set; }

        public Exception? Error { get; init; }

        public IReadOnlyList<ActivityPageViewRow> Rows { get; init; } = Array.Empty<ActivityPageViewRow>();

        public Task<IReadOnlyList<ActivityPageViewRow>> QueryAsync(string resourceId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error is not null) throw Error;
            return Task.FromResult(Rows);
        }
    }

    private sealed class ListLogger : ILogger<ActivityPageViews>
    {
        public int Warnings { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class NoStoreCache : IMemoryCache
    {
        public void Dispose()
        {
        }

        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        public ICacheEntry CreateEntry(object key) => new NoopEntry(key);

        public void Remove(object key)
        {
        }

        private sealed class NoopEntry : ICacheEntry
        {
            public NoopEntry(object key) => Key = key;

            public object Key { get; }

            public object? Value { get; set; }

            public DateTimeOffset? AbsoluteExpiration { get; set; }

            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }

            public TimeSpan? SlidingExpiration { get; set; }

            public CacheItemPriority Priority { get; set; }

            public long? Size { get; set; }

            public IList<IChangeToken> ExpirationTokens { get; } = new List<IChangeToken>();

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = new List<PostEvictionCallbackRegistration>();

            public void Dispose()
            {
            }
        }
    }
}
