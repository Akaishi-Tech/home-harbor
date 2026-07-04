using System.Globalization;
using System.Text;

namespace HomeHarbor.Tooling;

public sealed class BuildToolCommands(string root, ICommandRunner? runner = null)
{
    private readonly string _root = BuildKeyDefaults.Apply(root);
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();

    public async Task BuildEfiLoaderAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["clang", "lld-link", "make"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "make",
            ["-C", Path.Combine(_root, "boot", "bootloader"), "OUTPUT=" + fullOutput, "all"],
            cancellationToken);
    }

    public async Task BuildHomeHarborAvbAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            [
                "-O2",
                "-Wall",
                "-Wextra",
                "-o",
                fullOutput,
                Path.Combine(_root, "boot", "avb", "homeharbor-avb.c"),
                "-lcrypto"
            ],
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    public async Task BuildHomeHarborInitAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            HomeHarborInitHelperBuild.CompileArguments(
                fullOutput,
                Path.Combine(_root, "boot", "init", "homeharbor-verity.c")),
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    public async Task ArchPackageAsync(string version, CancellationToken cancellationToken = default)
    {
        RequireSafeVersion(version);
        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            throw new InvalidOperationException("arch-package must run as a normal user; makepkg and fakeroot must not be driven from a rootful builder.");
        }

        await RequireToolsAsync(["dotnet", "fakeroot", "makepkg", "pnpm", "tar"], cancellationToken);

        var packageWork = Env.String("HOMEHARBOR_PACKAGE_WORK", Path.Combine(_root, ".work", "arch-package", version));
        var packageOutput = Env.String("HOMEHARBOR_PACKAGE_OUTPUT", Path.Combine(_root, "artifacts", "packages", version));
        var sourceDir = Path.Combine(packageWork, "source");
        var buildDir = Path.Combine(packageWork, "makepkg");
        var sourceTarball = Path.Combine(sourceDir, $"homeharbor-{version}.tar.gz");

        DeleteIfExists(packageWork);
        DeleteIfExists(packageOutput);
        _ = Directory.CreateDirectory(sourceDir);
        _ = Directory.CreateDirectory(buildDir);
        _ = Directory.CreateDirectory(packageOutput);

        await RunRequiredAsync(
            "tar",
            [
                "-C",
                _root,
                "--exclude=./.work",
                "--exclude=./artifacts",
                "--exclude=./node_modules",
                "--exclude=./docs/node_modules",
                "--exclude=./frontend/node_modules",
                "--exclude=./packaging/arch/pkg",
                "--exclude=./packaging/arch/src",
                "--exclude=./packaging/arch/homeharbor-*.tar.gz",
                "--exclude=./packaging/arch/*.pkg.tar.*",
                "--exclude=*/bin",
                "--exclude=*/obj",
                "--transform",
                $"s#^\\.#homeharbor-{version}#",
                "-czf",
                sourceTarball,
                "."
            ],
            cancellationToken);

        var packagingTarball = Path.Combine(_root, "packaging", "arch", $"homeharbor-{version}.tar.gz");
        if (File.Exists(packagingTarball))
        {
            File.Delete(packagingTarball);
        }

        var environment = new Dictionary<string, string>
        {
            ["HOMEHARBOR_VERSION"] = version,
            ["HOMEHARBOR_CHANNEL"] = Env.String("HOMEHARBOR_CHANNEL", ReleaseChannel.Dev),
            ["HOMEHARBOR_SOURCE_TARBALL"] = sourceTarball,
            ["PKGDEST"] = packageOutput,
            ["BUILDDIR"] = buildDir
        };
        await RunRequiredAsync(
            "makepkg",
            ["--force", "--cleanbuild", "--clean", "--nodeps"],
            cancellationToken,
            workingDirectory: Path.Combine(_root, "packaging", "arch"),
            environment: environment);

        foreach (var package in Directory.GetFiles(packageOutput, "*.pkg.tar.*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            Console.WriteLine(package);
        }
    }

    public async Task GenerateEfiAvbPublicKeyHeaderAsync(string output, CancellationToken cancellationToken = default)
    {
        await RequireToolsAsync(["avbtool", "openssl"], cancellationToken);
        var fullOutput = Path.GetFullPath(output);
        var work = Path.Combine(Path.GetTempPath(), "homeharbor-avb-public-key-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var encodedKey = Path.Combine(work, "homeharbor-avb-public-key.avbpub");
            var publicKey = Env.Optional("HOMEHARBOR_AVB_PUBLIC_KEY");
            if (!string.IsNullOrWhiteSpace(publicKey))
            {
                RequireFile(publicKey, "HOMEHARBOR_AVB_PUBLIC_KEY does not point to a readable file");
                if (IsEncodedAvbPublicKey(publicKey))
                {
                    File.Copy(publicKey, encodedKey, overwrite: true);
                }
                else
                {
                    await RunRequiredAsync("avbtool", ["extract_public_key", "--key", publicKey, "--output", encodedKey], cancellationToken);
                }
            }
            else
            {
                var secureBootKey = Env.Optional("HOMEHARBOR_SECURE_BOOT_KEY");
                if (!string.IsNullOrWhiteSpace(secureBootKey))
                {
                    RequireFile(secureBootKey, "HOMEHARBOR_SECURE_BOOT_KEY does not point to a readable file");
                    await RunRequiredAsync("avbtool", ["extract_public_key", "--key", secureBootKey, "--output", encodedKey], cancellationToken);
                }
                else
                {
                    var cert = Env.String("HOMEHARBOR_SECURE_BOOT_CERT", Path.Combine(_root, "certs", "homeharbor-secure-boot.crt"));
                    RequireFile(cert, "no AVB public key source found; set HOMEHARBOR_AVB_PUBLIC_KEY or HOMEHARBOR_SECURE_BOOT_KEY");
                    var pem = Path.Combine(work, "secure-boot-public.pem");
                    var result = await _runner.RunAsync(
                        "openssl",
                        ["x509", "-in", cert, "-pubkey", "-noout"],
                        new CommandRunOptions(StreamError: true),
                        cancellationToken);
                    _ = result.EnsureSuccess("openssl x509 failed");
                    await File.WriteAllTextAsync(pem, result.Stdout, cancellationToken);
                    await RunRequiredAsync("avbtool", ["extract_public_key", "--key", pem, "--output", encodedKey], cancellationToken);
                }
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            await File.WriteAllTextAsync(fullOutput, RenderAvbPublicKeyHeader(await File.ReadAllBytesAsync(encodedKey, cancellationToken)), Encoding.ASCII, cancellationToken);
        }
        finally
        {
            DeleteIfExists(work);
        }
    }

    private static string RenderAvbPublicKeyHeader(byte[] data)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("#include <stdint.h>");
        _ = builder.AppendLine(CultureInfo.InvariantCulture, $"#define HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE {data.Length}U");
        _ = builder.AppendLine("static const uint8_t HomeHarborTrustedAvbPublicKey[HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE] = {");
        for (var i = 0; i < data.Length; i += 12)
        {
            var chunk = data.Skip(i).Take(12).Select(value => "0x" + value.ToString("x2", CultureInfo.InvariantCulture));
            _ = builder.Append("    ");
            _ = builder.Append(string.Join(", ", chunk));
            if (i + 12 < data.Length)
            {
                _ = builder.Append(',');
            }

            _ = builder.AppendLine();
        }

        _ = builder.AppendLine("};");
        return builder.ToString();
    }

    private static bool IsEncodedAvbPublicKey(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8)
        {
            return false;
        }

        var bits = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        return bits is 2048 or 4096 or 8192 && data.Length == 8 + bits / 8 * 2;
    }

    private static void RequireSafeVersion(string version)
    {
        if (!SecurityGuards.IsSafeVersion(version))
        {
            throw new InvalidOperationException("version must contain only letters, numbers, dot, underscore, and dash: " + version);
        }
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(message + ": " + path, path);
        }
    }

    private async Task RequireToolsAsync(IEnumerable<string> tools, CancellationToken cancellationToken)
    {
        foreach (var tool in tools)
        {
            var result = await _runner.RunAsync(
                "sh",
                ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", tool],
                new CommandRunOptions(ThrowOnStartFailure: false),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("missing required tool: " + tool);
            }
        }
    }

    private async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await _runner.RunAsync(
            fileName,
            arguments,
            new CommandRunOptions(WorkingDirectory: workingDirectory, StreamOutput: true, StreamError: true, EnvironmentOverride: environment),
            cancellationToken);
        _ = result.EnsureSuccess();
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
