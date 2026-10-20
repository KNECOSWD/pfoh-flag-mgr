using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace PFOH.Api.Tests;

public class VersionApiTests(PfohApiFactory factory) : IClassFixture<PfohApiFactory>
{
    [Fact]
    public async Task Version_endpoint_returns_json_instead_of_the_spa_page()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("{", raw.Trim());
        Assert.DoesNotContain("<html", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<!DOCTYPE", raw, StringComparison.OrdinalIgnoreCase);

        var payload = await response.Content.ReadFromJsonAsync<VersionPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal("2.0.18", payload.Version);
        Assert.False(string.IsNullOrWhiteSpace(payload.Commit));
        Assert.True(DateTime.TryParse(payload.BuiltAt, out _));
    }

    private sealed record VersionPayload(string Version, string Commit, string BuiltAt);
}
