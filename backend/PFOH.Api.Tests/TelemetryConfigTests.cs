using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace PFOH.Api.Tests;

public class TelemetryConfigTests
{
    [Fact]
    public async Task Telemetry_config_is_anonymous_and_off_when_unset()
    {
        await using var factory = new PfohApiFactory
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["ApplicationInsights:ConnectionString"] = "",
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
            }
        };

        var response = await factory.CreateClient().GetAsync("/api/telemetry-config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.False(body.TryGetProperty("connectionString", out _));
        Assert.DoesNotContain("InstrumentationKey", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telemetry_config_returns_the_configured_connection_string()
    {
        const string connectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000";
        await using var factory = new PfohApiFactory
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["ApplicationInsights:ConnectionString"] = connectionString,
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=ffffffff-ffff-ffff-ffff-ffffffffffff"
            }
        };

        var response = await factory.CreateClient().GetAsync("/api/telemetry-config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(connectionString, body.GetProperty("connectionString").GetString());
        Assert.Equal(2, body.EnumerateObject().Count());
    }

    [Fact]
    public async Task Telemetry_config_reads_the_environment_connection_string()
    {
        const string connectionString = "InstrumentationKey=11111111-1111-1111-1111-111111111111";
        await using var factory = new PfohApiFactory
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["ApplicationInsights:ConnectionString"] = "",
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = connectionString
            }
        };

        var response = await factory.CreateClient().GetAsync("/api/telemetry-config");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(connectionString, body.GetProperty("connectionString").GetString());
    }
}
