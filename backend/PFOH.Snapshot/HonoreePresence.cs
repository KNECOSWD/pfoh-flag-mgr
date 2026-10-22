namespace PFOH.Snapshot;

/// <summary>
/// Derived boolean flags. The source text and photo name are not written to the snapshot.
/// </summary>
public static class HonoreePresence
{
    private static readonly HashSet<string> EmptyText = new(StringComparer.Ordinal)
    {
        "no information provided",
        "n/a",
        "none",
        "unknown"
    };

    public static bool HasPhoto(object? photoFileName)
    {
        var value = AsText(photoFileName);
        if (value.Length == 0)
        {
            return false;
        }

        return !value.StartsWith("silhouette", StringComparison.Ordinal);
    }

    public static bool HasDescription(object? value) => HasMeaningfulText(value);

    public static bool HasAwards(object? value) => HasMeaningfulText(value);

    public static bool HasConflictsServed(object? value) => HasMeaningfulText(value);

    public static bool HasMeaningfulText(object? value)
    {
        var text = AsText(value);
        return text.Length > 0 && !EmptyText.Contains(text);
    }

    private static string AsText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text.Trim().ToLowerInvariant(),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant() ?? string.Empty
        };
    }
}
