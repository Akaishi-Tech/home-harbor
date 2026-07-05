namespace HomeHarbor.Core.Ota;

public sealed record OtaSlot(
    string Name,
    string Version,
    bool IsActive,
    bool IsHealthy,
    string? VbmetaDigest);

public sealed record OtaManifest(
    string Version,
    string RootfsHash,
    string VbmetaAHash,
    string VbmetaBHash,
    string VbmetaADigest,
    string VbmetaBDigest,
    DateTimeOffset CreatedAt,
    string Channel,
    string Signature,
    int SchemaVersion = 1,
    string? PackageKind = null,
    string SignatureAlgorithm = "Ed25519",
    string? SigningKeyId = null,
    string? SignedPayloadSha256 = null,
    string Type = "full-system",
    string? VmlinuzHash = null,
    string? InitramfsHash = null,
    string? KernelRelease = null,
    string? KernelChannel = null,
    string? ModulesHash = null,
    string? FirmwareHash = null,
    string? RecoveryHash = null,
    string? RecoveryBootHash = null);
