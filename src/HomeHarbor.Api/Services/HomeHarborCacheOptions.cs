namespace HomeHarbor.Api.Services;

public sealed class HomeHarborCacheOptions
{
    public const string SectionName = "HomeHarbor:Cache";

    public string UnixSocketPath { get; set; } = "/run/valkey/homeharbor.sock";

    public string InstanceName { get; set; } = "homeharbor:";

    public int OverviewTtlSeconds { get; set; } = 30;
}
