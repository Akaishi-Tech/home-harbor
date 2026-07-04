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
            _ = Directory.CreateDirectory(GetAreaRoot(familyId, area));
        }
    }

    public string GetAreaRoot(Guid familyId, StorageArea area)
        => Path.Combine(_options.DataRoot, "families", familyId.ToString("N"), StoragePathPolicy.AreaDirectoryName(area));

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
            .OrderBy(f => (f.Attributes & FileAttributes.Directory) == 0)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task WriteFileAsync(Guid familyId, StorageArea area, string? davPath, Stream input, CancellationToken cancellationToken)
    {
        var path = Resolve(familyId, area, davPath, out var normalized);
        if (normalized == "/") throw new InvalidOperationException("PUT requires a file path.");
        if (Directory.Exists(path)) throw new IOException("Cannot write a file over an existing directory.");

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) _ = Directory.CreateDirectory(parent);

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
        var path = Resolve(familyId, area, davPath);
        _ = Directory.CreateDirectory(path);
    }

    public void Delete(Guid familyId, StorageArea area, string? davPath)
    {
        var path = Resolve(familyId, area, davPath, out var normalized);
        if (normalized == "/") throw new InvalidOperationException("Storage area root cannot be deleted.");

        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
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

        var destinationExists = File.Exists(destination) || Directory.Exists(destination);
        if (destinationExists && !overwrite) return StorageTransferResult.PreconditionFailed;
        if (destinationExists) DeletePhysical(destination);

        CopyPhysical(source, destination);
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

        var destinationExists = File.Exists(destination) || Directory.Exists(destination);
        if (destinationExists && !overwrite) return StorageTransferResult.PreconditionFailed;
        if (destinationExists) DeletePhysical(destination);

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
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
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            _ = Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target);
        }
    }

    private static void DeletePhysical(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
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
