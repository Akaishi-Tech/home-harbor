using HomeHarbor.Api.Data;
using HomeHarbor.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Services;

public sealed class MediaIndexer(HomeHarborDbContext db, IHomeHarborStorageService storage) : IMediaIndexer
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".aac", ".ogg", ".wav", ".m4a"
    };

    private static readonly StorageArea[] IndexedAreas = [StorageArea.Photos, StorageArea.Files];

    public async Task<IReadOnlyList<MediaAssetEntity>> IndexAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var indexed = new List<MediaAssetEntity>();
        var indexedAreas = IndexedAreas
            .Select(area => new
            {
                Area = area,
                AreaName = StoragePathPolicy.AreaDirectoryName(area),
                Root = storage.GetAreaRoot(familyId, area)
            })
            .ToArray();
        var areaNames = indexedAreas.Select(area => area.AreaName).ToArray();
        var existingAssets = await db.MediaAssets
            .Where(asset => asset.FamilyId == familyId && areaNames.Contains(asset.Area))
            .ToDictionaryAsync(asset => (asset.Area, asset.RelativePath), cancellationToken);
        var seen = new HashSet<(string Area, string RelativePath)>();

        foreach (var area in indexedAreas)
        {
            foreach (var info in storage.EnumerateFiles(familyId, area.Area))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mediaType = Classify(info.FullName);
                if (mediaType is null) continue;

                var relative = Path.GetRelativePath(area.Root, info.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var key = (area.AreaName, relative);
                _ = seen.Add(key);

                if (!existingAssets.TryGetValue(key, out var existing))
                {
                    existing = new MediaAssetEntity
                    {
                        Id = Guid.NewGuid(),
                        FamilyId = familyId,
                        Area = area.AreaName,
                        RelativePath = relative
                    };
                    _ = db.MediaAssets.Add(existing);
                    existingAssets[key] = existing;
                }

                existing.MediaType = mediaType;
                existing.Size = info.Length;
                existing.LastModifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                existing.IndexedAt = DateTimeOffset.UtcNow;
                indexed.Add(existing);
            }
        }

        var stale = existingAssets
            .Where(pair => !seen.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        db.MediaAssets.RemoveRange(stale);

        _ = await db.SaveChangesAsync(cancellationToken);
        return indexed;
    }

    private static string? Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (PhotoExtensions.Contains(extension)) return "photo";
        if (VideoExtensions.Contains(extension)) return "video";
        return AudioExtensions.Contains(extension) ? "audio" : null;
    }
}
