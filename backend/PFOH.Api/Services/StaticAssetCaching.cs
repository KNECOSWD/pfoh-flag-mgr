namespace PFOH.Api.Services;

public static class StaticAssetCaching
{
    public const string ImmutableYear = "public, max-age=31536000, immutable";
    public const string Html = "no-cache";

    public static bool IsHashedAsset(string? path)
    {
        if (string.IsNullOrEmpty(path) ||
            !path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        if (dash <= 0 || dash >= name.Length - 1)
        {
            return false;
        }

        var hash = name[(dash + 1)..];
        if (hash.Length < 8)
        {
            return false;
        }

        foreach (var character in hash)
        {
            if (!char.IsLetterOrDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    public static string? CacheControlFor(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/", StringComparison.OrdinalIgnoreCase))
        {
            return Html;
        }

        return IsHashedAsset(path) ? ImmutableYear : null;
    }
}

public static class FlagMapDocument
{
    public const string SectionsPreload = "<link rel=\"preload\" href=\"/api/public/flag-map/sections\" as=\"fetch\" type=\"application/json\" crossorigin=\"anonymous\">";
    public const string SearchPreload = "<link rel=\"preload\" href=\"/api/public/flag-map/search-index\" as=\"fetch\" type=\"application/json\" crossorigin=\"anonymous\">";

    public static string WithPreload(string html)
    {
        if (html.Contains("/api/public/flag-map/sections", StringComparison.Ordinal))
        {
            return html;
        }

        var links = SectionsPreload + SearchPreload;
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (head < 0)
        {
            return links + html;
        }

        return html.Insert(head, links);
    }
}
