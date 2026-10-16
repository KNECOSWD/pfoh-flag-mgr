using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PFOH.Api.Services;

public sealed class SubmitterSuggestionTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Entry> entries = new();

    public string Issue(string normalizedFirst, string normalizedLast)
    {
        Prune();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        entries[token] = new Entry(normalizedFirst, normalizedLast, DateTimeOffset.UtcNow.Add(Lifetime));
        return token;
    }

    public bool TryRead(string? token, out string normalizedFirst, out string normalizedLast)
    {
        normalizedFirst = string.Empty;
        normalizedLast = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64)
        {
            return false;
        }

        if (!entries.TryGetValue(token, out var entry) || entry.Expires <= DateTimeOffset.UtcNow)
        {
            if (token is not null)
            {
                entries.TryRemove(token, out _);
            }

            return false;
        }

        normalizedFirst = entry.First;
        normalizedLast = entry.Last;
        return true;
    }

    private void Prune()
    {
        if (entries.Count < 2000)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in entries)
        {
            if (pair.Value.Expires <= now)
            {
                entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Entry(string First, string Last, DateTimeOffset Expires);
}
