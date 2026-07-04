using HomeHarbor.Api.Data;

namespace HomeHarbor.Api.Services;

public sealed class StorageHealthService(IHomeHarborStorageService storage) : IStorageHealthService
{
    public StorageHealthEntity Check(Guid familyId)
    {
        _ = Directory.CreateDirectory(storage.DataRoot);
        var drive = FileSystemStats.GetDriveForPath(storage.DataRoot);
        var availableRatio = drive.TotalSize == 0 ? 0 : (double)drive.AvailableFreeSpace / drive.TotalSize;
        var status = availableRatio switch
        {
            < 0.05 => "critical",
            < 0.15 => "warning",
            _ => "ok"
        };

        var notes = $"Drive {drive.Name} format={drive.DriveFormat}; SMART details are available through appliance smartctl jobs.";
        return new StorageHealthEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            DataRoot = storage.DataRoot,
            TotalBytes = drive.TotalSize,
            AvailableBytes = drive.AvailableFreeSpace,
            Status = status,
            Notes = notes,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }
}
