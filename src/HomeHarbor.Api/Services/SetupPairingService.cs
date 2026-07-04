
namespace HomeHarbor.Api.Services;

public sealed class SetupPairingService(ITokenGenerator tokens) : ISetupPairingService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private SetupPairingTicket? _current;

    public SetupPairingTicket GetOrCreate(string publicOrigin)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_current is not null && _current.ExpiresAt > now.AddMinutes(1))
                return _current with { PublicOrigin = publicOrigin, PairingUrl = BuildPairingUrl(publicOrigin, _current.Code) };

            var code = tokens.GenerateRecoveryCode();
            _current = new SetupPairingTicket(
                code,
                publicOrigin,
                BuildPairingUrl(publicOrigin, code),
                now.Add(Lifetime));
            return _current;
        }
    }

    public bool IsValid(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        lock (_gate)
        {
            return _current is not null &&
                _current.ExpiresAt > DateTimeOffset.UtcNow &&
                string.Equals(_current.Code, code.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Consume(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;

        lock (_gate)
        {
            if (_current is not null &&
                string.Equals(_current.Code, code.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _current = null;
            }
        }
    }

    private static string BuildPairingUrl(string publicOrigin, string code)
        => $"{publicOrigin.TrimEnd('/')}/setup?pair={Uri.EscapeDataString(code)}";
}

public sealed record SetupPairingTicket(
    string Code,
    string PublicOrigin,
    string PairingUrl,
    DateTimeOffset ExpiresAt);
