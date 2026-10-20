using System.Net;
using System.Net.Http.Headers;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PFOH.Api.Tests;

public class AuthenticatedGetRateLimitTests : IDisposable
{
    private readonly PfohApiFactory factory = new() { AuthenticatedGetPermit = 60 };

    [Fact]
    public async Task Profile_get_returns_429_with_retry_after_after_sixty_reads_for_one_user()
    {
        var client = factory.CreateClient();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 61; i++)
        {
            var response = await client.SendAsync(ProfileGet("rate-limit-user"));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
                Assert.Equal(60, i);
                break;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.NotNull(limited);
        Assert.True(limited!.Headers.TryGetValues("Retry-After", out var retryAfter), "429 includes Retry-After");
        var seconds = int.Parse(Assert.Single(retryAfter));
        Assert.InRange(seconds, 1, 60);

        var other = await client.SendAsync(ProfileGet("someone-else"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        var anonymous = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Health_and_version_stay_available_past_the_authenticated_read_limit()
    {
        var client = factory.CreateClient();
        for (var i = 0; i < 70; i++)
        {
            var health = await client.GetAsync("/health");
            var version = await client.GetAsync("/api/version");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        }
    }

    public void Dispose() => factory.Dispose();

    private static HttpRequestMessage ProfileGet(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add(TestAuthHandler.OidHeader, userId);
        request.Headers.Add(TestAuthHandler.EmailHeader, $"{userId}@example.com");
        request.Headers.Add(TestAuthHandler.NameHeader, "Rate Limit");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}

public class SamplingConfigurationTests : IDisposable
{
    private readonly PfohApiFactory factory = new();

    [Fact]
    public void Adaptive_sampling_is_on_and_exceptions_are_excluded()
    {
        var program = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "backend", "PFOH.Api", "Program.cs")));
        Assert.Contains("UseAdaptiveSampling(excludedTypes: \"Exception\"", program, StringComparison.Ordinal);
        Assert.Contains("options.EnableAdaptiveSampling = false", program, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<TelemetryConfiguration>();
        var sampler = configuration.DefaultTelemetrySink.TelemetryProcessors
            .OfType<AdaptiveSamplingTelemetryProcessor>()
            .Single();
        Assert.Contains("Exception", sampler.ExcludedTypes, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => factory.Dispose();
}
