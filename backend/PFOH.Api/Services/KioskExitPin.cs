using System.Security.Cryptography;
using System.Text;

namespace PFOH.Api.Services;

internal static class KioskExitPin
{
    public static string? ConfiguredValue(IConfiguration configuration)
    {
        var value = configuration["PublicFlagMap:KioskExitPin"];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool Matches(IConfiguration configuration, string? submitted)
    {
        var configured = ConfiguredValue(configuration);
        if (configured is null)
        {
            return false;
        }

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(submitted?.Trim() ?? ""));
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
