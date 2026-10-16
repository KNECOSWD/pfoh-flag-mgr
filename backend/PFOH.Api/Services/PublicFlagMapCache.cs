using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PFOH.Api.Services;

public sealed class PublicFlagMapSnapshot
{
    public required string SectionsJson { get; init; }
    public required string SectionsEtag { get; init; }
    public required string SearchJson { get; init; }
    public required string SearchEtag { get; init; }
    public required string FieldJson { get; init; }
    public required string FieldEtag { get; init; }
    public required IReadOnlyDictionary<string, (string Json, string Etag)> Sections { get; init; }
    public required IReadOnlyList<string?> StoredLogoFileNames { get; init; }
}

public sealed class PublicFlagMapCache(IServiceScopeFactory scopes)
{
    public const int TtlSeconds = 60;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly object gate = new();
    private PublicFlagMapSnapshot? snapshot;
    private long generation;
    private long cachedGeneration = -1;
    private DateTimeOffset cachedUntil;

    public int Builds { get; private set; }

    public void Invalidate()
    {
        Interlocked.Increment(ref generation);
    }

    public async Task<PublicFlagMapSnapshot> GetAsync(CancellationToken ct)
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
                var built = await map.BuildSnapshotAsync(ct);
                Builds++;
                if (Volatile.Read(ref generation) != seen)
                {
                    hit = built;
                    continue;
                }

                lock (gate)
                {
                    snapshot = built;
                    cachedGeneration = seen;
                    cachedUntil = DateTimeOffset.UtcNow.AddSeconds(TtlSeconds);
                }

                return built;
            }

            return hit ?? throw new InvalidOperationException("The flag map snapshot could not be built.");
        }
        finally
        {
            refresh.Release();
        }
    }

    private PublicFlagMapSnapshot? Read()
    {
        lock (gate)
        {
            if (snapshot is null || cachedGeneration != Volatile.Read(ref generation) || DateTimeOffset.UtcNow >= cachedUntil)
            {
                return null;
            }

            return snapshot;
        }
    }
}

public sealed class PublicFlagMapCacheInvalidator(PublicFlagMapCache cache) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (TouchesMap(eventData.Context))
        {
            cache.Invalidate();
        }

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (TouchesMap(eventData.Context))
        {
            cache.Invalidate();
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static bool TouchesMap(DbContext? context)
    {
        if (context is null)
        {
            return false;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Entity is Models.Honoree or Models.FlagGrid or Models.ServiceBranch or Models.ServiceBranchCategory)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class PublicLogoCache
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string Etag, string ContentType)> items = new(StringComparer.Ordinal);

    public (byte[] Bytes, string Etag, string ContentType) Get(string fileName, int edge, bool webp, byte[] source)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(source));
        var key = $"{fileName}|{edge}|{(webp ? "webp" : "png")}|{sourceHash}";
        return items.GetOrAdd(key, _ => Encode(fileName, edge, webp, source, sourceHash));
    }

    private static (byte[] Bytes, string Etag, string ContentType) Encode(string fileName, int edge, bool webp, byte[] source, string sourceHash)
    {
        if (DisplayImage.TryEncodeLogo(source, edge, webp, out var encoded))
        {
            var hash = Convert.ToHexString(SHA256.HashData(encoded));
            return (encoded, $"\"{hash}\"", webp ? "image/webp" : LogoContentType(fileName));
        }

        return (source, $"\"{sourceHash}\"", LogoContentType(fileName));
    }

    public static string ContentTypeFor(string fileName, bool webp)
    {
        if (webp)
        {
            return "image/webp";
        }

        return LogoContentType(fileName);
    }

    public static string LogoContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }
}
