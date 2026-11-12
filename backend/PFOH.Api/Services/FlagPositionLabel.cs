using System.Globalization;

namespace PFOH.Api.Services;

/// <summary>
/// Flag spots are stored as labels such as C-11. Search also accepts C11.
/// </summary>
public static class FlagPositionLabel
{
    public static bool Matches(string? query, string? flagName)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(flagName))
        {
            return false;
        }

        var needle = query.Trim();
        if (flagName.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var compactQuery = Compact(needle);
        var compactName = Compact(flagName);
        if (compactQuery.Length > 0 && compactQuery == compactName)
        {
            return true;
        }

        var left = Key(needle);
        var right = Key(flagName);
        return left is not null && left == right;
    }

    internal static string? Key(string value)
    {
        var trimmed = value.Trim().ToUpperInvariant();
        var letters = new string(trimmed.TakeWhile(character => !char.IsDigit(character)).Where(char.IsLetter).ToArray());
        var digits = new string(trimmed.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        if (letters.Length == 0 || digits.Length == 0)
        {
            return null;
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? letters + number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string Compact(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        return new string(chars);
    }
}
