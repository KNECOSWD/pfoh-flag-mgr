using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class PublicFlagMapKioskExitUnsetTests : IClassFixture<PfohApiFactory>
{
    private readonly PfohApiFactory factory;

    public PublicFlagMapKioskExitUnsetTests(PfohApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task An_unset_pin_cannot_exit_kiosk_mode()
    {
        using var response = await Send(factory, "198.51.100.30", "482913");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(KioskExitPin.Matches(new ConfigurationBuilder().Build(), "482913"));
    }

    private static Task<HttpResponseMessage> Send(PfohApiFactory factory, string ip, string pin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/flag-map/kiosk-exit")
        {
            Content = JsonContent.Create(new { pin })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return factory.CreateClient().SendAsync(request);
    }
}

public class PublicFlagMapKioskExitTests : IClassFixture<PublicFlagMapKioskExitTests.Factory>
{
    private readonly Factory factory;

    public PublicFlagMapKioskExitTests(Factory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task The_correct_pin_exits_and_a_wrong_pin_does_not()
    {
        using var wrong = await Send("198.51.100.31", "0000");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var right = await Send("198.51.100.32", "482913");
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.False(KioskExitPin.Matches(Config(), "48291"));
        Assert.True(KioskExitPin.Matches(Config(), "482913"));
    }

    [Fact]
    public async Task Wrong_pins_are_rate_limited()
    {
        for (var i = 0; i < 5; i++)
        {
            using var response = await Send("198.51.100.33", "1111");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await Send("198.51.100.33", "482913");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private Task<HttpResponseMessage> Send(string ip, string pin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/flag-map/kiosk-exit")
        {
            Content = JsonContent.Create(new { pin })
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        return factory.CreateClient().SendAsync(request);
    }

    private static IConfiguration Config()
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicFlagMap:KioskExitPin"] = "482913"
        }).Build();
    }

    public sealed class Factory : PfohApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PublicFlagMap:KioskExitPin"] = "482913"
                });
            });
            base.ConfigureWebHost(builder);
        }
    }
}
