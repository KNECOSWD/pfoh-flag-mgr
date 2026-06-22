using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using PFOH.Api.Data;

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
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "PFOH.Api" })).AllowAnonymous();
app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
