using System.Security.Cryptography;

namespace PFOH.Api.Services;

/// <summary>
/// Creates a URL-safe token and compares only its SHA-256 hash.
/// The raw token is never stored and never written to a log.
/// </summary>
public static class ReactivationTokenSecret
{
    public const int ByteLength = 32;

    internal static readonly byte[] DummyHash = new byte[32];

    public static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(ByteLength);
        return ToBase64Url(bytes);
    }

    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = DummyHash;
        if (string.IsNullOrWhiteSpace(token) || !TryDecode(token.Trim(), out var bytes) || bytes.Length != ByteLength)
        {
            return false;
        }

        hash = SHA256.HashData(bytes);
        return true;
    }

    public static bool HashEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);

    public static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecode(string token, out byte[] bytes)
    {
        bytes = [];
        if (token.Length == 0 || token.Any(ch => !IsUrlSafe(ch)))
        {
            return false;
        }

        var padded = token.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 0:
                break;
            default:
                return false;
        }

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsUrlSafe(char ch) =>
        char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_';
}
