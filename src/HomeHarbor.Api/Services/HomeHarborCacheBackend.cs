using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

namespace HomeHarbor.Api.Services;

public static class HomeHarborCacheBackend
{
    public static bool ShouldUseDevelopmentMemoryFallback(
        IHostEnvironment environment,
        HomeHarborCacheOptions options,
        Func<string, bool>? canConnect = null)
        => environment.IsDevelopment() && !(canConnect ?? CanConnectUnixSocket)(options.UnixSocketPath);

    public static bool CanConnectUnixSocket(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
