namespace PFOH.Api.Services;

/// <summary>
/// The browser uses the same Application Insights resource as the API.
/// The connection string is already on the app. It is not stored in the repo.
/// </summary>
public static class BrowserTelemetryConfig
{
    public const string ConfigurationKey = "ApplicationInsights:ConnectionString";
    public const string EnvironmentKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public static string? ReadConnectionString(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var fromEnvironment = configuration[EnvironmentKey];
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return null;
    }
}
