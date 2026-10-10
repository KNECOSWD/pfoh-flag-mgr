using System.Text.RegularExpressions;

namespace PFOH.Api.Services;

/// <summary>
/// E.164 phone numbers: a leading +, a country code that does not start with 0, and 2 to 15 digits total.
/// Spaces, hyphens, parentheses, and dots are removed before the check.
/// </summary>
public static partial class E164Phone
{
    public const string Example = "+19725550100";

    [GeneratedRegex(@"^\+[1-9]\d{1,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"[\s().\-]", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 32)
        {
            return false;
        }

        var compact = Separators().Replace(trimmed, "");
        if (!Pattern().IsMatch(compact))
        {
            return false;
        }

        normalized = compact;
        return true;
    }
}
