using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public sealed class RuntimeSignalService(IOptions<HomeHarborRuntimeOptions> options) : IRuntimeSignalService
{
    private readonly HomeHarborRuntimeOptions _options = options.Value;

    public void RequestSmbApply()
        => Touch(Path.Combine(_options.RequestDirectory, "smb-apply.request"));

    public void RequestContainerApply()
        => Touch(Path.Combine(_options.RequestDirectory, "container-apply.request"));

    public void RequestSystemAppApply()
        => Touch(Path.Combine(_options.RequestDirectory, "system-app-apply.request"));

    public async Task WriteSmbPasswordAsync(
        Guid credentialId,
        string username,
        string unixUser,
        string password,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_options.SmbCredentialDirectory);
        var path = Path.Combine(_options.SmbCredentialDirectory, $"{credentialId:N}.json");
        var payload = JsonSerializer.Serialize(new
        {
            action = "upsert",
            credentialId,
            username,
            unixUser,
            password
        });
        await File.WriteAllTextAsync(path, payload, cancellationToken);
    }

    public async Task WriteSmbRevokeAsync(
        Guid credentialId,
        string username,
        string unixUser,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_options.SmbCredentialDirectory);
        var path = Path.Combine(_options.SmbCredentialDirectory, $"{credentialId:N}.json");
        var payload = JsonSerializer.Serialize(new
        {
            action = "revoke",
            credentialId,
            username,
            unixUser
        });
        await File.WriteAllTextAsync(path, payload, cancellationToken);
    }

    private static void Touch(string path)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
    }
}
