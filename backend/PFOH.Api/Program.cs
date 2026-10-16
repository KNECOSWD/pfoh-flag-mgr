using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using PFOH.Api.Data;
using PFOH.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        jwtOptions =>
        {
            builder.Configuration.Bind("AzureAd", jwtOptions);

            var apiClientId = builder.Configuration["AzureAd:ClientId"];
            var apiAudience = builder.Configuration["AzureAd:Audience"];

            jwtOptions.TokenValidationParameters.ValidAudiences = new[]
            {
                apiClientId,
                apiAudience,
                $"api://{apiClientId}"
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .ToArray();

            jwtOptions.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    Console.WriteLine($"JWT authentication failed: {context.Exception.Message}");
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    Console.WriteLine($"JWT challenge error: {context.Error} - {context.ErrorDescription}");
                    return Task.CompletedTask;
                }
            };
        },
        identityOptions =>
        {
            builder.Configuration.Bind("AzureAd", identityOptions);
        });

builder.Services.AddAuthorization();

builder.Services.AddAuthorization(options =>
{
    // Entra emits the app role as the "roles" claim on the access token.
    // AddMicrosoftIdentityWebApi leaves MapInboundClaims on and RoleClaimType at
    // ClaimTypes.Role, so JwtBearer renames "roles" before RequireRole runs.
    // Setting RoleClaimType to "roles" hides a role that is already in the token.
    options.AddPolicy("AdminOnly", policy =>
    {
        policy.RequireRole("PFOH.Admin", "Admin");
    });
});

builder.Services.AddDbContext<PfohDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");
    options.UseSqlServer(connectionString);
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://localhost:3000" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddHttpClient();
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.EnableAdaptiveSampling = false;
});
builder.Services.AddSingleton<HonoreeFileStorage>();
builder.Services.AddScoped<UserProfileService>();
builder.Services.AddScoped<FlagGridAssignment>();
builder.Services.AddScoped<PendingReviewMaintenance>();
builder.Services.AddScoped<ReprintQueueMaintenance>();
builder.Services.AddScoped<PublicFlagMap>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("PublicFlagMap", httpContext =>
    {
        var ip = PublicFlagMapClientIp.Resolve(httpContext);
        if (PublicFlagMapClientIp.IsUnlimited(httpContext.RequestServices.GetRequiredService<IConfiguration>(), ip))
        {
            return RateLimitPartition.GetNoLimiter(ip);
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

var app = builder.Build();

//using (var scope = app.Services.CreateScope())
//{
    //var db = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
    // MVP convenience: creates the Flags table if it does not exist.
    // For production ALM, replace with EF migrations executed by your release pipeline.
    //db.Database.EnsureCreated();
//}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.Equals("/flag-map", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/public/flag-map", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }

    await next();
});
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "PFOH.Api" })).AllowAnonymous();
app.MapControllers();
app.MapFallbackToFile("index.html");

try
{
    await using var scope = app.Services.CreateAsyncScope();
    var startupDb = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
    try
    {
        await PendingSubmissionGridSchema.EnsureAsync(startupDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while ensuring nullable flag spots and UX_FlagClaims_ActiveFlagGrid. {Failure}",
            ex.ToString());
    }

    try
    {
        var startupMaintenance = scope.ServiceProvider.GetRequiredService<PendingReviewMaintenance>();
        await startupMaintenance.CleanupDuplicatePendingAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while superseding duplicate pending requests and releasing pending spots. The site will keep running. {Failure}",
            ex.ToString());
    }
}
catch (Exception ex)
{
    app.Logger.LogError(
        ex,
        "STARTUP CLEANUP FAILED before pending submission cleanup could start. The site will keep running. {Failure}",
        ex.ToString());
}

app.Run();

public partial class Program
{
}
