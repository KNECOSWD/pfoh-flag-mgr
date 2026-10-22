using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class ProfileStoreOutageTests : IDisposable
{
    private readonly PfohApiFactory factory = new();

    [Fact]
    public async Task Health_reports_unhealthy_and_profile_returns_503_while_the_circuit_is_open()
    {
        var circuit = factory.Services.GetRequiredService<ProfileStoreCircuit>();
        var client = factory.CreateClient();

        var healthy = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal("Healthy", (await healthy.Content.ReadAsStringAsync()).Trim());

        circuit.Open(DateTime.UtcNow);

        var unhealthy = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
        Assert.Equal("Unhealthy", (await unhealthy.Content.ReadAsStringAsync()).Trim());

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add(TestAuthHandler.OidHeader, "outage-user");
        request.Headers.Add(TestAuthHandler.EmailHeader, "outage@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Outage Person");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var profile = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, profile.StatusCode);
        Assert.True(profile.Headers.TryGetValues("Retry-After", out var retryAfter), "503 includes Retry-After");
        var seconds = int.Parse(Assert.Single(retryAfter));
        Assert.InRange(seconds, 1, 5);
        var body = await profile.Content.ReadAsStringAsync();
        Assert.Contains("The profile store is unavailable.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);

        circuit.Close();
        var restored = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("Healthy", (await restored.Content.ReadAsStringAsync()).Trim());
    }

    public void Dispose()
    {
        factory.Services.GetRequiredService<ProfileStoreCircuit>().Close();
        factory.Dispose();
    }
}
