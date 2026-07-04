namespace HomeHarbor.Core.RemoteAccess;

public sealed record WireGuardPeer(
    Guid Id,
    Guid FamilyId,
    string Name,
    string PublicKey,
    string Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastHandshakeAt);

