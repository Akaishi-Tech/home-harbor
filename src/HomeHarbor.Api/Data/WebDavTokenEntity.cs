using HomeHarbor.Core.Identity;

namespace HomeHarbor.Api.Data;

public sealed class WebDavTokenEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public WebDavTokenScope Scope { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

