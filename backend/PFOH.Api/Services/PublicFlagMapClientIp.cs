using System.Net;

namespace PFOH.Api.Services;

internal static class PublicFlagMapClientIp
{
    public static string Resolve(HttpContext httpContext)
    {
        // ForwardLimit 1: trust only the hop the immediate proxy appended (the right-most address).
        var forwarded = RightMostForwardedHop(httpContext.Request.Headers["X-Forwarded-For"].ToString());
        if (forwarded is not null)
        {
            return forwarded;
        }

        var remote = httpContext.Connection.RemoteIpAddress;
        return remote is null ? "unknown" : Normalize(remote);
    }

    public static bool IsUnlimited(IConfiguration configuration, string ip)
    {
        foreach (var allowed in UnlimitedIps(configuration))
        {
            if (string.Equals(allowed, ip, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string? RightMostForwardedHop(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var parts = header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        return NormalizeHop(parts[^1]);
    }

    private static IEnumerable<string> UnlimitedIps(IConfiguration configuration)
    {
        var section = configuration.GetSection("PublicFlagMap:UnlimitedIps");
        var children = section.GetChildren().ToList();
        if (children.Count > 0)
        {
            foreach (var child in children)
            {
                foreach (var ip in Split(child.Value))
                {
                    yield return ip;
                }
            }

            yield break;
        }

        foreach (var ip in Split(section.Value))
        {
            yield return ip;
        }
    }

    private static IEnumerable<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizeHop(part);
            if (normalized is not null)
            {
                yield return normalized;
            }
        }
    }

    private static string? NormalizeHop(string hop)
    {
        var value = hop.Trim().Trim('"');
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] == '[')
        {
            var end = value.IndexOf(']');
            if (end > 1 && IPAddress.TryParse(value[1..end], out var ipv6))
            {
                return Normalize(ipv6);
            }
        }

        var firstColon = value.IndexOf(':');
        var lastColon = value.LastIndexOf(':');
        if (firstColon > 0 && firstColon == lastColon && IPAddress.TryParse(value[..firstColon], out var withPort))
        {
            return Normalize(withPort);
        }

        return IPAddress.TryParse(value, out var address) ? Normalize(address) : null;
    }

    private static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }
}
