namespace PFOH.Api.Services;

public sealed class SubmitterNameTokenCache(IServiceScopeFactory scopes)
{
    public const int TtlSeconds = 60;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly object gate = new();
    private SubmitterTokenIndex? index;
    private long generation;
    private long cachedGeneration = -1;
    private DateTimeOffset cachedUntil;

    public int Builds { get; private set; }

    public void Invalidate()
    {
        Interlocked.Increment(ref generation);
    }

    public async Task<PublicFlagMap.SubmitterSuggestionResult> SuggestAsync(string? query, CancellationToken ct)
    {
        if (PublicFlagMap.SuggestionQueryOutcome(query) is string outcome)
        {
            return new PublicFlagMap.SubmitterSuggestionResult(outcome, []);
        }

        var tokens = await GetIndexAsync(ct);
        var names = tokens.Match(query, PublicFlagMap.SubmitterSuggestionLimit);
        return new PublicFlagMap.SubmitterSuggestionResult(names.Count == 0 ? "none" : "ok", names);
    }

    private async Task<SubmitterTokenIndex> GetIndexAsync(CancellationToken ct)
    {
        var hit = Read();
        if (hit is not null)
        {
            return hit;
        }

        await refresh.WaitAsync(ct);
        try
        {
            hit = Read();
            if (hit is not null)
            {
                return hit;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var seen = Volatile.Read(ref generation);
                using var scope = scopes.CreateScope();
                var map = scope.ServiceProvider.GetRequiredService<PublicFlagMap>();
                var built = SubmitterTokenIndex.Build(await map.LoadSubmitterTokensAsync(ct));
                Builds++;
                if (Volatile.Read(ref generation) != seen)
                {
                    hit = built;
                    continue;
                }

                lock (gate)
                {
                    index = built;
                    cachedGeneration = seen;
                    cachedUntil = DateTimeOffset.UtcNow.AddSeconds(TtlSeconds);
                }

                return built;
            }

            return hit ?? SubmitterTokenIndex.Build([]);
        }
        finally
        {
            refresh.Release();
        }
    }

    private SubmitterTokenIndex? Read()
    {
        lock (gate)
        {
            if (index is null || cachedGeneration != Volatile.Read(ref generation) || DateTimeOffset.UtcNow >= cachedUntil)
            {
                return null;
            }

            return index;
        }
    }
}
