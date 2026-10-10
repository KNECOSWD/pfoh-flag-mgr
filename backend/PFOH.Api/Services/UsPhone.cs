namespace PFOH.Api.Services;

/// <summary>
/// US mobile numbers. Common local formats are accepted and stored as +1 and 10 digits.
/// Area code and exchange must start with 2-9. N11 area codes such as 911 and 411 are rejected.
/// </summary>
public static class UsPhone
{
    public const string InvalidMessage = "Please enter a 10-digit US mobile number, like 214-555-1234.";
    public const string ExampleStored = "+12145551234";
    public const string ExampleDisplay = "(214) 555-1234";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryNationalDigits(value, out var digits))
        {
            return false;
        }

        normalized = "+1" + digits;
        return true;
    }

    public static string Format(string? value)
    {
        if (!TryNationalDigits(value, out var digits))
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        return $"({digits[..3]}) {digits[3..6]}-{digits[6..]}";
    }

    private static bool TryNationalDigits(string? value, out string digits)
    {
        digits = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 32 || !HasOnlyPhoneCharacters(trimmed) || !PlusIsCountryPrefix(trimmed))
        {
            return false;
        }

        var numeric = new string(trimmed.Where(static character => character is >= '0' and <= '9').ToArray());
        if (numeric.Length == 11 && numeric[0] == '1')
        {
            numeric = numeric[1..];
        }

        if (numeric.Length != 10 || numeric[0] is < '2' or > '9' || numeric[3] is < '2' or > '9')
        {
            return false;
        }

        if (numeric[1] == '1' && numeric[2] == '1')
        {
            return false;
        }

        digits = numeric;
        return true;
    }

    private static bool HasOnlyPhoneCharacters(string value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or ' ' or '-' or '(' or ')' or '.' or '+'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PlusIsCountryPrefix(string value)
    {
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return true;
        }

        return plus == 0 && value.IndexOf('+', 1) < 0;
    }
}
