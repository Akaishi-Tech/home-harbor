using HomeHarbor.Api.Data;
using HomeHarbor.Core.Storage;

namespace HomeHarbor.Api.Services;

public interface ITokenGenerator
{
    string GenerateUsername(string prefix = "hh");
    string GenerateSecret(int byteLength = 32);
    string GenerateRecoveryCode();
}

public interface IJwtTokenService
{
    TimeSpan UserAccessTokenLifetime { get; }
    string GenerateTokenId();
    string IssueUserAccessToken(
        MemberSessionEntity session,
        FamilyMemberEntity member,
        FamilySpaceEntity family,
        string tokenId);
    string IssueAutomationToken();
    Task WriteAutomationTokenAsync(CancellationToken cancellationToken = default);
}

public interface ISetupPairingService
{
    SetupPairingTicket GetOrCreate(string publicOrigin, Guid familyId);
    bool IsBootstrapComplete();
    bool IsBootstrapCodeValid(string? code);
    bool IsDeviceCodeValid(string? code);
    bool TryConsumeDeviceCode(string? code, out SetupPairingTicket? ticket);
    void ConsumeBootstrapCode(string? code);
}

public interface IFamilyResolver
{
    Task<Guid?> ResolveAsync(Guid? requestedFamilyId, CancellationToken cancellationToken);
    Task RequireAccessAsync(Guid familyId, CancellationToken cancellationToken);
}

public interface IHomeHarborStorageService
{
    string DataRoot { get; }
    long MaxUploadBytes { get; }
    void EnsureFamilyRoots(Guid familyId);
    string GetAreaRoot(Guid familyId, StorageArea area);
    string Resolve(Guid familyId, StorageArea area, string? davPath);
    FileSystemInfo? Stat(Guid familyId, StorageArea area, string? davPath);
    IReadOnlyList<FileSystemInfo> Enumerate(Guid familyId, StorageArea area, string? davPath);
    IReadOnlyList<FileInfo> EnumerateFiles(Guid familyId, StorageArea area);
    FileStream OpenRead(Guid familyId, StorageArea area, string? davPath);
    Task WriteFileAsync(Guid familyId, StorageArea area, string? davPath, Stream input, CancellationToken cancellationToken);
    void CreateDirectory(Guid familyId, StorageArea area, string? davPath);
    void Delete(Guid familyId, StorageArea area, string? davPath);
    StorageTransferResult Copy(
        Guid familyId,
        StorageArea sourceArea,
        string? sourcePath,
        StorageArea destinationArea,
        string destinationPath,
        bool overwrite);
    StorageTransferResult Move(
        Guid familyId,
        StorageArea sourceArea,
        string? sourcePath,
        StorageArea destinationArea,
        string destinationPath,
        bool overwrite);
}

public interface IMediaIndexer
{
    Task<IReadOnlyList<MediaAssetEntity>> IndexAsync(Guid familyId, CancellationToken cancellationToken);
}

public interface IReverseProxyConfigService
{
    string BuildCaddyfile(IEnumerable<ReverseProxyRouteEntity> routes);
}

public interface IStorageHealthService
{
    StorageHealthEntity Check(Guid familyId);
}

public interface IStorageOobeService
{
    Task<StorageInventory> InventoryAsync(CancellationToken cancellationToken);
    StorageRecommendation Recommend(StorageInventory inventory, StorageUseProfile profile);
    Task<StoragePlan> CreatePlanAsync(
        StorageInventory inventory,
        StoragePlanRequest request,
        CancellationToken cancellationToken);
    Task<StorageApplyStatus> ApplyAsync(
        string planId,
        string confirmation,
        string? recoveryPassphrase,
        CancellationToken cancellationToken);
    Task<StorageApplyStatus> StatusAsync(CancellationToken cancellationToken);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

public interface IAppRuntimeCatalog
{
    IReadOnlyList<ManagedAppTemplate> List(string? role = null);
    ManagedAppTemplate? Find(string appKey);
    Task<IReadOnlyList<ManagedAppTemplate>> ListAsync(string? role = null, CancellationToken cancellationToken = default);
    Task<ManagedAppTemplate?> FindAsync(string appKey, CancellationToken cancellationToken = default);
}

public interface ISmbConfigService
{
    string BuildSmbConf(
        IEnumerable<SmbShareEntity> shares,
        IEnumerable<SmbCredentialEntity> credentials);
}

public interface IRuntimeSignalService
{
    void RequestSmbApply();
    void RequestContainerApply();
    void RequestSystemAppApply();
    void RequestCaddyRender();
    Task WriteSmbPasswordAsync(
        Guid credentialId,
        string username,
        string unixUser,
        string password,
        CancellationToken cancellationToken);
    Task WriteSmbRevokeAsync(
        Guid credentialId,
        string username,
        string unixUser,
        CancellationToken cancellationToken);
}

public interface IManagedContainerSpecService
{
    ContainerDefinition Normalize(Guid familyId, Guid containerId, ContainerDefinitionRequest request);
    string Serialize(ContainerDefinition definition);
    ContainerDefinition Deserialize(string json);
    void EnsurePortsAvailable(
        ContainerDefinition definition,
        IEnumerable<ManagedContainerEntity> existingContainers,
        Guid? excludeContainerId = null);
    string BuildQuadlet(ManagedContainerEntity container);
    string BuildQuadlet(ManagedContainerEntity container, ContainerDefinition definition);
}

public interface IWireGuardKeyGenerator
{
    Task<WireGuardKeyPair> GenerateAsync(CancellationToken cancellationToken);
}
