namespace HomeHarbor.Api.Data;

public sealed class ReverseProxyRouteEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string UpstreamUrl { get; set; } = string.Empty;
    public bool TlsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

