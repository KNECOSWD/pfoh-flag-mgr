namespace PFOH.Api.Services;

internal sealed class SubmitterTokenIndex
{
    private readonly Dictionary<string, PublicFlagMap.SubmitterIdentity[]> firstBuckets;
    private readonly Dictionary<string, PublicFlagMap.SubmitterIdentity[]> lastBuckets;
    private readonly PublicFlagMap.SubmitterIdentity[] all;

    private SubmitterTokenIndex(
        Dictionary<string, PublicFlagMap.SubmitterIdentity[]> firstBuckets,
        Dictionary<string, PublicFlagMap.SubmitterIdentity[]> lastBuckets,
        PublicFlagMap.SubmitterIdentity[] all)
    {
        this.firstBuckets = firstBuckets;
        this.lastBuckets = lastBuckets;
        this.all = all;
    }

    public static SubmitterTokenIndex Build(IReadOnlyList<PublicFlagMap.SubmitterIdentity> tokens)
    {
        var first = new Dictionary<string, List<PublicFlagMap.SubmitterIdentity>>(StringComparer.Ordinal);
        var last = new Dictionary<string, List<PublicFlagMap.SubmitterIdentity>>(StringComparer.Ordinal);
        var all = new List<PublicFlagMap.SubmitterIdentity>(tokens.Count);
        foreach (var token in tokens)
        {
            all.Add(token);
            Add(first, token.NormalizedFirst, token);
            Add(last, token.NormalizedLast, token);
        }

        return new SubmitterTokenIndex(Freeze(first), Freeze(last), all.ToArray());
    }

    public static IReadOnlyList<PublicFlagMap.SubmitterIdentity> Match(
        IReadOnlyList<PublicFlagMap.SubmitterIdentity> tokens,
        string? query,
        int limit)
    {
        return Build(tokens).Match(query, limit);
    }

    public IReadOnlyList<PublicFlagMap.SubmitterIdentity> Match(string? query, int limit)
    {
        if (PublicFlagMap.SuggestionQueryOutcome(query) is not null)
        {
            return [];
        }

        var trimmed = (query ?? string.Empty).Trim();
        var needle = PublicFlagMap.NormalizeName(trimmed);
        if (needle.Length < 3)
        {
            return [];
        }

        IEnumerable<PublicFlagMap.SubmitterIdentity> candidates;
        if (needle.Contains(' '))
        {
            candidates = all.Where(token =>
                PublicFlagMap.SuggestionMatches(token.NormalizedFirst, token.NormalizedLast, trimmed));
        }
        else
        {
            var prefix = needle[..3];
            var seen = new Dictionary<string, PublicFlagMap.SubmitterIdentity>(StringComparer.Ordinal);
            Take(firstBuckets, prefix, trimmed, seen);
            Take(lastBuckets, prefix, trimmed, seen);
            candidates = seen.Values;
        }

        return candidates
            .OrderBy(token => token.Display, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static void Take(
        Dictionary<string, PublicFlagMap.SubmitterIdentity[]> buckets,
        string prefix,
        string query,
        Dictionary<string, PublicFlagMap.SubmitterIdentity> seen)
    {
        if (!buckets.TryGetValue(prefix, out var bucket))
        {
            return;
        }

        foreach (var token in bucket)
        {
            if (!PublicFlagMap.SuggestionMatches(token.NormalizedFirst, token.NormalizedLast, query))
            {
                continue;
            }

            seen[token.NormalizedFirst + "\n" + token.NormalizedLast] = token;
        }
    }

    private static void Add(
        Dictionary<string, List<PublicFlagMap.SubmitterIdentity>> buckets,
        string name,
        PublicFlagMap.SubmitterIdentity token)
    {
        if (name.Length < 3)
        {
            return;
        }

        var prefix = name[..3];
        if (!buckets.TryGetValue(prefix, out var list))
        {
            list = [];
            buckets[prefix] = list;
        }

        list.Add(token);
    }

    private static Dictionary<string, PublicFlagMap.SubmitterIdentity[]> Freeze(
        Dictionary<string, List<PublicFlagMap.SubmitterIdentity>> buckets)
    {
        return buckets.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }
}
