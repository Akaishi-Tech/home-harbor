using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public sealed class ValkeyCacheStartupValidator(
    IDistributedCache cache,
    IOptions<HomeHarborCacheOptions> options,
    ILogger<ValkeyCacheStartupValidator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var key = $"startup:{Guid.NewGuid():N}";
        await cache.SetStringAsync(key, "ok", cancellationToken);
        await cache.RemoveAsync(key, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Connected to Valkey cache through Unix socket {UnixSocketPath}.",
                options.Value.UnixSocketPath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
