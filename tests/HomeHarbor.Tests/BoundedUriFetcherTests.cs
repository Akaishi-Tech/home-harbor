using System.Net;
using System.Text;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class BoundedUriFetcherTests
{
    [TestMethod]
    public void ValidateUri_Rejects_Cleartext_And_Private_Network_Targets()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BoundedUriFetcher.ValidateUri("http://example.com/index.json"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BoundedUriFetcher.ValidateUri("https://127.0.0.1/index.json"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BoundedUriFetcher.ValidateUri("https://[::1]/index.json"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BoundedUriFetcher.ValidateUri("https://metadata.local/latest/meta-data"));
    }

    [TestMethod]
    public async Task ReadUtf8TextAsync_Enforces_Response_Size_Without_ContentLength()
    {
        using var http = new HttpClient(new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("12345")))
            }));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BoundedUriFetcher.ReadUtf8TextAsync(
                http,
                "https://example.com/data",
                maxBytes: 4,
                label: "test response"));

        Assert.Contains("4-byte limit", exception.Message);
    }

    [TestMethod]
    public async Task ReadUtf8TextAsync_Rejects_CrossOrigin_Redirect()
    {
        using var http = new HttpClient(new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example/payload") }
            }));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BoundedUriFetcher.ReadUtf8TextAsync(
                http,
                "https://store.example/index.json",
                maxBytes: 1024,
                label: "test redirect"));

        Assert.Contains("trusted origin", exception.Message);
    }

    [TestMethod]
    public async Task ReadUtf8TextAsync_Confines_File_Urls_To_Explicit_Root_And_Resolves_Symlinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "homeharbor-uri-root-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "homeharbor-uri-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = Directory.CreateDirectory(root);
            _ = Directory.CreateDirectory(outside);
            var insidePath = Path.Combine(root, "inside.json");
            var outsidePath = Path.Combine(outside, "secret.json");
            await File.WriteAllTextAsync(insidePath, "inside");
            await File.WriteAllTextAsync(outsidePath, "outside");
            using var http = new HttpClient(new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.NotFound)));

            var text = await BoundedUriFetcher.ReadUtf8TextAsync(
                http,
                new Uri(insidePath).AbsoluteUri,
                64,
                allowedFileRoot: root);
            Assert.AreEqual("inside", text);

            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                BoundedUriFetcher.ReadUtf8TextAsync(
                    http,
                    new Uri(outsidePath).AbsoluteUri,
                    64,
                    allowedFileRoot: root));

            var link = Path.Combine(root, "escape.json");
            string? symlinkUnavailableReason = null;
            try
            {
                _ = File.CreateSymbolicLink(link, outsidePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                symlinkUnavailableReason = "File symlinks are unavailable: " + exception.Message;
            }
            if (symlinkUnavailableReason is not null)
            {
                Assert.Inconclusive(symlinkUnavailableReason);
            }

            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                BoundedUriFetcher.ReadUtf8TextAsync(
                    http,
                    new Uri(link).AbsoluteUri,
                    64,
                    allowedFileRoot: root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
