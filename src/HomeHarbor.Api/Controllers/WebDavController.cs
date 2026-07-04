using System.Globalization;
using System.Text;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Storage;
using HomeHarbor.WebDav;
using HomeHarbor.WebDav.Http;
using HomeHarbor.WebDav.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = BasicAuthenticationHandler.SchemeName)]
[Route("dav/{area}/{*path=}")]
public sealed class WebDavController(
    IHomeHarborStorageService storage,
    IContentTypeProvider contentTypeProvider,
    IOverviewCacheInvalidator overviewCache) : ControllerBase
{
    private const string AllowHeader = "OPTIONS, PROPFIND, HEAD, GET, PUT, MKCOL, DELETE, COPY, MOVE";

    [HttpOptions]
    public IActionResult Options(string area)
    {
        if (!TryParseArea(area, out _)) return NotFound();
        Response.Headers[WebDavConstants.Headers.Dav] = "1, 2";
        Response.Headers["MS-Author-Via"] = "DAV";
        Response.Headers.Allow = AllowHeader;
        return NoContent();
    }

    [HttpPropFind("")]
    public IActionResult PropFind(string area, string? path)
    {
        if (!TryAuthorize(area, out var identity, out var storageArea, out var denied)) return denied;

        if (!TryNormalizePath(path, out var normalized)) return InvalidPath();
        var resource = storage.Stat(identity.FamilyId, storageArea, normalized);
        if (resource is null) return NotFound();

        var depth = ParseDepth(Request.Headers[WebDavConstants.Headers.Depth].ToString());
        var isDirectory = IsDirectory(resource);
        if (depth == DepthValue.Infinity && isDirectory)
        {
            Response.Headers[WebDavConstants.Headers.Dav] = "1, 2";
            return StatusCode(WebDavStatusCodes.Forbidden);
        }

        var multi = new MultiStatus();
        multi.Responses.Add(BuildResponse(area, normalized, resource));

        if (depth == DepthValue.One && isDirectory)
        {
            foreach (var child in storage.Enumerate(identity.FamilyId, storageArea, normalized))
            {
                var childPath = AppendSegment(normalized, child.Name);
                multi.Responses.Add(BuildResponse(area, childPath, child));
            }
        }

        return MultiStatus(multi);
    }

    [HttpGet]
    [HttpHead]
    public IActionResult GetFile(string area, string? path)
    {
        if (!TryAuthorize(area, out var identity, out var storageArea, out var denied)) return denied;

        if (!TryNormalizePath(path, out var normalized)) return InvalidPath();
        var resource = storage.Stat(identity.FamilyId, storageArea, normalized);
        if (resource is null) return NotFound();
        if (IsDirectory(resource))
        {
            Response.Headers.Allow = AllowHeader;
            return StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        var physicalPath = storage.Resolve(identity.FamilyId, storageArea, normalized);
        var fileName = Path.GetFileName(physicalPath);
        var contentType = contentTypeProvider.TryGetContentType(fileName, out var ct)
            ? ct
            : "application/octet-stream";
        return PhysicalFile(physicalPath, contentType, fileName, enableRangeProcessing: true);
    }

    [HttpPut]
    public async Task<IActionResult> Put(string area, string? path, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(area, out var identity, out var storageArea, out var denied)) return denied;

        if (!TryNormalizePath(path, out var normalized)) return InvalidPath();
        if (normalized == "/") return BadRequest(new { error = "PUT requires a file path." });
        if (Request.ContentLength is long contentLength && contentLength > storage.MaxUploadBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var existingResource = storage.Stat(identity.FamilyId, storageArea, normalized);
        if (existingResource is not null && IsDirectory(existingResource)) return StatusCode(WebDavStatusCodes.Conflict);

        var existed = existingResource is not null;
        try
        {
            await storage.WriteFileAsync(identity.FamilyId, storageArea, normalized, Request.Body, cancellationToken);
        }
        catch (MaxUploadSizeExceededException)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        await overviewCache.InvalidateFamilyAsync(identity.FamilyId, cancellationToken);
        return StatusCode(existed ? WebDavStatusCodes.NoContent : WebDavStatusCodes.Created);
    }

    [HttpMkcol("")]
    public async Task<IActionResult> Mkcol(string area, string? path, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(area, out var identity, out var storageArea, out var denied)) return denied;

        if (!TryNormalizePath(path, out var normalized)) return InvalidPath();
        if (normalized == "/") return MethodNotAllowed();
        if (storage.Stat(identity.FamilyId, storageArea, normalized) is not null) return StatusCode(WebDavStatusCodes.Conflict);
        storage.CreateDirectory(identity.FamilyId, storageArea, normalized);
        await overviewCache.InvalidateFamilyAsync(identity.FamilyId, cancellationToken);
        return StatusCode(WebDavStatusCodes.Created);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(string area, string? path, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(area, out var identity, out var storageArea, out var denied)) return denied;

        if (!TryNormalizePath(path, out var normalized)) return InvalidPath();
        if (normalized == "/") return StatusCode(WebDavStatusCodes.Forbidden);
        if (storage.Stat(identity.FamilyId, storageArea, normalized) is null) return NotFound();
        storage.Delete(identity.FamilyId, storageArea, normalized);
        await overviewCache.InvalidateFamilyAsync(identity.FamilyId, cancellationToken);
        return NoContent();
    }

    [HttpCopy("")]
    public async Task<IActionResult> Copy(string area, string? path, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(area, out var identity, out var sourceArea, out var denied)) return denied;
        if (!TryParseDestination(out var destinationArea, out var destinationPath)) return BadRequest(new { error = "Destination header is invalid." });
        if (!identity.CanAccess(destinationArea)) return Forbid();

        if (!TryNormalizePath(path, out var sourcePath)) return InvalidPath();
        if (sourcePath == "/" || destinationPath == "/") return StatusCode(WebDavStatusCodes.Forbidden);
        var overwrite = ParseOverwrite();
        try
        {
            var result = storage.Copy(identity.FamilyId, sourceArea, sourcePath, destinationArea, destinationPath, overwrite);
            if (Mutated(result)) await overviewCache.InvalidateFamilyAsync(identity.FamilyId, cancellationToken);
            return TransferResult(result);
        }
        catch (IOException)
        {
            return StatusCode(WebDavStatusCodes.PreconditionFailed);
        }
    }

    [HttpMove("")]
    public async Task<IActionResult> Move(string area, string? path, CancellationToken cancellationToken)
    {
        if (!TryAuthorize(area, out var identity, out var sourceArea, out var denied)) return denied;
        if (!TryParseDestination(out var destinationArea, out var destinationPath)) return BadRequest(new { error = "Destination header is invalid." });
        if (!identity.CanAccess(destinationArea)) return Forbid();

        if (!TryNormalizePath(path, out var sourcePath)) return InvalidPath();
        if (sourcePath == "/" || destinationPath == "/") return StatusCode(WebDavStatusCodes.Forbidden);
        var overwrite = ParseOverwrite();
        try
        {
            var result = storage.Move(identity.FamilyId, sourceArea, sourcePath, destinationArea, destinationPath, overwrite);
            if (Mutated(result)) await overviewCache.InvalidateFamilyAsync(identity.FamilyId, cancellationToken);
            return TransferResult(result);
        }
        catch (IOException)
        {
            return StatusCode(WebDavStatusCodes.PreconditionFailed);
        }
    }

    [HttpPropPatch("")]
    [HttpLock("")]
    [HttpUnlock("")]
    public IActionResult MethodNotAllowed()
    {
        Response.Headers.Allow = AllowHeader;
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    private bool TryAuthorize(
        string area,
        out WebDavIdentity identity,
        out StorageArea storageArea,
        out IActionResult denied)
    {
        identity = WebDavIdentity.FromPrincipal(User);
        if (!TryParseArea(area, out storageArea))
        {
            denied = NotFound();
            return false;
        }

        if (!identity.CanAccess(storageArea))
        {
            denied = Forbid();
            return false;
        }

        denied = Ok();
        return true;
    }

    private bool TryParseDestination(out StorageArea area, out string normalizedPath)
    {
        area = StorageArea.Files;
        normalizedPath = "/";
        var header = Request.Headers[WebDavConstants.Headers.Destination].ToString();
        if (string.IsNullOrWhiteSpace(header)) return false;

        string pathAndQuery = Uri.TryCreate(header, UriKind.Absolute, out var absolute) ? ExtractPathFromAbsoluteDestination(header, absolute) : header;
        var path = StripQueryAndFragment(pathAndQuery);
        string destination;
        try
        {
            destination = StoragePathPolicy.NormalizeDavPath(path);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var segments = destination.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "dav", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryParseArea(segments[1], out area)) return false;
        normalizedPath = "/" + string.Join('/', segments.Skip(2));
        return true;
    }

    private bool ParseOverwrite()
    {
        var overwrite = Request.Headers[WebDavConstants.Headers.Overwrite].ToString();
        return string.IsNullOrWhiteSpace(overwrite) || overwrite.Equals("T", StringComparison.OrdinalIgnoreCase);
    }

    private IActionResult TransferResult(StorageTransferResult result)
        => result switch
        {
            StorageTransferResult.Created => StatusCode(WebDavStatusCodes.Created),
            StorageTransferResult.Replaced => StatusCode(WebDavStatusCodes.NoContent),
            StorageTransferResult.SourceMissing => NotFound(),
            StorageTransferResult.PreconditionFailed => StatusCode(WebDavStatusCodes.PreconditionFailed),
            StorageTransferResult.Forbidden => StatusCode(WebDavStatusCodes.Forbidden),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };

    private static bool Mutated(StorageTransferResult result)
        => result is StorageTransferResult.Created or StorageTransferResult.Replaced;

    private DavResponse BuildResponse(string area, string normalizedPath, FileSystemInfo resource)
    {
        var isDirectory = IsDirectory(resource);
        var prop = new Prop
        {
            DisplayName = normalizedPath == "/" ? string.Empty : resource.Name,
            ResourceType = new ResourceType { IsCollection = isDirectory },
            GetContentType = isDirectory ? "httpd/unix-directory" : ResolveContentType(resource.Name)
        };

        var modified = resource.LastWriteTimeUtc;
        prop.GetLastModified = modified.ToString("R", CultureInfo.InvariantCulture);
        prop.CreationDate = resource.CreationTimeUtc.ToString("o", CultureInfo.InvariantCulture);
        if (!isDirectory && resource is FileInfo file)
        {
            prop.GetContentLength = file.Length.ToString(CultureInfo.InvariantCulture);
            prop.GetETag = $"\"{modified.Ticks:x}-{file.Length:x}\"";
        }
        else
        {
            TryPopulateQuota(prop, resource.FullName);
        }

        return new DavResponse
        {
            Href = BuildHref(area, normalizedPath, isDirectory),
            PropStats =
            [
                new PropStat
                {
                    Prop = prop,
                    Status = WebDavStatusCodes.FormatStatusLine(WebDavStatusCodes.Ok)
                }
            ]
        };
    }

    private IActionResult MultiStatus(MultiStatus multiStatus)
    {
        Response.StatusCode = WebDavStatusCodes.MultiStatus;
        Response.ContentType = WebDavConstants.XmlContentType;
        return Content(WebDavXml.Serialize(multiStatus), WebDavConstants.XmlContentType, Encoding.UTF8);
    }

    private string ResolveContentType(string fileName)
        => contentTypeProvider.TryGetContentType(fileName, out var ct) ? ct : "application/octet-stream";

    private static string Normalize(string? path) => StoragePathPolicy.NormalizeDavPath(path);

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        try
        {
            normalized = Normalize(path);
            return true;
        }
        catch (InvalidOperationException)
        {
            normalized = "/";
            return false;
        }
    }

    private IActionResult InvalidPath() => BadRequest(new { error = "Path is invalid." });

    private static bool IsDirectory(FileSystemInfo info)
        => (info.Attributes & FileAttributes.Directory) != 0;

    private static string ExtractPathFromAbsoluteDestination(string header, Uri absolute)
    {
        var authorityMarker = header.IndexOf("://", StringComparison.Ordinal);
        if (authorityMarker < 0) return absolute.AbsolutePath;

        var pathStart = header.IndexOf('/', authorityMarker + 3);
        return pathStart < 0 ? "/" : header[pathStart..];
    }

    private static string StripQueryAndFragment(string pathAndQuery)
    {
        var queryStart = pathAndQuery.IndexOf('?');
        var fragmentStart = pathAndQuery.IndexOf('#');

        var end = queryStart switch
        {
            < 0 when fragmentStart < 0 => -1,
            < 0 => fragmentStart,
            _ when fragmentStart < 0 => queryStart,
            _ => Math.Min(queryStart, fragmentStart)
        };

        return end < 0 ? pathAndQuery : pathAndQuery[..end];
    }

    private static string AppendSegment(string parent, string segment)
        => parent == "/" ? "/" + segment : parent.TrimEnd('/') + "/" + segment;

    private static string BuildHref(string area, string normalizedPath, bool isDirectory)
    {
        var builder = new StringBuilder();
        _ = builder.Append("/dav/");
        _ = builder.Append(Uri.EscapeDataString(area));
        foreach (var segment in normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            _ = builder.Append('/');
            _ = builder.Append(Uri.EscapeDataString(segment));
        }

        if (isDirectory && builder[^1] != '/') _ = builder.Append('/');
        return builder.ToString();
    }

    private static bool TryParseArea(string area, out StorageArea storageArea)
    {
        storageArea = area.ToLowerInvariant() switch
        {
            "files" => StorageArea.Files,
            "photos" => StorageArea.Photos,
            "backups" => StorageArea.Backups,
            _ => (StorageArea)(-1)
        };
        return Enum.IsDefined(storageArea);
    }

    private static DepthValue ParseDepth(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return DepthValue.Infinity;
        if (header.Equals(WebDavConstants.Depth.Zero, StringComparison.OrdinalIgnoreCase)) return DepthValue.Zero;
        return header.Equals(WebDavConstants.Depth.One, StringComparison.OrdinalIgnoreCase) ? DepthValue.One : DepthValue.Infinity;
    }

    private static void TryPopulateQuota(Prop prop, string path)
    {
        try
        {
            var drive = FileSystemStats.GetDriveForPath(path);
            prop.QuotaAvailableBytes = drive.AvailableFreeSpace.ToString(CultureInfo.InvariantCulture);
            prop.QuotaUsedBytes = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            // Quota is advisory for WebDAV clients; never fail listings because the host cannot report it.
        }
    }

    private enum DepthValue
    {
        Zero,
        One,
        Infinity
    }
}
