namespace HomeHarbor.Core.Storage;

public enum StorageArea
{
    Files,
    Photos,
    Backups
}

public sealed record ResolvedStoragePath(
    StorageArea Area,
    Guid FamilyId,
    string RelativePath,
    string PhysicalPath,
    bool IsDirectory);

