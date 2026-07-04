using HomeHarbor.Api.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class OverviewCacheTests
{
    [TestMethod]
    public async Task GetOrCreateAsync_Caches_Response_Per_Family()
    {
        var cache = new TestDistributedCache();
        var service = CreateCache(cache);
        var familyId = Guid.NewGuid();
        var calls = 0;

        var first = await service.GetOrCreateAsync(
            familyId,
            _ =>
            {
                calls++;
                return Task.FromResult(CreateOverview(familyId, "Harbor A"));
            },
            CancellationToken.None);
        var second = await service.GetOrCreateAsync(
            familyId,
            _ =>
            {
                calls++;
                return Task.FromResult(CreateOverview(familyId, "Harbor B"));
            },
            CancellationToken.None);
        var otherFamily = Guid.NewGuid();
        _ = await service.GetOrCreateAsync(
            otherFamily,
            _ =>
            {
                calls++;
                return Task.FromResult(CreateOverview(otherFamily, "Harbor C"));
            },
            CancellationToken.None);

        Assert.AreEqual(1, first.Modules.Files.Count);
        Assert.AreEqual("Harbor A", second.Family.Name);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task InvalidateFamilyAsync_Removes_Cached_Overview()
    {
        var cache = new TestDistributedCache();
        var service = CreateCache(cache);
        var familyId = Guid.NewGuid();
        var calls = 0;

        _ = await service.GetOrCreateAsync(
            familyId,
            _ =>
            {
                calls++;
                return Task.FromResult(CreateOverview(familyId, "Before"));
            },
            CancellationToken.None);
        await service.InvalidateFamilyAsync(familyId, CancellationToken.None);
        var refreshed = await service.GetOrCreateAsync(
            familyId,
            _ =>
            {
                calls++;
                return Task.FromResult(CreateOverview(familyId, "After"));
            },
            CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.AreEqual("After", refreshed.Family.Name);
    }

    [TestMethod]
    public void ShouldUseDevelopmentMemoryFallback_Only_For_Development_When_Socket_Unavailable()
    {
        var options = new HomeHarborCacheOptions { UnixSocketPath = "/run/valkey/homeharbor.sock" };

        Assert.IsTrue(HomeHarborCacheBackend.ShouldUseDevelopmentMemoryFallback(
            new TestHostEnvironment(Environments.Development),
            options,
            _ => false));
        Assert.IsFalse(HomeHarborCacheBackend.ShouldUseDevelopmentMemoryFallback(
            new TestHostEnvironment(Environments.Development),
            options,
            _ => true));
        Assert.IsFalse(HomeHarborCacheBackend.ShouldUseDevelopmentMemoryFallback(
            new TestHostEnvironment(Environments.Production),
            options,
            _ => false));
    }

    private static OverviewCache CreateCache(IDistributedCache cache)
        => new(
            cache,
            Options.Create(new HomeHarborCacheOptions
            {
                OverviewTtlSeconds = 30
            }),
            NullLogger<OverviewCache>.Instance);

    private static OverviewResponse CreateOverview(Guid familyId, string name)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new OverviewResponse(
            true,
            new OverviewFamily(familyId, name, "Owner", now),
            new OverviewModules(
                new OverviewAreaModule(1, 10, "/dav/files/"),
                new OverviewAreaModule(2, 20, "/dav/photos/"),
                new OverviewBackupModule(3, 30, 1, new OverviewLatestBackupJob(Guid.NewGuid(), "succeeded", now)),
                new OverviewVaultModule(4, true),
                [new OverviewMediaGroup("image", 5, 50)],
                new OverviewMembersModule(6, "owner + members"),
                new OverviewDevicesModule(7, 8),
                new OverviewRemoteAccessModule(9, "wireguard"),
                new OverviewSmbModule(10, 11, "smb://homeharbor.local"),
                new OverviewRuntimeModule(12, 13, "bundled")),
            new OverviewSecurity(true, true, false),
            new OverviewStorage("healthy", now),
            new OverviewOta("0.1.0", "dev", "idle", "ready"));
    }

    private sealed class TestDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

        public byte[]? Get(string key)
            => _items.TryGetValue(key, out var value) ? [.. value] : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => Task.FromResult(Get(key));

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
            => Task.CompletedTask;

        public void Remove(string key)
            => _items.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => _items[key] = [.. value];

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "HomeHarbor.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
