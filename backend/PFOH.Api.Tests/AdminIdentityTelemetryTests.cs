using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class AdminIdentityTelemetryTests
{
    private const string SearchTerm = "pii-search-9f3c2a@example.test";
    private const string UserObjectId = "b7e1c0a2-4d55-4e8a-9c31-0a6f2e8d4b17";
    private const string ProfileObjectId = "c8f2d1b3-5e66-4f9b-8d42-1b7a3f9e5c28";
    private const string Probe = "pfoh-telemetry-sink-marker";

    [Fact]
    public void Automatic_telemetry_omits_the_search_term_and_object_ids()
    {
        var encodedTerm = Uri.EscapeDataString(SearchTerm);
        var accessor = new HttpContextAccessor();
        var items = new List<ITelemetry>();
        var seenBeforeSampling = new List<ITelemetry>();
        var client = Client(accessor, items, seenBeforeSampling);

        accessor.HttpContext = Route("GET", "api/admin/users/search");
        var search = new RequestTelemetry
        {
            Name = "GET /api/admin/users/search?q=" + SearchTerm,
            Url = new Uri("https://app.example/api/admin/users/search?q=" + encodedTerm),
            ResponseCode = "200",
            Success = true
        };
        search.Properties["RequestPath"] = "/api/admin/users/search?q=" + SearchTerm;
        search.Context.Operation.Name = search.Name;
        client.TrackRequest(search);

        accessor.HttpContext = Route("POST", "api/admin/users/{userObjectId:guid}/admin-role");
        var grant = Request(
            "POST /api/admin/users/" + UserObjectId + "/admin-role",
            "https://app.example/api/admin/users/" + UserObjectId + "/admin-role?unused=1");
        client.TrackRequest(grant);

        accessor.HttpContext = Route("DELETE", "api/admin/users/{userObjectId:guid}/admin-role");
        var remove = Request(
            "DELETE /api/admin/users/" + UserObjectId + "/admin-role",
            "https://app.example/api/admin/users/" + UserObjectId + "/admin-role");
        client.TrackRequest(remove);

        accessor.HttpContext = Route("PUT", "api/profile/{ownerObjectId}");
        var profile = Request(
            "PUT /api/profile/" + ProfileObjectId,
            "https://app.example/api/profile/" + ProfileObjectId + "?next=1");
        client.TrackRequest(profile);

        accessor.HttpContext = null;
        var graphFilter = new DependencyTelemetry(
            "HTTP",
            "graph.microsoft.com",
            "GET /v1.0/users",
            "https://graph.microsoft.com/v1.0/users?$select=id&$filter=startswith(mail,'" + SearchTerm + "')");
        graphFilter.Context.Operation.Name = "GET /api/admin/users/search?q=" + SearchTerm;
        client.TrackDependency(graphFilter);

        var graphUser = new DependencyTelemetry(
            "HTTP",
            "graph.microsoft.com",
            "GET /v1.0/users/" + UserObjectId,
            "https://graph.microsoft.com/v1.0/users/" + UserObjectId + "?$select=id,displayName");
        client.TrackDependency(graphUser);

        var exception = new ExceptionTelemetry(new InvalidOperationException(
            "Microsoft Graph returned 400: https://graph.microsoft.com/v1.0/users/" + UserObjectId
            + "?$filter=startswith(mail,'" + SearchTerm + "')"));
        exception.Properties["RequestPath"] = "/api/profile/" + ProfileObjectId;
        client.TrackException(exception);

        var trace = new TraceTelemetry("Request failed for /api/admin/users/search?q=" + SearchTerm);
        trace.Properties["RequestPath"] = "/api/admin/users/" + UserObjectId + "/admin-role";
        trace.Properties["ProfilePath"] = "/api/profile/" + ProfileObjectId;
        client.TrackTrace(trace);

        client.Flush();

        Assert.NotEmpty(items);
        Assert.Equal(items.Count, seenBeforeSampling.Count);
        foreach (var item in items.Concat(seenBeforeSampling))
        {
            var serialized = Serialize(item);
            Assert.DoesNotContain(SearchTerm, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pii-search-9f3c2a", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(encodedTerm, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(UserObjectId, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ProfileObjectId, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("$filter", serialized, StringComparison.OrdinalIgnoreCase);
        }

        var searchText = Serialize(items[0]);
        Assert.Contains("/api/admin/users/search", searchText, StringComparison.Ordinal);
        Assert.DoesNotContain("?", searchText, StringComparison.Ordinal);
        Assert.Equal("GET api/admin/users/search", items[0].Context.Operation.Name);

        var grantText = Serialize(items[1]);
        Assert.Contains("admin-role", grantText, StringComparison.Ordinal);
        Assert.Contains(AdminIdentityTelemetryInitializer.IdToken, grantText, StringComparison.Ordinal);
        Assert.Equal("POST api/admin/users/{userObjectId:guid}/admin-role", items[1].Context.Operation.Name);
        Assert.Equal(
            "/api/admin/users/" + AdminIdentityTelemetryInitializer.IdToken + "/admin-role",
            ((RequestTelemetry)items[1]).Properties["RequestPath"]);

        var removeText = Serialize(items[2]);
        Assert.Contains("admin-role", removeText, StringComparison.Ordinal);
        Assert.Equal("DELETE api/admin/users/{userObjectId:guid}/admin-role", items[2].Context.Operation.Name);

        var profileText = Serialize(items[3]);
        Assert.Contains("/api/profile/" + AdminIdentityTelemetryInitializer.IdToken, profileText, StringComparison.Ordinal);
        Assert.Equal("PUT api/profile/{ownerObjectId}", items[3].Context.Operation.Name);
        Assert.Equal(
            "/api/profile/" + AdminIdentityTelemetryInitializer.IdToken,
            ((RequestTelemetry)items[3]).Properties["RequestPath"]);

        var filterDependency = (DependencyTelemetry)items[4];
        Assert.Equal("/v1.0/users", filterDependency.Data);
        Assert.Equal("/v1.0/users", filterDependency.Name);
        Assert.Equal("/v1.0/users", filterDependency.Target);

        var userDependency = (DependencyTelemetry)items[5];
        Assert.Equal("/v1.0/users/" + AdminIdentityTelemetryInitializer.IdToken, userDependency.Data);
        Assert.Equal(userDependency.Data, userDependency.Name);
        Assert.Equal(userDependency.Data, userDependency.Target);

        var traceItem = Assert.IsType<TraceTelemetry>(items[7]);
        Assert.Equal(
            "/api/admin/users/" + AdminIdentityTelemetryInitializer.IdToken + "/admin-role",
            traceItem.Properties["RequestPath"]);
        Assert.Equal(
            "/api/profile/" + AdminIdentityTelemetryInitializer.IdToken,
            traceItem.Properties["ProfilePath"]);
        Assert.DoesNotContain("?", traceItem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ef_update_errors_do_not_reach_the_application_insights_channel()
    {
        var appsettings = ProductionAppsettingsPath();
        var configuration = new ConfigurationBuilder().AddJsonFile(appsettings).Build();
        Assert.Equal("None", configuration["Logging:ApplicationInsights:LogLevel:Microsoft.EntityFrameworkCore.Update"]);
        Assert.Equal("None", configuration["Logging:ApplicationInsights:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"]);
        Assert.Equal("None", configuration["Logging:LogLevel:Microsoft.EntityFrameworkCore.Update"]);
        Assert.Equal("None", configuration["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"]);

        var items = new List<ITelemetry>();
        var channel = new StubChannel(items);
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddApplicationInsights(telemetry =>
            {
                telemetry.ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000";
                telemetry.TelemetryChannel = channel;
                telemetry.TelemetryInitializers.Add(new AdminIdentityTelemetryInitializer());
                channel.DeveloperMode = true;
            }, _ => { });
        });

        factory.CreateLogger("Microsoft.EntityFrameworkCore.Update").LogError(
            new DbUpdateException("duplicate key (" + UserObjectId + ")"),
            "An exception occurred in the database while saving changes for {ObjectId}.",
            UserObjectId);
        factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError(
            "Failed executing DbCommand ({Elapsed}) [Parameters=[{ObjectId}]]",
            3,
            UserObjectId);
        factory.CreateLogger("PFOH.Api.Tests.TelemetryProbe").LogInformation(Probe);
        factory.Dispose();

        var serialized = items.Select(Serialize).ToList();
        Assert.Contains(serialized, item => item.Contains(Probe, StringComparison.Ordinal));
        Assert.All(serialized, item => Assert.DoesNotContain(UserObjectId, item, StringComparison.OrdinalIgnoreCase));
        Assert.All(serialized, item => Assert.DoesNotContain("duplicate key", item, StringComparison.Ordinal));
        Assert.All(serialized, item => Assert.DoesNotContain("Failed executing DbCommand", item, StringComparison.Ordinal));
        Assert.DoesNotContain(serialized, item => item.Contains("Microsoft.EntityFrameworkCore.Update", StringComparison.Ordinal));
    }

    private static RequestTelemetry Request(string name, string url)
    {
        var request = new RequestTelemetry
        {
            Name = name,
            Url = new Uri(url),
            ResponseCode = "500",
            Success = false
        };
        request.Properties["RequestPath"] = new Uri(url).AbsolutePath;
        request.Context.Operation.Name = name;
        return request;
    }

    private static DefaultHttpContext Route(string method, string template)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            0,
            EndpointMetadataCollection.Empty,
            template));
        return context;
    }

    private static TelemetryClient Client(
        IHttpContextAccessor accessor,
        List<ITelemetry> items,
        List<ITelemetry> seenBeforeSampling)
    {
        var configuration = new TelemetryConfiguration
        {
            ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
            TelemetryChannel = new StubChannel(items)
        };
        configuration.TelemetryInitializers.Add(new AdminIdentityTelemetryInitializer(accessor));
        configuration.TelemetryProcessorChainBuilder.Use(next => new CaptureProcessor(seenBeforeSampling, next));
        configuration.TelemetryProcessorChainBuilder.Build();
        return new TelemetryClient(configuration);
    }

    private static string ProductionAppsettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "PFOH.Api", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("backend/PFOH.Api/appsettings.json was not found from the test output directory.");
    }

    private static string Serialize(ITelemetry telemetry)
    {
        var writer = new ItemText();
        telemetry.SerializeData(writer);
        writer.Append(telemetry.Context.Operation.Name);
        switch (telemetry)
        {
            case RequestTelemetry request:
                writer.Append(request.Url?.AbsoluteUri);
                writer.Append(request.Name);
                break;
            case DependencyTelemetry dependency:
                writer.Append(dependency.Data);
                writer.Append(dependency.Name);
                writer.Append(dependency.Target);
                break;
            case ExceptionTelemetry exception:
                writer.Append(exception.Message);
                writer.Append(exception.Exception?.ToString());
                break;
            case TraceTelemetry trace:
                writer.Append(trace.Message);
                break;
        }

        foreach (var property in telemetry.Context.GlobalProperties)
        {
            writer.Append(property.Key);
            writer.Append(property.Value);
        }

        return writer.Text;
    }

    private sealed class StubChannel(List<ITelemetry> items) : ITelemetryChannel
    {
        public bool? DeveloperMode { get; set; } = true;
        public string EndpointAddress { get; set; } = string.Empty;

        public void Send(ITelemetry item) => items.Add(item);

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ItemText : ISerializationWriter
    {
        private readonly StringBuilder text = new();

        public string Text => text.ToString();

        public void Append(string? value) => text.AppendLine(value);

        public void WriteProperty(string name, string? value) => text.Append(name).Append('=').AppendLine(value);

        public void WriteProperty(string name, double? value) => text.Append(name).Append('=').AppendLine(value?.ToString());

        public void WriteProperty(string name, int? value) => text.Append(name).Append('=').AppendLine(value?.ToString());

        public void WriteProperty(string name, bool? value) => text.Append(name).Append('=').AppendLine(value?.ToString());

        public void WriteProperty(string name, TimeSpan? value) => text.Append(name).Append('=').AppendLine(value?.ToString());

        public void WriteProperty(string name, DateTimeOffset? value) => text.Append(name).Append('=').AppendLine(value?.ToString());

        public void WriteProperty(string name, IDictionary<string, string>? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var pair in value)
            {
                text.Append(name).Append('.').Append(pair.Key).Append('=').AppendLine(pair.Value);
            }
        }

        public void WriteProperty(string name, IDictionary<string, double>? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var pair in value)
            {
                text.Append(name).Append('.').Append(pair.Key).Append('=').AppendLine(pair.Value.ToString());
            }
        }

        public void WriteProperty(string name, IList<string>? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var item in value)
            {
                text.Append(name).Append('=').AppendLine(item);
            }
        }

        public void WriteProperty(string name, IList<ISerializableWithWriter>? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var item in value)
            {
                item?.Serialize(this);
            }
        }

        public void WriteProperty(string name, ISerializableWithWriter? value) => value?.Serialize(this);

        public void WriteProperty(ISerializableWithWriter? value) => value?.Serialize(this);

        public void WriteStartObject()
        {
        }

        public void WriteStartObject(string name) => text.AppendLine(name);

        public void WriteEndObject()
        {
        }
    }
}
