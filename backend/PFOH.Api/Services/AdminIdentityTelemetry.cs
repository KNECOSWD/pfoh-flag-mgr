using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PFOH.Api.Services;

/// <summary>
/// Removes admin search text and person ids from automatic Application Insights
/// telemetry. Initializers run before sampling, so a redacted item is what the
/// sampler and the channel see.
/// </summary>
public sealed class AdminIdentityTelemetryInitializer : ITelemetryInitializer
{
    public const string IdToken = "{id}";

    private const string SearchPath = "/api/admin/users/search";
    private const string GraphHost = "graph.microsoft.com";

    private static readonly Regex GuidPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AssignmentSegmentPattern = new(
        @"(appRoleAssignedTo/|appRoleAssignments/)[^/?#]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OpaqueSegmentPattern = new(
        @"(?<=/)[A-Za-z0-9_-]{20,}(?=/|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphUrlPattern = new(
        @"https://graph\.microsoft\.com\S*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SearchRequestPattern = new(
        @"(/api/admin/users/search)(?:\?[^\s""']*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AdminRolePattern = new(
        @"(/api/admin/users/)[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(/admin-role)(?:\?[^\s""']*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReactivationLinkPattern = new(
        @"(/api/reactivate/|/reactivate/)[A-Za-z0-9_-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProfilePattern = new(
        @"(/api/profile/)[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?:\?[^\s""']*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IHttpContextAccessor? httpContextAccessor;

    public AdminIdentityTelemetryInitializer()
    {
    }

    public AdminIdentityTelemetryInitializer(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    internal bool HasHttpContextAccessor => httpContextAccessor is not null;

    public void Initialize(ITelemetry telemetry)
    {
        try
        {
            Redact(telemetry);
        }
        catch
        {
            // A failed redact must not send the raw item, and must not escape.
            Blank(telemetry);
        }
    }

    private void Redact(ITelemetry telemetry)
    {
        var template = SensitiveRouteTemplate();
        switch (telemetry)
        {
            case RequestTelemetry request:
                RedactRequest(request, template);
                break;
            case DependencyTelemetry dependency:
                RedactDependency(dependency);
                break;
            case ExceptionTelemetry exception:
                RedactException(exception);
                break;
            case TraceTelemetry trace:
                RedactTrace(trace);
                break;
        }

        if (template is not null)
        {
            telemetry.Context.Operation.Name = template;
        }
        else if (!string.IsNullOrEmpty(telemetry.Context.Operation.Name))
        {
            telemetry.Context.Operation.Name = RedactText(telemetry.Context.Operation.Name);
        }

        if (telemetry is ISupportProperties withProperties)
        {
            RedactProperties(withProperties.Properties);
        }
    }

    private void RedactRequest(RequestTelemetry request, string? template)
    {
        if (request.Url is { IsAbsoluteUri: true } url && IsSensitiveApiPath(url.AbsolutePath))
        {
            var redactedPath = RedactText(url.AbsolutePath);
            request.Url = new UriBuilder(url)
            {
                Path = string.IsNullOrEmpty(redactedPath) ? url.AbsolutePath : redactedPath,
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;
        }

        request.Name = template ?? RedactText(request.Name);
    }

    private static void RedactDependency(DependencyTelemetry dependency)
    {
        if (!IsGraphDependency(dependency))
        {
            return;
        }

        var path = ToRedactedPath(GraphSource(dependency));
        dependency.Data = path;
        dependency.Name = path;
        dependency.Target = GraphHost;
    }

    private static void RedactException(ExceptionTelemetry telemetry)
    {
        if (telemetry.Message is not null)
        {
            telemetry.Message = RedactText(telemetry.Message);
        }

        if (telemetry.Exception is not { } exception)
        {
            return;
        }

        var original = exception.ToString();
        var redacted = RedactText(original);
        if (!string.Equals(original, redacted, StringComparison.Ordinal))
        {
            telemetry.Exception = new InvalidOperationException(redacted);
        }
    }

    private static void RedactTrace(TraceTelemetry telemetry)
    {
        if (telemetry.Message is not null)
        {
            telemetry.Message = RedactText(telemetry.Message);
        }
    }

    private string? SensitiveRouteTemplate()
    {
        var context = httpContextAccessor?.HttpContext;
        if (context?.GetEndpoint() is not RouteEndpoint endpoint)
        {
            return null;
        }

        var raw = endpoint.RoutePattern.RawText;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var path = raw.StartsWith('/') ? raw : "/" + raw;
        var isSearch = path.Contains(SearchPath, StringComparison.OrdinalIgnoreCase);
        var isRole = path.Contains("/api/admin/users/", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/admin-role", StringComparison.OrdinalIgnoreCase);
        var isProfile = path.Contains("/api/profile/", StringComparison.OrdinalIgnoreCase);
        var isReactivation = path.Contains("/reactivate/", StringComparison.OrdinalIgnoreCase);
        if (!isSearch && !isRole && !isProfile && !isReactivation)
        {
            return null;
        }

        var method = context.Request.Method;
        return string.IsNullOrEmpty(method) ? raw : method + " " + raw;
    }

    private static bool IsSensitiveApiPath(string path)
    {
        return IsAdminSearchPath(path)
            || AdminRolePattern.IsMatch(path)
            || ProfilePattern.IsMatch(path)
            || path.Contains("/reactivate/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdminSearchPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var value = path;
        var query = value.IndexOf('?');
        if (query >= 0)
        {
            value = value[..query];
        }

        value = value.TrimEnd('/');
        return value.EndsWith(SearchPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraphDependency(DependencyTelemetry dependency)
    {
        return ContainsGraphHost(dependency.Data)
            || ContainsGraphHost(dependency.Name)
            || ContainsGraphHost(dependency.Target);
    }

    private static bool ContainsGraphHost(string? value)
    {
        return value?.Contains(GraphHost, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? GraphSource(DependencyTelemetry dependency)
    {
        var values = new[] { dependency.Data, dependency.Name, dependency.Target };
        foreach (var candidate in values)
        {
            if (HasGraphPath(candidate))
            {
                return candidate;
            }
        }

        return values.FirstOrDefault(ContainsGraphHost);
    }

    private static bool HasGraphPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains(GraphHost + "/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/v1.0/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/beta/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var text = ReactivationLinkPattern.Replace(value, "$1{token}");
        text = GraphUrlPattern.Replace(text, match => ToRedactedPath(match.Value.TrimEnd('.', ',', ';', ')', ']')));
        text = SearchRequestPattern.Replace(text, "$1");
        text = AdminRolePattern.Replace(text, "$1" + IdToken + "$2");
        text = ProfilePattern.Replace(text, "$1" + IdToken);
        return text;
    }

    private static string ToRedactedPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "/";
        }

        var text = value.Trim();
        var space = text.IndexOf(' ');
        if (space is > 0 and <= 7 && text.Take(space).All(char.IsLetter))
        {
            text = text[(space + 1)..].TrimStart();
        }

        string path;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }
        else
        {
            var query = text.IndexOf('?');
            path = query >= 0 ? text[..query] : text;
        }

        path = AssignmentSegmentPattern.Replace(path, "$1" + IdToken);
        path = OpaqueSegmentPattern.Replace(path, IdToken);
        path = GuidPattern.Replace(path, IdToken);
        if (path.Length == 0)
        {
            return "/";
        }

        return path.StartsWith('/') ? path : "/" + path;
    }

    private static void RedactProperties(IDictionary<string, string> properties)
    {
        foreach (var key in properties.Keys.ToArray())
        {
            properties[key] = RedactText(properties[key]);
        }
    }

    private static void Blank(ITelemetry telemetry)
    {
        try
        {
            switch (telemetry)
            {
                case RequestTelemetry request:
                    request.Url = null;
                    request.Name = string.Empty;
                    break;
                case DependencyTelemetry dependency:
                    dependency.Name = string.Empty;
                    dependency.Data = string.Empty;
                    dependency.Target = string.Empty;
                    break;
                case ExceptionTelemetry exception:
                    exception.Message = string.Empty;
                    exception.Exception = new InvalidOperationException(string.Empty);
                    break;
                case TraceTelemetry trace:
                    trace.Message = string.Empty;
                    break;
            }

            telemetry.Context.Operation.Name = string.Empty;
            if (telemetry is ISupportProperties withProperties)
            {
                foreach (var key in withProperties.Properties.Keys.ToArray())
                {
                    withProperties.Properties[key] = string.Empty;
                }
            }

            foreach (var key in telemetry.Context.GlobalProperties.Keys.ToArray())
            {
                telemetry.Context.GlobalProperties[key] = string.Empty;
            }
        }
        catch
        {
            // Blanking is the last resort. Never rethrow the original item.
        }
    }
}
