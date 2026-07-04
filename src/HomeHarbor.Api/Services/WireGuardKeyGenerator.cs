using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace HomeHarbor.Api.Services;

public sealed class WireGuardKeyGenerator : IWireGuardKeyGenerator
{
    private readonly ILogger<WireGuardKeyGenerator> logger;
    private readonly WireGuardCommandRunner runWireGuardAsync;

    public WireGuardKeyGenerator(ILogger<WireGuardKeyGenerator> logger)
        : this(logger, RunWireGuardAsync)
    {
    }

    internal WireGuardKeyGenerator(
        ILogger<WireGuardKeyGenerator> logger,
        WireGuardCommandRunner runWireGuardAsync)
    {
        this.logger = logger;
        this.runWireGuardAsync = runWireGuardAsync;
    }

    internal delegate Task<string> WireGuardCommandRunner(string arguments, string? stdin, CancellationToken cancellationToken);

    public async Task<WireGuardKeyPair> GenerateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var privateKey = await runWireGuardAsync("genkey", null, cancellationToken);
            var publicKey = await runWireGuardAsync("pubkey", privateKey, cancellationToken);
            return new WireGuardKeyPair(publicKey, privateKey, "wireguard-tools");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            logger.LogWarning(ex, "wireguard-tools is unavailable; using development fallback keys.");
            return WireGuardKeyDerivation.GenerateFallbackKeyPair();
        }
    }

    private static async Task<string> RunWireGuardAsync(string arguments, string? stdin, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("wg", arguments)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start wg.");

        if (stdin is not null)
        {
            await process.StandardInput.WriteLineAsync(stdin.AsMemory(), cancellationToken);
            await process.StandardInput.DisposeAsync();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await outputTask).Trim();
        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) return output;

        var error = (await errorTask).Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
            ? $"wg {arguments} exited with code {process.ExitCode}."
            : error);
    }
}

public sealed record WireGuardKeyPair(string PublicKey, string PrivateKey, string Mode);

internal static class WireGuardKeyDerivation
{
    private static readonly BigInteger Prime = (BigInteger.One << 255) - 19;
    private static readonly BigInteger A24 = 121665;

    public static WireGuardKeyPair GenerateFallbackKeyPair()
    {
        Span<byte> privateKey = stackalloc byte[32];
        RandomNumberGenerator.Fill(privateKey);
        ClampPrivateKey(privateKey);

        var publicKey = DerivePublicKey(privateKey);
        return new WireGuardKeyPair(
            Convert.ToBase64String(publicKey),
            Convert.ToBase64String(privateKey),
            "managed-fallback");
    }

    public static byte[] DerivePublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != 32)
        {
            throw new ArgumentException("WireGuard private keys must be 32 bytes.", nameof(privateKey));
        }

        Span<byte> scalar = stackalloc byte[32];
        privateKey.CopyTo(scalar);
        ClampPrivateKey(scalar);

        Span<byte> basePoint = stackalloc byte[32];
        basePoint[0] = 9;
        return ScalarMult(scalar, basePoint);
    }

    private static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uCoordinate)
    {
        // RFC 7748 Montgomery ladder for X25519.
        Span<byte> u = stackalloc byte[32];
        uCoordinate.CopyTo(u);
        u[31] &= 0x7f;

        var x1 = new BigInteger(u, isUnsigned: true, isBigEndian: false);
        var x2 = BigInteger.One;
        var z2 = BigInteger.Zero;
        var x3 = x1;
        var z3 = BigInteger.One;
        var swap = 0;

        for (var t = 254; t >= 0; t--)
        {
            var bit = (scalar[t >> 3] >> (t & 7)) & 1;
            if ((swap ^ bit) != 0)
            {
                Swap(ref x2, ref x3);
                Swap(ref z2, ref z3);
            }

            swap = bit;

            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);
            var daPlusCb = Mod(da + cb);
            var daMinusCb = Mod(da - cb);

            x3 = Mod(daPlusCb * daPlusCb);
            z3 = Mod(x1 * Mod(daMinusCb * daMinusCb));
            x2 = Mod(aa * bb);
            z2 = Mod(e * Mod(aa + A24 * e));
        }

        if (swap != 0)
        {
            Swap(ref x2, ref x3);
            Swap(ref z2, ref z3);
        }

        return ToLittleEndian32(Mod(x2 * BigInteger.ModPow(z2, Prime - 2, Prime)));
    }

    private static void ClampPrivateKey(Span<byte> privateKey)
    {
        privateKey[0] &= 248;
        privateKey[31] &= 127;
        privateKey[31] |= 64;
    }

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % Prime;
        return result.Sign < 0 ? result + Prime : result;
    }

    private static byte[] ToLittleEndian32(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Resize(ref bytes, 32);
        return bytes;
    }

    private static void Swap(ref BigInteger left, ref BigInteger right)
        => (left, right) = (right, left);
}
