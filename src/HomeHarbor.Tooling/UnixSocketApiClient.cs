using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace HomeHarbor.Tooling;

public sealed class HomeHarborApiClient : IDisposable
{
    private readonly HttpClient _http;

    public HomeHarborApiClient(string apiUrl, string? apiSocket, string? tokenFile)
    {
        if (!string.IsNullOrWhiteSpace(apiSocket))
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(apiSocket), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };
            _http = new HttpClient(handler) { BaseAddress = new Uri(apiUrl) };
        }
        else
        {
            _http = new HttpClient { BaseAddress = new Uri(apiUrl) };
        }

        if (!string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile) && new FileInfo(tokenFile).Length > 0)
        {
            var token = File.ReadAllText(tokenFile).Trim();
            if (token.Length > 0)
            {
                _http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            }
        }
    }

    public async Task DownloadAsync(string path, string destination, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        _ = response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    public async Task<string> GetStringAsync(string path, CancellationToken cancellationToken = default)
        => await _http.GetStringAsync(path, cancellationToken);

    public async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<T>(path, cancellationToken);

    public async Task PostJsonAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(path, value, cancellationToken);
        _ = response.EnsureSuccessStatusCode();
    }

    public void Dispose()
        => _http.Dispose();
}
