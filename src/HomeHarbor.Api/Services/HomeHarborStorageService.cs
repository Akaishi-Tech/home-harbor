using HomeHarbor.Core.Storage;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public sealed class HomeHarborStorageService(IOptions<HomeHarborStorageOptions> options) : IHomeHarborStorageService
{
    private readonly HomeHarborStorageOptions _options = options.Value;

    public string DataRoot => _options.DataRoot;
    public long MaxUploadBytes => _options.MaxUploadBytes;

    public void EnsureFamilyRoots(Guid familyId)
    {
        foreach (var area in Enum.GetValues<StorageArea>())
        {
            var root = GetAreaRoot(familyId, area);
            _ = Directory.CreateDirectory(root);
            _ = GetAreaRoot(familyId, area);
        }
    }

    public string GetAreaRoot(Guid familyId, StorageArea area)
        => StoragePathPolicy.ResolvePhysicalPath(_options.DataRoot, familyId, area, "/");

    public string Resolve(Guid familyId, StorageArea area, string? davPath)
        => Resolve(familyId, area, davPath, out _);

    private string Resolve(Guid familyId, StorageArea area, string? davPath, out string normalized)
    {
        normalized = StoragePathPolicy.NormalizeDavPath(davPath);
        return StoragePathPolicy.ResolvePhysicalPath(_options.DataRoot, familyId, area, normalized);
    }

    public FileSystemInfo? Stat(Guid familyId, StorageArea area, string? davPath)
    {
        var path = Resolve(familyId, area, davPath);
        return File.Exists(path) ? new FileInfo(path) : Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo?)null;
    }

    public IReadOnlyList<FileSystemInfo> Enumerate(Guid familyId, StorageArea area, string? davPath)
    {
        var path = Resolve(familyId, area, davPath);
        return !Directory.Exists(path)
            ? []
            : [.. new DirectoryInfo(path)
            .EnumerateFileSystemInfos()
            .Where(info => !StoragePathPolicy.IsReparsePoint(info.FullName))
            .OrderBy(f => (f.Attributes & FileAttributes.Directory) == 0)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<FileInfo> EnumerateFiles(Guid familyId, StorageArea area)
    {
        var root = Resolve(familyId, area, "/");
        if (!Directory.Exists(root)) return [];

        var files = new List<FileInfo>();
        EnumerateFilesWithoutLinks(new DirectoryInfo(root), files);
        return files;
    }

    public FileStream OpenRead(Guid familyId, StorageArea area, string? davPath)
    {
        var path = Resolve(familyId, area, davPath, out var normalized);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _ = Resolve(familyId, area, normalized);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public async Task WriteFileAsync(Guid familyId, StorageArea area, string? davPath, Stream input, CancellationToken cancellationToken)
    {
        var path = Resolve(familyId, area, davPath, out var normalized);
        if (normalized == "/") throw new InvalidOperationException("PUT requires a file path.");
        if (Directory.Exists(path)) throw new IOException("Cannot write a file over an existing directory.");

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            _ = Directory.CreateDirectory(parent);
            var verifiedPath = Resolve(familyId, area, normalized);
            if (!string.Equals(path, verifiedPath, PathComparison))
                throw new InvalidOperationException("Storage path changed while preparing the upload.");
        }

        var tempPath = Path.Combine(parent ?? _options.DataRoot, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.upload");
        try
        {
            await using (var file = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0) break;

                    if (total + read > _options.MaxUploadBytes)
                        throw new MaxUploadSizeExceededException(_options.MaxUploadBytes);

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                }
            }

            _ = Resolve(familyId, area, normalized);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    public void CreateDirectory(Guid familyId, StorageArea area, string? davPath)
    {
        var path = Resolve(familyId, area, davPath, out var normalized);
        _ = Directory.CreateDirectory(path);
        _ = Resolve(familyId, area, normalized);
    }

    public void Delete(Guid familyId, StorageArea area, string? davPath)
    {
        var path = Resolve(familyId, area, davPath, out var normalized);
        if (normalized == "/") throw new InvalidOperationException("Storage area root cannot be deleted.");

        if (File.Exists(path))
        {
            _ = Resolve(familyId, area, normalized);
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            ThrowIfTreeContainsReparsePoints(new DirectoryInfo(path));
            _ = Resolve(familyId, area, normalized);
            Directory.Delete(path, recursive: true);
        }
    }

    public StorageTransferResult Copy(Guid familyId, StorageArea sourceArea, string? sourcePath, StorageArea destinationArea, string destinationPath, bool overwrite)
    {
        var source = Resolve(familyId, sourceArea, sourcePath, out var normalizedSource);
        var destination = Resolve(familyId, destinationArea, destinationPath, out var normalizedDestination);
        if (normalizedSource == "/" || normalizedDestination == "/") return StorageTransferResult.Forbidden;

        var sourceIsFile = File.Exists(source);
        var sourceIsDirectory = Directory.Exists(source);
        if (!sourceIsFile && !sourceIsDirectory) return StorageTransferResult.SourceMissing;
        if (IsInvalidTransfer(source, destination, sourceIsDirectory)) return StorageTransferResult.Forbidden;
        if (sourceIsDirectory) ThrowIfTreeContainsReparsePoints(new DirectoryInfo(source));

        var destinationExists = File.Exists(destination) || Directory.Exists(destination);
        if (destinationExists && !overwrite) return StorageTransferResult.PreconditionFailed;
        if (destinationExists)
        {
            _ = Resolve(familyId, destinationArea, normalizedDestination);
            DeletePhysical(destination);
        }

        CopyPhysical(source, destination);
        _ = Resolve(familyId, destinationArea, normalizedDestination);
        return destinationExists ? StorageTransferResult.Replaced : StorageTransferResult.Created;
    }

    public StorageTransferResult Move(Guid familyId, StorageArea sourceArea, string? sourcePath, StorageArea destinationArea, string destinationPath, bool overwrite)
    {
        var source = Resolve(familyId, sourceArea, sourcePath, out var normalizedSource);
        var destination = Resolve(familyId, destinationArea, destinationPath, out var normalizedDestination);
        if (normalizedSource == "/" || normalizedDestination == "/") return StorageTransferResult.Forbidden;

        var sourceIsFile = File.Exists(source);
        var sourceIsDirectory = Directory.Exists(source);
        if (!sourceIsFile && !sourceIsDirectory) return StorageTransferResult.SourceMissing;
        if (IsInvalidTransfer(source, destination, sourceIsDirectory)) return StorageTransferResult.Forbidden;
        if (sourceIsDirectory) ThrowIfTreeContainsReparsePoints(new DirectoryInfo(source));

        var destinationExists = File.Exists(destination) || Directory.Exists(destination);
        if (destinationExists && !overwrite) return StorageTransferResult.PreconditionFailed;
        if (destinationExists)
        {
            _ = Resolve(familyId, destinationArea, normalizedDestination);
            DeletePhysical(destination);
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
        _ = Resolve(familyId, destinationArea, normalizedDestination);
        if (sourceIsFile) File.Move(source, destination);
        else Directory.Move(source, destination);
        return destinationExists ? StorageTransferResult.Replaced : StorageTransferResult.Created;
    }

    private static void CopyPhysical(string source, string destination)
    {
        if (File.Exists(source))
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
            File.Copy(source, destination);
            return;
        }

        if (!Directory.Exists(source)) throw new FileNotFoundException(source);
        _ = Directory.CreateDirectory(destination);
        CopyDirectoryWithoutLinks(new DirectoryInfo(source), destination);
    }

    private static void CopyDirectoryWithoutLinks(DirectoryInfo source, string destination)
    {
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (StoragePathPolicy.IsReparsePoint(entry.FullName))
                throw new InvalidOperationException("Storage path contains a symbolic link or reparse point that cannot be copied.");

            var target = Path.Combine(destination, entry.Name);
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                _ = Directory.CreateDirectory(target);
                CopyDirectoryWithoutLinks((DirectoryInfo)entry, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    private static void DeletePhysical(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path))
        {
            ThrowIfTreeContainsReparsePoints(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
    }

    private static void EnumerateFilesWithoutLinks(DirectoryInfo directory, List<FileInfo> files)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (StoragePathPolicy.IsReparsePoint(entry.FullName)) continue;
            if ((entry.Attributes & FileAttributes.Directory) != 0)
                EnumerateFilesWithoutLinks((DirectoryInfo)entry, files);
            else if (entry is FileInfo file)
                files.Add(file);
        }
    }

    private static void ThrowIfTreeContainsReparsePoints(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (StoragePathPolicy.IsReparsePoint(entry.FullName))
                throw new InvalidOperationException("Storage path trees cannot contain symbolic links or reparse points.");
            if ((entry.Attributes & FileAttributes.Directory) != 0)
                ThrowIfTreeContainsReparsePoints((DirectoryInfo)entry);
        }
    }

    private static bool IsInvalidTransfer(string source, string destination, bool sourceIsDirectory)
        => PathsEqual(source, destination) || (sourceIsDirectory && IsDescendantPath(source, destination));

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static bool IsDescendantPath(string parent, string child)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullChild = Path.GetFullPath(child);
        return fullChild.StartsWith(fullParent + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public enum StorageTransferResult
{
    Created,
    Replaced,
    SourceMissing,
    PreconditionFailed,
    Forbidden
}

public sealed class MaxUploadSizeExceededException(long maxUploadBytes)
    : IOException($"Upload exceeds the configured limit of {maxUploadBytes} bytes.")
{
    public long MaxUploadBytes { get; } = maxUploadBytes;
}
