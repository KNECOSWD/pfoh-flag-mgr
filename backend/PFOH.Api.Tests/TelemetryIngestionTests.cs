using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WindowsServer.Channel.Implementation;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class TelemetryVolumeProcessorTests
{
    [Fact]
    public void Exception_passes_through()
    {
        var capture = new CaptureProcessor();
        var processor = Processor(capture);
        var exception = new ExceptionTelemetry(new InvalidOperationException("synthetic"));

        processor.Process(exception);

        Assert.Same(exception, Assert.Single(capture.Items));
    }

    [Fact]
    public void Failed_request_passes_through()
    {
        var capture = new CaptureProcessor();
        var processor = Processor(capture);
        var request = Request("GET /api/profile", "/api/profile", success: false, responseCode: "500");

        processor.Process(request);

        Assert.Same(request, Assert.Single(capture.Items));
    }

    [Fact]
    public void Server_error_request_passes_through_when_success_is_still_true()
    {
        var capture = new CaptureProcessor();
        Processor(capture).Process(Request("GET /api/profile", "/api/profile", success: true, responseCode: "503"));

        Assert.Single(capture.Items);
    }

    [Fact]
    public void Successful_fast_sql_dependency_is_dropped()
    {
        var capture = new CaptureProcessor();
        Processor(capture).Process(Dependency("SQL", success: true, resultCode: "0", milliseconds: 40));

        Assert.Empty(capture.Items);
    }

    [Fact]
    public void Failed_dependency_is_kept()
    {
        var capture = new CaptureProcessor();
        var failed = Dependency("SQL", success: false, resultCode: "18456", milliseconds: 5);

        Processor(capture).Process(failed);

        Assert.Same(failed, Assert.Single(capture.Items));
    }

    [Fact]
    public void Server_error_dependency_is_kept_when_it_is_fast()
    {
        var capture = new CaptureProcessor();
        Processor(capture).Process(Dependency("HTTP", success: true, resultCode: "500", milliseconds: 10));

        Assert.Single(capture.Items);
    }

    [Fact]
    public void Slow_successful_sql_dependency_is_kept()
    {
        var capture = new CaptureProcessor();
        Processor(capture).Process(Dependency("SQL", success: true, resultCode: "0", milliseconds: 100));

        Assert.Single(capture.Items);
    }

    [Fact]
    public void Successful_fast_http_dependency_is_dropped()
    {
        var capture = new CaptureProcessor();
        Processor(capture).Process(Dependency("Http", success: true, resultCode: "200", milliseconds: 15));

        Assert.Empty(capture.Items);
    }

    [Fact]
    public void Health_probe_request_is_filtered()
    {
        var capture = new CaptureProcessor();
        var processor = Processor(capture);

        processor.Process(Request("GET /health", "/health", success: true, responseCode: "200"));
        processor.Process(Request("GET /Health/", "/Health/", success: true, responseCode: "200"));
        processor.Process(Request("GET /robots933456.txt", "/robots933456.txt", success: true, responseCode: "200"));

        Assert.Empty(capture.Items);
    }

    [Fact]
    public void Failed_health_probe_is_kept()
    {
        var capture = new CaptureProcessor();
        Processor(capture).Process(Request("GET /health", "/health", success: false, responseCode: "500"));

        Assert.Single(capture.Items);
    }

    [Fact]
    public void Ordinary_successful_request_is_kept()
    {
        var capture = new CaptureProcessor();
        Processor(capture).Process(Request("GET /api/version", "/api/version", success: true, responseCode: "200"));

        Assert.Single(capture.Items);
    }

    private static TelemetryVolumeProcessor Processor(CaptureProcessor capture, int thresholdMs = 100)
    {
        return new TelemetryVolumeProcessor(capture, TimeSpan.FromMilliseconds(thresholdMs));
    }

    private static RequestTelemetry Request(string name, string path, bool success, string responseCode)
    {
        return new RequestTelemetry
        {
            Name = name,
            Url = new Uri("https://pfoh.example" + path),
            Success = success,
            ResponseCode = responseCode
        };
    }

    private static DependencyTelemetry Dependency(string type, bool success, string resultCode, int milliseconds)
    {
        return new DependencyTelemetry
        {
            Type = type,
            Name = type,
            Target = "database.example",
            Data = "not-collected",
            Success = success,
            ResultCode = resultCode,
            Duration = TimeSpan.FromMilliseconds(milliseconds)
        };
    }
}

public class FailureRetentionTelemetryInitializerTests
{
    private readonly FailureRetentionTelemetryInitializer initializer = new();

    [Fact]
    public void Exception_failed_request_and_failed_dependency_are_retained_at_100_percent()
    {
        var exception = new ExceptionTelemetry(new InvalidOperationException("synthetic"));
        var request = new RequestTelemetry { Success = false, ResponseCode = "500" };
        var dependency = new DependencyTelemetry { Type = "SQL", Success = false, ResultCode = "4060" };
        var serverError = new RequestTelemetry { Success = true, ResponseCode = "502" };

        initializer.Initialize(exception);
        initializer.Initialize(request);
        initializer.Initialize(dependency);
        initializer.Initialize(serverError);

        Assert.Equal(100, ((ISupportSampling)exception).SamplingPercentage);
        Assert.Equal(100, ((ISupportSampling)request).SamplingPercentage);
        Assert.Equal(100, ((ISupportSampling)dependency).SamplingPercentage);
        Assert.Equal(100, ((ISupportSampling)serverError).SamplingPercentage);
    }

    [Fact]
    public void Successful_request_and_dependency_are_left_for_sampling()
    {
        var request = new RequestTelemetry { Success = true, ResponseCode = "200" };
        var dependency = new DependencyTelemetry { Type = "SQL", Success = true, ResultCode = "0" };

        initializer.Initialize(request);
        initializer.Initialize(dependency);

        Assert.Null(((ISupportSampling)request).SamplingPercentage);
        Assert.Null(((ISupportSampling)dependency).SamplingPercentage);
    }
}

public class AdaptiveSamplingRetentionTests
{
    [Fact]
    public void Exceptions_events_and_failed_requests_pass_sampling_that_drops_successful_traffic()
    {
        var captured = new List<ITelemetry>();
        var configuration = TelemetryConfiguration.CreateDefault();
        var builder = configuration.DefaultTelemetrySink.TelemetryProcessorChainBuilder;
        builder.UseAdaptiveSampling(new SamplingPercentageEstimatorSettings
        {
            MaxTelemetryItemsPerSecond = 0.1,
            InitialSamplingPercentage = 0.1,
            MinSamplingPercentage = 0.1,
            MaxSamplingPercentage = 0.1
        }, callback: null, excludedTypes: TelemetryIngestion.SamplingExcludedTypes);
        builder.Use(next => new CaptureProcessor(captured, next));
        builder.Build();

        var root = configuration.DefaultTelemetrySink.TelemetryProcessors.First();
        var initializer = new FailureRetentionTelemetryInitializer();
        var volume = new TelemetryVolumeProcessor(root, TimeSpan.FromMilliseconds(100));

        for (var i = 0; i < 20; i++)
        {
            var request = new RequestTelemetry
            {
                Name = "GET /api/profile",
                Url = new Uri("https://pfoh.example/api/profile"),
                Success = true,
                ResponseCode = "200"
            };
            request.Context.Operation.Id = Guid.NewGuid().ToString("N");
            initializer.Initialize(request);
            volume.Process(request);
        }

        var failed = new RequestTelemetry
        {
            Name = "GET /api/profile",
            Url = new Uri("https://pfoh.example/api/profile"),
            Success = false,
            ResponseCode = "500"
        };
        failed.Context.Operation.Id = Guid.NewGuid().ToString("N");
        initializer.Initialize(failed);
        volume.Process(failed);

        var exception = new ExceptionTelemetry(new InvalidOperationException("synthetic"));
        initializer.Initialize(exception);
        volume.Process(exception);

        var customEvent = new EventTelemetry("synthetic-event");
        volume.Process(customEvent);

        volume.Process(new DependencyTelemetry
        {
            Type = "SQL",
            Name = "SQL",
            Success = true,
            ResultCode = "0",
            Duration = TimeSpan.FromMilliseconds(8)
        });

        var failedDependency = new DependencyTelemetry
        {
            Type = "HTTP",
            Name = "HTTP",
            Success = false,
            ResultCode = "503",
            Duration = TimeSpan.FromMilliseconds(8)
        };
        failedDependency.Context.Operation.Id = Guid.NewGuid().ToString("N");
        initializer.Initialize(failedDependency);
        volume.Process(failedDependency);

        volume.Process(new RequestTelemetry
        {
            Name = "GET /health",
            Url = new Uri("https://pfoh.example/health"),
            Success = true,
            ResponseCode = "200"
        });

        Assert.DoesNotContain(captured, item => item is RequestTelemetry request && request.ResponseCode == "200");
        Assert.Contains(captured, item => item is RequestTelemetry request && request.ResponseCode == "500");
        Assert.Contains(captured, item => item is ExceptionTelemetry);
        Assert.Contains(captured, item => item is EventTelemetry);
        Assert.Contains(captured, item => item is DependencyTelemetry dependency && dependency.Success == false);
        Assert.DoesNotContain(captured, item => item is DependencyTelemetry dependency && dependency.Success == true);
        Assert.DoesNotContain(captured, item => item is RequestTelemetry request && request.Url?.AbsolutePath == "/health");
    }
}

public class TelemetrySamplingSettingsTests
{
    [Fact]
    public void Missing_or_invalid_settings_use_the_safe_defaults()
    {
        var missing = new ConfigurationBuilder().Build();
        Assert.Equal(TelemetryIngestion.DefaultMaxTelemetryItemsPerSecond, TelemetryIngestion.ReadMaxTelemetryItemsPerSecond(missing));
        Assert.Equal(TimeSpan.FromMilliseconds(100), TelemetryIngestion.ReadSuccessfulDependencyDropBelow(missing));

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryIngestion.MaxTelemetryItemsPerSecondKey] = "-1",
            [TelemetryIngestion.SuccessfulDependencyDropBelowMillisecondsKey] = "nope"
        }).Build();
        Assert.Equal(2, TelemetryIngestion.ReadMaxTelemetryItemsPerSecond(invalid));
        Assert.Equal(TimeSpan.FromMilliseconds(100), TelemetryIngestion.ReadSuccessfulDependencyDropBelow(invalid));
    }

    [Fact]
    public void App_settings_override_the_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryIngestion.MaxTelemetryItemsPerSecondKey] = "9.5",
            [TelemetryIngestion.SuccessfulDependencyDropBelowMillisecondsKey] = "250"
        }).Build();

        Assert.Equal(9.5, TelemetryIngestion.ReadMaxTelemetryItemsPerSecond(configuration));
        Assert.Equal(TimeSpan.FromMilliseconds(250), TelemetryIngestion.ReadSuccessfulDependencyDropBelow(configuration));
    }
}

public class TelemetrySamplingHostTests : IDisposable
{
    private readonly PfohApiFactory factory = new();

    [Fact]
    public void Adaptive_sampling_excludes_exceptions_and_events_and_uses_the_configured_cap()
    {
        using var scope = factory.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<TelemetryConfiguration>();
        var processors = configuration.DefaultTelemetrySink.TelemetryProcessors.ToList();
        var names = string.Join(", ", processors.Select(processor => processor.GetType().Name));
        var volumeAt = processors.FindIndex(processor => processor is TelemetryVolumeProcessor);
        var samplerAt = processors.FindIndex(processor => processor is AdaptiveSamplingTelemetryProcessor);
        Assert.True(volumeAt >= 0 && samplerAt > volumeAt, names);

        var sampler = Assert.Single(processors.OfType<AdaptiveSamplingTelemetryProcessor>());
        Assert.Contains("Exception", sampler.ExcludedTypes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Event", sampler.ExcludedTypes, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, sampler.MaxTelemetryItemsPerSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(100), ((TelemetryVolumeProcessor)processors[volumeAt]).SuccessfulDependencyDropBelow);

        var dependencyModule = scope.ServiceProvider.GetServices<ITelemetryModule>()
            .OfType<DependencyTrackingTelemetryModule>()
            .Single();
        Assert.False(dependencyModule.EnableSqlCommandTextInstrumentation);

        var settings = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        Assert.Equal("Warning", settings["Logging:ApplicationInsights:LogLevel:Microsoft"]);
        Assert.Equal("Warning", settings["Logging:ApplicationInsights:LogLevel:Microsoft.AspNetCore"]);
        Assert.Equal("Warning", settings["Logging:ApplicationInsights:LogLevel:Microsoft.EntityFrameworkCore"]);
        Assert.Equal("None", settings["Logging:ApplicationInsights:LogLevel:Microsoft.EntityFrameworkCore.Update"]);
        Assert.Equal("None", settings["Logging:ApplicationInsights:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"]);
        Assert.Equal("Warning", settings["Logging:ApplicationInsights:LogLevel:System"]);
        Assert.Equal("Information", settings["Logging:ApplicationInsights:LogLevel:PFOH"]);
        Assert.Equal("Information", settings["Logging:ApplicationInsights:LogLevel:Default"]);
        Assert.Equal("Warning", settings["Logging:LogLevel:Microsoft.EntityFrameworkCore"]);
        Assert.Equal("None", settings["Logging:LogLevel:Microsoft.EntityFrameworkCore.Update"]);
        Assert.Equal("None", settings["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"]);
        Assert.Contains(
            configuration.TelemetryInitializers,
            initializer => initializer is AdminIdentityTelemetryInitializer);
        var registered = scope.ServiceProvider.GetServices<ITelemetryInitializer>()
            .OfType<AdminIdentityTelemetryInitializer>()
            .Single();
        Assert.True(registered.HasHttpContextAccessor);
    }

    [Fact]
    public void Sampling_cap_app_setting_is_applied_to_the_sampler()
    {
        using var configured = new PfohApiFactory
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                [TelemetryIngestion.MaxTelemetryItemsPerSecondKey] = "9"
            }
        };
        using var scope = configured.Services.CreateScope();
        var sampler = scope.ServiceProvider.GetRequiredService<TelemetryConfiguration>()
            .DefaultTelemetrySink.TelemetryProcessors
            .OfType<AdaptiveSamplingTelemetryProcessor>()
            .Single();

        Assert.Equal(9, sampler.MaxTelemetryItemsPerSecond);
    }

    public void Dispose() => factory.Dispose();
}

internal sealed class CaptureProcessor : ITelemetryProcessor
{
    private readonly List<ITelemetry> items;
    private readonly ITelemetryProcessor? next;

    public CaptureProcessor()
        : this(new List<ITelemetry>(), null)
    {
    }

    public CaptureProcessor(List<ITelemetry> items, ITelemetryProcessor? next)
    {
        this.items = items;
        this.next = next;
    }

    public List<ITelemetry> Items => items;

    public void Process(ITelemetry item)
    {
        items.Add(item);
        next?.Process(item);
    }
}
