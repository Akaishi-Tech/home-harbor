namespace HomeHarbor.Api.Services;

public sealed class HomeHarborApiOptions
{
    public const string SectionName = "HomeHarbor:Api";

    public string? UnixSocketPath { get; set; }

    public string HttpUpstream { get; set; } = "127.0.0.1:5181";

    public string PublicOrigin { get; set; } = "https://homeharbor.local";

    public string CaddyUpstream
        => string.IsNullOrWhiteSpace(UnixSocketPath)
            ? HttpUpstream
            : $"unix/{UnixSocketPath.Trim()}";
}
