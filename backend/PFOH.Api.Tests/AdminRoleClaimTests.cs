using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace PFOH.Api.Tests;

public class AdminRoleClaimTests
{
    private const string ApiClientId = "c9004926-396b-47e0-93e7-57f19504185e";
    private const string SpaClientId = "06e5ea83-b3c7-4d53-9513-9ddbcfc2d434";

    [Fact]
    public async Task Api_audience_with_roles_claim_is_an_administrator()
    {
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(Token(ApiClientId, new Claim("roles", "PFOH.Admin"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_audience_without_a_role_claim_is_forbidden()
    {
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(Token(ApiClientId, new Claim("scp", "access_as_user"))));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Spa_audience_is_rejected_before_the_role_check()
    {
        await using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(Token(SpaClientId, new Claim("roles", "PFOH.Admin"))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-signing-key-at-least-32-bytes-long"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Same mapping the API uses: inbound "roles" becomes ClaimTypes.Role.
                options.MapInboundClaims = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = true,
                    ValidAudiences = [ApiClientId, $"api://{ApiClientId}"],
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                    NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy => policy.RequireRole("PFOH.Admin", "Admin"));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/admin", () => Results.Ok(new { ok = true })).RequireAuthorization("AdminOnly");
        await app.StartAsync();
        return app;
    }

    private static string Token(string audience, params Claim[] claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-signing-key-at-least-32-bytes-long"));
        var jwt = new JwtSecurityToken(
            issuer: "test",
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static HttpRequestMessage Request(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
