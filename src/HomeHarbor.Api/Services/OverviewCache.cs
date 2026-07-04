using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public interface IOverviewCache : IOverviewCacheInvalidator
{
    Task<OverviewResponse> GetOrCreateAsync(
        Guid familyId,
        Func<CancellationToken, Task<OverviewResponse>> factory,
        CancellationToken cancellationToken);
}

public interface IOverviewCacheInvalidator
{
    Task InvalidateFamilyAsync(Guid familyId, CancellationToken cancellationToken);
}

public sealed class OverviewCache(
    IDistributedCache cache,
    IOptions<HomeHarborCacheOptions> options,
    ILogger<OverviewCache> logger) : IOverviewCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HomeHarborCacheOptions _options = options.Value;

    public async Task<OverviewResponse> GetOrCreateAsync(
        Guid familyId,
        Func<CancellationToken, Task<OverviewResponse>> factory,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(familyId);
        var cached = await cache.GetStringAsync(key, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var overview = JsonSerializer.Deserialize<OverviewResponse>(cached, JsonOptions);
            if (overview is not null) return overview;
        }

        var created = await factory(cancellationToken);
        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(created, JsonOptions),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, _options.OverviewTtlSeconds))
            },
            cancellationToken);
        return created;
    }

    public async Task InvalidateFamilyAsync(Guid familyId, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(CacheKey(familyId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate overview cache for family {FamilyId}.", familyId);
        }
    }

    private static string CacheKey(Guid familyId) => $"overview:{familyId:N}";
}
