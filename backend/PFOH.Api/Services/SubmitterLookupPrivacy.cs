using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http.Features;

namespace PFOH.Api.Services;

public static class SubmitterLookupPrivacy
{
    public const string Path = "/api/public/flag-map/by-submitter";
    public const string SuggestionsPath = "/api/public/flag-map/submitter-suggestions";
    public const string FlagsPath = "/api/public/flag-map/submitter-flags";

    public static bool IsLookupPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return string.Equals(value, Path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, SuggestionsPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, FlagsPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void RedactRequestTarget(HttpContext context)
    {
        if (!IsLookupPath(context.Request.Path) || !context.Request.QueryString.HasValue)
        {
            return;
        }

        context.Request.QueryString = QueryString.Empty;
    }

    public static void ApplyResponseHeaders(HttpResponse response)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Headers.Remove("ETag");
    }
}

public sealed class SubmitterLookupHttpContextFactory(IHttpContextFactory inner) : IHttpContextFactory
{
    public HttpContext Create(IFeatureCollection featureCollection)
    {
        var context = inner.Create(featureCollection);
        SubmitterLookupPrivacy.RedactRequestTarget(context);
        return context;
    }

    public void Dispose(HttpContext httpContext) => inner.Dispose(httpContext);
}

public sealed class SubmitterLookupTelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is not RequestTelemetry request)
        {
            return;
        }

        var path = request.Url?.AbsolutePath ?? string.Empty;
        var name = request.Name ?? string.Empty;
        var namedPath = (name.Split(' ').LastOrDefault() ?? string.Empty).Split('?')[0];
        if (!SubmitterLookupPrivacy.IsLookupPath(new PathString(path)) &&
            !SubmitterLookupPrivacy.IsLookupPath(new PathString(namedPath)))
        {
            return;
        }

        if (request.Url is not null && !string.IsNullOrEmpty(request.Url.Query))
        {
            request.Url = new UriBuilder(request.Url) { Query = string.Empty }.Uri;
        }

        var queryAt = name.IndexOf('?');
        if (queryAt >= 0)
        {
            request.Name = name[..queryAt];
        }

        foreach (var key in request.Properties.Keys.ToArray())
        {
            var value = request.Properties[key] ?? string.Empty;
            if (key.Contains("body", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                value.Contains('@') ||
                value.Contains("first=", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("last=", StringComparison.OrdinalIgnoreCase))
            {
                request.Properties.Remove(key);
            }
        }
    }
}
