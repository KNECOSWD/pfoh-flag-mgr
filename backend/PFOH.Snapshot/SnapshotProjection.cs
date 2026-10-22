using System.Globalization;

namespace PFOH.Snapshot;

public static class SnapshotProjection
{
    private static readonly HashSet<string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "binary",
        "varbinary",
        "image",
        "timestamp",
        "rowversion"
    };

    public static bool IsBinaryType(string? typeName)
    {
        return typeName is not null && BinaryTypes.Contains(typeName);
    }

    public static object? Normalize(object? value, string? typeName)
    {
        if (IsBinaryType(typeName) || value is byte[])
        {
            return Skip.Instance;
        }

        return value switch
        {
            null or DBNull => null,
            string text => Truncate(text),
            bool flag => flag,
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            DateTime date => date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            DateTimeOffset stamp => stamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Guid id => id.ToString("D"),
            _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    public static string Truncate(string value)
    {
        return value.Length <= SnapshotFieldSpec.StringMaxLength
            ? value
            : value[..SnapshotFieldSpec.StringMaxLength];
    }

    public sealed class Skip
    {
        public static readonly Skip Instance = new();
        private Skip()
        {
        }
    }
}
