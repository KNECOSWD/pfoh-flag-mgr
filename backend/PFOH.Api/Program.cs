using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Web;
using PFOH.Api.Data;
using PFOH.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "text/html",
        "text/css",
        "application/javascript",
        "text/javascript",
        "application/json"
    ]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);

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

builder.Services.AddSingleton<PublicFlagMapCache>();
builder.Services.AddSingleton<SubmitterNameTokenCache>();
builder.Services.AddSingleton<PublicFlagMapCacheInvalidator>();
builder.Services.AddSingleton<PublicLogoCache>();
builder.Services.AddDbContext<PfohDbContext>((sp, options) =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");
    options.UseSqlServer(connectionString, sqlServerOptionsAction: sql =>
        sql.EnableRetryOnFailure(
            maxRetryCount: ProfileSqlRetry.MaxRetryCount,
            maxRetryDelay: ProfileSqlRetry.MaxRetryDelay,
            errorNumbersToAdd: null));
    options.AddInterceptors(sp.GetRequiredService<PublicFlagMapCacheInvalidator>());
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://localhost:3000" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .WithExposedHeaders(DuplicateReviewGroups.OpenGroupCountHeader)
            .AllowAnyMethod();
    });
});

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
// AddApplicationInsightsTelemetry inserts a provider-wide Warning rule at the
// front of the filter list. Logging:ApplicationInsights:LogLevel is more specific
// and is what turns EF categories off. A later AddFilter for those categories
// would outrank that config, so this file does not add one.
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    // The built-in sampler is replaced after the app is built so the cap
    // comes from configuration. Exception and Event stay out of that cap.
    options.EnableAdaptiveSampling = false;
});
builder.Services.ConfigureTelemetryModule<DependencyTrackingTelemetryModule>((module, _) =>
{
    module.EnableSqlCommandTextInstrumentation = false;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITelemetryInitializer, FailureRetentionTelemetryInitializer>();
builder.Services.AddSingleton<ITelemetryInitializer, SubmitterLookupTelemetryInitializer>();
// Initializers run before the sampling processor built after the app starts.
builder.Services.AddSingleton<ITelemetryInitializer, AdminIdentityTelemetryInitializer>();
builder.Services.AddSingleton<IHttpContextFactory>(serviceProvider =>
    new SubmitterLookupHttpContextFactory(ActivatorUtilities.CreateInstance<DefaultHttpContextFactory>(serviceProvider)));
builder.Services.AddSingleton<HonoreeFileStorage>();
builder.Services.AddSingleton<ProfileStoreCircuit>();
builder.Services.AddHealthChecks()
    .AddCheck<ProfileStoreHealthCheck>("profile-store", tags: ["ready"]);
builder.Services.AddScoped<UserProfileService>();
builder.Services.AddScoped<FlagGridAssignment>();
builder.Services.AddScoped<PendingReviewMaintenance>();
builder.Services.AddScoped<ReprintQueueMaintenance>();
builder.Services.AddScoped<PublicFlagMap>();
builder.Services.AddSingleton<SubmitterSuggestionTokens>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // The framework sets the status code only when OnRejected is unset, and it
    // does not write Retry-After. Keep the empty 429 and add the wait hint.
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            if (seconds < 1)
            {
                seconds = 1;
            }

            context.HttpContext.Response.Headers.RetryAfter =
                seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
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
    options.AddPolicy("PublicFlagMapBySubmitter", httpContext =>
    {
        var ip = PublicFlagMapClientIp.Resolve(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    options.AddPolicy("PublicFlagMapSubmitterSearch", httpContext =>
    {
        var ip = PublicFlagMapClientIp.Resolve(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    options.AddPolicy("PublicFlagMapKioskExit", httpContext =>
    {
        var ip = PublicFlagMapClientIp.Resolve(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    // Signed-in reads only. Health, version, Search, and the public Flag Map
    // do not use this policy.
    options.AddPolicy("AuthenticatedGet", httpContext =>
    {
        if (!HttpMethods.IsGet(httpContext.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter("write");
        }

        var userId = httpContext.User.FindFirst("oid")?.Value
            ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return RateLimitPartition.GetNoLimiter("anonymous");
        }

        var permit = httpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:AuthenticatedGetPermit", 60);
        if (permit < 1)
        {
            permit = 60;
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            userId,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

var app = builder.Build();

var telemetryConfiguration = app.Services.GetRequiredService<TelemetryConfiguration>();
var telemetryChain = telemetryConfiguration.DefaultTelemetrySink.TelemetryProcessorChainBuilder;
TelemetryIngestion.ConfigureProcessorChain(
    telemetryChain,
    TelemetryIngestion.ReadMaxTelemetryItemsPerSecond(app.Configuration),
    TelemetryIngestion.ReadSuccessfulDependencyDropBelow(app.Configuration));
telemetryChain.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (ex is ProfileStoreUnavailableException or RetryLimitExceededException)
    {
        if (context.Response.HasStarted)
        {
            throw;
        }

        if (ex is RetryLimitExceededException)
        {
            app.Logger.LogError(
                "SQL retries exhausted. ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
                ex.GetType().FullName,
                UserProfileService.SqlErrorNumber(ex));
        }

        var retryAfter = ex is ProfileStoreUnavailableException unavailable
            ? unavailable.RetryAfter
            : TimeSpan.FromSeconds(5);
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        if (seconds < 1)
        {
            seconds = 1;
        }

        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { message = "The profile store is unavailable." });
    }
});

app.Use(async (context, next) =>
{
    if (SubmitterLookupPrivacy.IsLookupPath(context.Request.Path))
    {
        context.Response.OnStarting(() =>
        {
            SubmitterLookupPrivacy.ApplyResponseHeaders(context.Response);
            return Task.CompletedTask;
        });
    }

    await next();
});

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

app.UseResponseCompression();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var html = path.Equals("/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/flag-map", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        if (!html)
        {
            return Task.CompletedTask;
        }

        var existing = context.Response.Headers.CacheControl.ToString();
        if (existing.Contains("no-store", StringComparison.OrdinalIgnoreCase)
            || existing.Contains("immutable", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(existing))
        {
            return Task.CompletedTask;
        }

        context.Response.Headers.CacheControl = StaticAssetCaching.Html;
        return Task.CompletedTask;
    });
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var cache = StaticAssetCaching.CacheControlFor(context.Context.Request.Path.Value);
        if (cache is not null)
        {
            context.Context.Response.Headers.CacheControl = cache;
        }
    }
});
app.UseRouting();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api/public/flag-map", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }

    await next();
});
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<UserProfileProvisioningMiddleware>();
// Liveness has no dependency checks. App Service must not recycle the only
// instance when the database is down. Readiness is /health/ready.
// Both use the built-in plain-text writer (Healthy / Degraded / Unhealthy).
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = static _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains("ready")
}).AllowAnonymous();
app.MapGet("/flag-map", async (HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = StaticAssetCaching.Html;
    var indexPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
    if (!File.Exists(indexPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var html = await File.ReadAllTextAsync(indexPath, ct);
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(FlagMapDocument.WithPreload(html), ct);
}).AllowAnonymous();
app.MapControllers();
app.MapFallback("/api", UnknownApi.Respond);
app.MapFallback("/api/{**path}", UnknownApi.Respond);
app.MapFallbackToFile("index.html");

try
{
    // SQL Server startup does not use the request retry. A separate context
    // has no execution-strategy retry. StartupDatabase.Create applies
    // StartupDatabase.ConnectTimeout (4 seconds) and the command timeout.
    // Nine steps plus per-step overhead are StartupDatabase.ExpectedWhenDatabaseHangs
    // (about 38 seconds) when the endpoint accepts and never answers.
    // Tests that replace the provider keep their own context.
    await using var scope = app.Services.CreateAsyncScope();
    var configured = scope.ServiceProvider.GetRequiredService<PfohDbContext>();
    var sqlServer = configured.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;
    var startupConnection = app.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");
    await using var startupOwned = sqlServer ? StartupDatabase.Create(startupConnection) : null;
    var startupDb = startupOwned ?? configured;
    var startupMaintenance = startupOwned is null
        ? scope.ServiceProvider.GetRequiredService<PendingReviewMaintenance>()
        : new PendingReviewMaintenance(startupDb, scope.ServiceProvider.GetRequiredService<HonoreeFileStorage>());
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
        await HonoreeClaimSchema.EnsureAsync(startupDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while ensuring dbo.HonoreeClaims. {Failure}",
            ex.ToString());
    }

    try
    {
        await ReprintDuplicateReviewSchema.EnsureAsync(startupDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while ensuring dbo.ReprintDuplicateReviews. {Failure}",
            ex.ToString());
    }

    try
    {
        await HonoreeNotDuplicatePairSchema.EnsureAsync(startupDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while ensuring dbo.HonoreeNotDuplicatePairs. {Failure}",
            ex.ToString());
    }

    try
    {
        await HonoreeMergeAuditSchema.EnsureAsync(startupDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while ensuring dbo.HonoreeMergeAudits. {Failure}",
            ex.ToString());
    }

    try
    {
        await CardPrintEventSchema.EnsureAsync(startupDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while ensuring dbo.CardPrintEvents. {Failure}",
            ex.ToString());
    }

    try
    {
        await FlagRemovalQueueSchema.EnsureAsync(startupDb, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "STARTUP CLEANUP FAILED while ensuring dbo.FlagRemovalQueueItems. {Failure}",
            ex.ToString());
    }

    try
    {
        await UserProfileSchema.EnsureAsync(startupDb, app.Logger, CancellationToken.None);
    }
    catch (Exception ex)
    {
        UserProfileService.LogStartupIndexFailure(app.Logger, ex);
    }

    try
    {
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

static class UnknownApi
{
    public static IResult Respond(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Remove("ETag");
            return Task.CompletedTask;
        });
        return Results.Problem(title: "Not found", statusCode: StatusCodes.Status404NotFound);
    }
}

public partial class Program
{
}
