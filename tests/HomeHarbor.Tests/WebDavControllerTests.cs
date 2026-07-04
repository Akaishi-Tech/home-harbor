using System.Security.Claims;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Controllers;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using HomeHarbor.Core.Storage;
using HomeHarbor.WebDav;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class WebDavControllerTests
{
    [TestMethod]
    public async Task Delete_MalformedPath_ReturnsBadRequest()
    {
        using var fixture = StorageFixture.Create();
        var controller = fixture.CreateController();

        var result = await controller.Delete("files", "%GG", CancellationToken.None);

        AssertStatus(StatusCodes.Status400BadRequest, result);
    }

    [TestMethod]
    public async Task Copy_MalformedSourcePath_ReturnsBadRequest()
    {
        using var fixture = StorageFixture.Create();
        var controller = fixture.CreateController();
        controller.Request.Headers[WebDavConstants.Headers.Destination] = "/dav/files/copy.txt";

        var result = await controller.Copy("files", "%", CancellationToken.None);

        AssertStatus(StatusCodes.Status400BadRequest, result);
    }

    [TestMethod]
    public async Task Copy_MalformedDestination_ReturnsBadRequest()
    {
        using var fixture = StorageFixture.Create();
        var controller = fixture.CreateController();
        controller.Request.Headers[WebDavConstants.Headers.Destination] = "http://example.test/dav/files/%GG";

        var result = await controller.Copy("files", "source.txt", CancellationToken.None);

        AssertStatus(StatusCodes.Status400BadRequest, result);
    }

    [TestMethod]
    public async Task Delete_Root_ReturnsForbidden_And_DoesNotRemoveAreaRoot()
    {
        using var fixture = StorageFixture.Create();
        var keepPath = Path.Combine(fixture.FilesRoot, "keep.txt");
        File.WriteAllText(keepPath, "keep");
        var controller = fixture.CreateController();

        var result = await controller.Delete("files", null, CancellationToken.None);

        AssertStatus(WebDavStatusCodes.Forbidden, result);
        Assert.IsTrue(Directory.Exists(fixture.FilesRoot));
        Assert.IsTrue(File.Exists(keepPath));
    }

    [TestMethod]
    public async Task Copy_RootSource_ReturnsForbidden_And_DoesNotCopyAreaRoot()
    {
        using var fixture = StorageFixture.Create();
        var controller = fixture.CreateController();
        controller.Request.Headers[WebDavConstants.Headers.Destination] = "/dav/files/root-copy";

        var result = await controller.Copy("files", null, CancellationToken.None);

        AssertStatus(WebDavStatusCodes.Forbidden, result);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.FilesRoot, "root-copy")));
    }

    [TestMethod]
    public async Task Copy_RootDestination_ReturnsForbidden_And_DoesNotRemoveSourceOrAreaRoot()
    {
        using var fixture = StorageFixture.Create();
        var sourcePath = Path.Combine(fixture.FilesRoot, "source.txt");
        File.WriteAllText(sourcePath, "source");
        var controller = fixture.CreateController();
        controller.Request.Headers[WebDavConstants.Headers.Destination] = "/dav/files/";

        var result = await controller.Copy("files", "source.txt", CancellationToken.None);

        AssertStatus(WebDavStatusCodes.Forbidden, result);
        Assert.IsTrue(Directory.Exists(fixture.FilesRoot));
        Assert.IsTrue(File.Exists(sourcePath));
    }

    [TestMethod]
    public async Task Move_RootSource_ReturnsForbidden_And_DoesNotMoveAreaRoot()
    {
        using var fixture = StorageFixture.Create();
        var keepPath = Path.Combine(fixture.FilesRoot, "keep.txt");
        File.WriteAllText(keepPath, "keep");
        var controller = fixture.CreateController();
        controller.Request.Headers[WebDavConstants.Headers.Destination] = "/dav/files/moved-root";

        var result = await controller.Move("files", null, CancellationToken.None);

        AssertStatus(WebDavStatusCodes.Forbidden, result);
        Assert.IsTrue(Directory.Exists(fixture.FilesRoot));
        Assert.IsTrue(File.Exists(keepPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.FilesRoot, "moved-root")));
    }

    [TestMethod]
    public async Task Move_RootDestination_ReturnsForbidden_And_DoesNotRemoveSourceOrAreaRoot()
    {
        using var fixture = StorageFixture.Create();
        var sourcePath = Path.Combine(fixture.FilesRoot, "source.txt");
        File.WriteAllText(sourcePath, "source");
        var controller = fixture.CreateController();
        controller.Request.Headers[WebDavConstants.Headers.Destination] = "/dav/files/";

        var result = await controller.Move("files", "source.txt", CancellationToken.None);

        AssertStatus(WebDavStatusCodes.Forbidden, result);
        Assert.IsTrue(Directory.Exists(fixture.FilesRoot));
        Assert.IsTrue(File.Exists(sourcePath));
    }

    [TestMethod]
    public async Task Put_ExistingDirectory_ReturnsConflict_WithoutReadingBody()
    {
        using var fixture = StorageFixture.Create();
        var directoryPath = Path.Combine(fixture.FilesRoot, "folder");
        _ = Directory.CreateDirectory(directoryPath);
        var body = new ThrowOnReadStream();
        var controller = fixture.CreateController(body);
        controller.Request.ContentLength = 4;

        var result = await controller.Put("files", "folder", CancellationToken.None);

        AssertStatus(WebDavStatusCodes.Conflict, result);
        Assert.IsFalse(body.ReadAttempted);
    }

    private static void AssertStatus(int expected, IActionResult result)
    {
        var actual = result switch
        {
            NoContentResult => StatusCodes.Status204NoContent,
            NotFoundResult => StatusCodes.Status404NotFound,
            StatusCodeResult status => status.StatusCode,
            ObjectResult status => status.StatusCode ?? StatusCodes.Status200OK,
            _ => throw new AssertFailedException($"Unexpected result type {result.GetType().Name}.")
        };

        Assert.AreEqual(expected, actual);
    }

    private sealed class StorageFixture : IDisposable
    {
        private StorageFixture(string dataRoot, Guid familyId, HomeHarborStorageService storage)
        {
            DataRoot = dataRoot;
            FamilyId = familyId;
            Storage = storage;
        }

        public string DataRoot { get; }
        public Guid FamilyId { get; }
        public HomeHarborStorageService Storage { get; }
        public string FilesRoot => Storage.GetAreaRoot(FamilyId, StorageArea.Files);

        public static StorageFixture Create()
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), "HomeHarbor.Tests", Guid.NewGuid().ToString("N"));
            var familyId = Guid.NewGuid();
            var storage = new HomeHarborStorageService(Options.Create(new HomeHarborStorageOptions
            {
                DataRoot = dataRoot,
                MaxUploadBytes = 1024 * 1024
            }));
            storage.EnsureFamilyRoots(familyId);
            return new StorageFixture(dataRoot, familyId, storage);
        }

        public WebDavController CreateController(Stream? body = null)
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(BasicAuthenticationHandler.FamilyIdClaim, FamilyId.ToString()),
                    new Claim(BasicAuthenticationHandler.WebDavScopeClaim, WebDavTokenScope.All.ToString())
                ], BasicAuthenticationHandler.SchemeName))
            };
            httpContext.Request.Body = body ?? Stream.Null;

            return new WebDavController(Storage, new FileExtensionContentTypeProvider(), NoopOverviewCacheInvalidator.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
        }
    }

    private sealed class NoopOverviewCacheInvalidator : IOverviewCacheInvalidator
    {
        public static readonly NoopOverviewCacheInvalidator Instance = new();

        private NoopOverviewCacheInvalidator()
        {
        }

        public Task InvalidateFamilyAsync(Guid familyId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public bool ReadAttempted { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Request body should not be read.");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Request body should not be read.");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Request body should not be read.");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
