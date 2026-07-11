using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeHarbor.Tooling;

[assembly: InternalsVisibleTo("HomeHarbor.Tests")]

var runner = new ProcessCommandRunner();
try
{
    return await AgentProgram.RunAsync(args, runner, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return ex is ArgumentException ? 2 : 1;
}

internal static partial class AgentProgram
{
    private const string RaidBackendFilesystem = "filesystem";
    private const string RaidBackendMdadm = "mdadm";
    private const string MdadmArrayName = "homeharbor-data";
    private const string MdadmArrayPath = "/dev/md/homeharbor-data";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task<int> RunAsync(string[] args, ICommandRunner runner, CancellationToken cancellationToken)
    {
        return InvokeAsync(CreateRootCommand(runner), args, cancellationToken);
    }

    private static async Task<int> InvokeAsync(RootCommand root, string[] args, CancellationToken cancellationToken)
    {
        var parseResult = root.Parse(args);
        var exitCode = await parseResult.InvokeAsync(
            new InvocationConfiguration { EnableDefaultExceptionHandler = false },
            cancellationToken);
        return parseResult.Errors.Count > 0 && exitCode != 0 ? 2 : exitCode;
    }

    private static string? ApiSocketValue(
        ParseResult parseResult,
        Option<string> apiUrlOption,
        Option<string?> apiSocketOption,
        Option<bool> noApiSocketOption)
    {
        _ = apiUrlOption;
        _ = noApiSocketOption;
        var lastApiTransportToken = parseResult.Tokens
            .LastOrDefault(token => token.Value is "--api-url" or "--api-socket" or "--no-api-socket")
            ?.Value;
        return lastApiTransportToken switch
        {
            "--api-socket" => parseResult.GetValue(apiSocketOption),
            "--api-url" or "--no-api-socket" => null,
            _ => "/run/homeharbor-api/api.sock"
        };
    }

    private static Option<string> StringOption(string name, string defaultValue, string description)
        => new(name)
        {
            DefaultValueFactory = _ => defaultValue,
            Description = description
        };

    private static Option<string?> NullableStringOption(string name, string description)
        => new(name) { Description = description };

    private static Option<int> NonNegativeIntOption(string name, int defaultValue, string description)
    {
        var option = new Option<int>(name)
        {
            DefaultValueFactory = _ => defaultValue,
            Description = description
        };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
            {
                result.AddError(name + " must be a non-negative integer.");
            }
        });
        return option;
    }

    private static Option<long> NonNegativeLongOption(string name, Func<long> defaultValue, string description)
    {
        var option = new Option<long>(name)
        {
            DefaultValueFactory = _ => defaultValue(),
            Description = description
        };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<long>() < 0)
            {
                result.AddError(name + " must be a non-negative integer.");
            }
        });
        return option;
    }

    private static Argument<string> RequiredArgument(string name, string description)
        => new(name) { Description = description };

    private static Argument<string> OptionalArgument(string name, string defaultValue, string description)
        => new(name)
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => defaultValue,
            Description = description
        };

    private static Task<int> HelpAsync()
    {
        PrintHelp(Console.Out);
        return Task.FromResult(0);
    }

    private static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine("""
            Usage: HomeHarbor.Agent <command>

            Commands:
              firstboot
              consume-setup-bootstrap
              postgres-init
              postgres-bootstrap
              ensure-caddy-config
              display-tls-trust [--certificate PATH] [--consoles PATHS]
              render-caddyfile
              storage-health
              ensure-smb-config
              apply-smb
              apply-containers
              apply-system-apps
              boot-attempt [--state-dir PATH] [--esp PATH] [--window-seconds N] [--threshold N] [--now UNIX_SECONDS] [--dry-run]
              boot-success [--state-dir PATH] [--ota-state-dir PATH] [--esp PATH] [--boot-env PATH] [--run-dir PATH] [--timeout-seconds N] [--health-url PATH_OR_URL] [--api-url URL] [--api-socket PATH|--no-api-socket] [--dry-run]
              ota-apply <bundle> [--public-key PATH] [--channel CHANNEL] [--boot-env PATH] [--dry-run]
              ota-commit [--state-dir PATH] [--esp PATH] [--boot-env PATH] [--run-dir PATH]
              storage-apply
              storage-postapply
              boot-state init|set-default|set-oneshot|set-recovery|clear-next ...
              verify-ota-manifest <manifest> <public-key>
              super table|create|remove ...
            """);
    }

    private static async Task<int> FirstbootAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        await EnsureDirAsync(runner, "/var/lib/homeharbor", 0750, "root", "homeharbor", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/homeharbor/ota", 0750, "root", "homeharbor", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/homeharbor/api", 0700, "homeharbor", "homeharbor", cancellationToken);
        await EnsureOtaChannelAsync(runner, cancellationToken);
        await EnsureKernelChannelAsync(runner, cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/homeharbor/secrets", 0700, "root", "root", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/homeharbor/storage", 0750, "homeharbor", "homeharbor", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/homeharbor/samba", 0750, "root", "root", cancellationToken);
        foreach (var child in new[] { "private", "state", "cache", "lock" })
        {
            await EnsureDirAsync(runner, "/var/lib/homeharbor/samba/" + child, 0750, "root", "root", cancellationToken);
        }

        await EnsureDirAsync(runner, "/var/lib/caddy", 0750, "caddy", "caddy", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/homeharbor-caddy", 0750, "root", "caddy", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/NetworkManager", 0755, null, null, cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/containers", 0755, null, null, cancellationToken);
        await EnsureDirAsync(runner, "/run/homeharbor", 0750, "homeharbor", "homeharbor", cancellationToken);
        await EnsureDirAsync(runner, "/run/homeharbor-api", 2750, "homeharbor", "homeharbor-api", cancellationToken);
        await EnsureDirAsync(runner, "/run/homeharbor-smb-credentials", 0700, "homeharbor", "homeharbor", cancellationToken);

        if ((await runner.RunAsync("id", ["-u", "homeharbor-containers"], cancellationToken: cancellationToken)).ExitCode == 0)
        {
            await EnsureDirAsync(runner, "/var/lib/homeharbor-containers", 0750, "root", "homeharbor-containers", cancellationToken);
            await EnsureDirAsync(runner, "/var/lib/homeharbor-containers/.config", 0750, "root", "homeharbor-containers", cancellationToken);
            await EnsureDirAsync(runner, "/var/lib/homeharbor-containers/.config/containers", 0750, "root", "homeharbor-containers", cancellationToken);
            await EnsureDirAsync(runner, "/var/lib/homeharbor-containers/.config/containers/systemd", 0750, "root", "homeharbor-containers", cancellationToken);
            await EnsureDirAsync(runner, "/var/lib/homeharbor-containers/.local/share/containers", 0750, "homeharbor-containers", "homeharbor-containers", cancellationToken);
            await EnsureDirAsync(runner, "/var/lib/systemd/linger", 0755, null, null, cancellationToken);
            File.WriteAllText("/var/lib/systemd/linger/homeharbor-containers", string.Empty);
        }

        if (await IsHomeHarborDataMountAsync(runner, cancellationToken))
        {
            await EnsureDirAsync(runner, "/homeharbor-data", 0751, "root", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/apps", 0711, "root", "root", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/families", 0750, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps", 0750, "root", "root", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps/active", 0750, "root", "root", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps/staged", 0700, "root", "root", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps/versions", 0750, "root", "root", cancellationToken);
        }
        else
        {
            Console.WriteLine("homeharbor-data is not mounted; skipping data directory normalization.");
        }

        var setupCodePath = SetupPath("HOMEHARBOR_SETUP_BOOTSTRAP_CODE_PATH", "HomeHarbor__Setup__BootstrapCodePath", SetupBootstrapCode.DefaultCodePath);
        var setupCompletePath = SetupPath("HOMEHARBOR_SETUP_BOOTSTRAP_COMPLETE_PATH", "HomeHarbor__Setup__BootstrapCompletePath", SetupBootstrapCode.DefaultCompletePath);
        await EnsureDirAsync(runner, Path.GetDirectoryName(setupCodePath) ?? "/var/lib/homeharbor/setup", 0750, "root", "homeharbor", cancellationToken);
        var setupConsoles = Env.String("HOMEHARBOR_SETUP_PHYSICAL_CONSOLES", "/dev/console,/dev/tty1,/dev/ttyS0")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _ = await SetupBootstrapCode.EnsureAndDisplayAsync(runner, setupCodePath, setupCompletePath, setupConsoles, cancellationToken);

        return 0;
    }

    private static string SetupPath(string agentEnvironmentName, string apiEnvironmentName, string fallback)
        => Env.Optional(agentEnvironmentName) ?? Env.Optional(apiEnvironmentName) ?? fallback;

    private static async Task EnsureOtaChannelAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var path = Env.String("HOMEHARBOR_OTA_CHANNEL_FILE", "/var/lib/homeharbor/ota/channel");
        await EnsureOtaChannelFileAsync(runner, path, Env.String("HOMEHARBOR_CHANNEL", ReleaseChannel.Dev), cancellationToken);
    }

    private static async Task EnsureKernelChannelAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var path = Env.String("HOMEHARBOR_KERNEL_CHANNEL_FILE", "/var/lib/homeharbor/ota/kernel-channel");
        await EnsureKernelChannelFileAsync(runner, path, Env.String("HOMEHARBOR_KERNEL_CHANNEL", KernelChannel.Generic), cancellationToken);
    }

    internal static async Task EnsureOtaChannelFileAsync(
        ICommandRunner runner,
        string path,
        string defaultChannel,
        CancellationToken cancellationToken)
    {
        _ = RootPathGuard.RequireNoSymlinkComponents(path, "OTA channel file");
        if (File.Exists(path))
        {
            _ = ReleaseChannel.Require((await File.ReadAllLinesAsync(path, cancellationToken)).FirstOrDefault(), "OTA channel file");
            await NormalizeRootReadableFileAsync(runner, path, 0640, cancellationToken);
            return;
        }

        var channel = ReleaseChannel.Require(defaultChannel, "HOMEHARBOR_CHANNEL");
        await FileWrites.AtomicWriteTextAsync(path, channel + Environment.NewLine, 0640, cancellationToken);
        await NormalizeRootReadableFileAsync(runner, path, 0640, cancellationToken);
    }

    internal static async Task EnsureKernelChannelFileAsync(
        ICommandRunner runner,
        string path,
        string defaultChannel,
        CancellationToken cancellationToken)
    {
        _ = RootPathGuard.RequireNoSymlinkComponents(path, "kernel channel file");
        if (File.Exists(path))
        {
            _ = KernelChannel.Require((await File.ReadAllLinesAsync(path, cancellationToken)).FirstOrDefault(), "kernel channel file");
            await NormalizeRootReadableFileAsync(runner, path, 0640, cancellationToken);
            return;
        }

        var channel = KernelChannel.Require(defaultChannel, "HOMEHARBOR_KERNEL_CHANNEL");
        await FileWrites.AtomicWriteTextAsync(path, channel + Environment.NewLine, 0640, cancellationToken);
        await NormalizeRootReadableFileAsync(runner, path, 0640, cancellationToken);
    }

    private static async Task<int> PostgresInitAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var dataDir = Env.String("HOMEHARBOR_POSTGRES_DATA_DIR", "/homeharbor-data/postgresql/data");
        var parent = Path.GetDirectoryName(dataDir) ?? "/homeharbor-data/postgresql";
        await EnsureDirAsync(runner, parent, 0710, "root", "postgres", cancellationToken);
        await EnsureDirAsync(runner, dataDir, 0700, "postgres", "postgres", cancellationToken);
        if (new FileInfo(Path.Combine(dataDir, "PG_VERSION")).Exists)
        {
            return 0;
        }

        var result = await runner.RunAsync(
            "runuser",
            ["-u", "postgres", "--", "initdb", "-D", dataDir, "--username=postgres", "--auth-local=peer", "--auth-host=reject", "--no-instructions"],
            cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("initdb failed");
        return 0;
    }

    private static async Task<int> PostgresBootstrapAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        try
        {
            var socketDir = Env.String("HOMEHARBOR_POSTGRES_SOCKET_DIR", "/run/postgresql");
            var port = Env.String("HOMEHARBOR_POSTGRES_PORT", "5432");
            var database = Env.String("HOMEHARBOR_POSTGRES_DATABASE", "homeharbor");
            var role = Env.String("HOMEHARBOR_POSTGRES_ROLE", "homeharbor");
            ValidateSqlIdentifier(role, "role");
            ValidateSqlIdentifier(database, "database");

            for (var i = 0; i < 60; i++)
            {
                var ready = await runner.RunAsync("runuser", ["-u", "postgres", "--", "pg_isready", "-h", socketDir, "-p", port, "-d", "postgres"], cancellationToken: cancellationToken);
                if (ready.ExitCode == 0)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            _ = (await runner.RunAsync("runuser", ["-u", "postgres", "--", "pg_isready", "-h", socketDir, "-p", port, "-d", "postgres"], cancellationToken: cancellationToken))
                .EnsureSuccess("postgres did not become ready");

            var roleExists = await runner.RunAsync("runuser",
                ["-u", "postgres", "--", "psql", "-h", socketDir, "-p", port, "-d", "postgres", "-Atc", $"SELECT 1 FROM pg_roles WHERE rolname = '{SqlString(role)}';"],
                cancellationToken: cancellationToken);
            _ = roleExists.EnsureSuccess("failed to inspect postgres role");
            if (roleExists.Stdout.Trim() != "1")
            {
                _ = (await runner.RunAsync("runuser",
                    ["-u", "postgres", "--", "psql", "-h", socketDir, "-p", port, "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", $"CREATE ROLE \"{SqlIdentifier(role)}\" LOGIN;"],
                    cancellationToken: cancellationToken)).EnsureSuccess("failed to create postgres role");
            }

            var databaseExists = await runner.RunAsync("runuser",
                ["-u", "postgres", "--", "psql", "-h", socketDir, "-p", port, "-d", "postgres", "-Atc", $"SELECT 1 FROM pg_database WHERE datname = '{SqlString(database)}';"],
                cancellationToken: cancellationToken);
            _ = databaseExists.EnsureSuccess("failed to inspect postgres database");
            if (databaseExists.Stdout.Trim() != "1")
            {
                _ = (await runner.RunAsync("runuser",
                    ["-u", "postgres", "--", "createdb", "-h", socketDir, "-p", port, "-O", role, database],
                    cancellationToken: cancellationToken)).EnsureSuccess("failed to create postgres database");
            }

            await MigrateHomeHarborDatabaseAsync(runner, cancellationToken);
            await MarkStorageDatabaseReadyAsync(cancellationToken);
            _ = (await runner.RunAsync(
                "systemctl",
                ["--no-block", "try-restart", "homeharbor-api.service"],
                cancellationToken: cancellationToken)).EnsureSuccess("failed to queue HomeHarbor API restart");
            _ = (await runner.RunAsync(
                "systemctl",
                ["--no-block", "start", "homeharbor-caddy-render.service"],
                cancellationToken: cancellationToken)).EnsureSuccess("failed to queue Caddy render");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await MarkStorageDatabaseFailedAsync(ex.Message, cancellationToken);
            throw;
        }
    }

    private static async Task<int> EnsureCaddyConfigAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        await EnsureCaddyGroupIsolationAsync(runner, cancellationToken);
        var caddyState = Env.String("HOMEHARBOR_CADDY_STATE_DIR", "/var/lib/homeharbor-caddy");
        var caddyfile = RootPathGuard.RequireChildPath(
            Env.String("HOMEHARBOR_CADDYFILE", Path.Combine(caddyState, "Caddyfile")),
            caddyState,
            "Caddyfile");
        await EnsureDirAsync(runner, caddyState, 0750, "root", "caddy", cancellationToken);
        if (File.Exists(caddyfile) && new FileInfo(caddyfile).Length > 0)
        {
            return 0;
        }

        await FileWrites.AtomicWriteTextAsync(caddyfile, DefaultCaddyfile(), 0640, cancellationToken);
        await ChownAsync(runner, caddyfile, "root", "caddy", cancellationToken);
        return 0;
    }

    internal static async Task EnsureCaddyGroupIsolationAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var identity = await runner.RunAsync("id", ["-nG", "caddy"], cancellationToken: cancellationToken);
        if (identity.ExitCode != 0)
        {
            throw new InvalidOperationException("caddy service user is missing");
        }

        var groups = identity.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!groups.Contains("homeharbor-api", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("caddy service user is missing the dedicated homeharbor-api group");
        }
        if (groups.Contains("homeharbor", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("caddy service user must not belong to the broad homeharbor group");
        }
    }

    private static async Task<int> RenderCaddyfileAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var caddyState = Env.String("HOMEHARBOR_CADDY_STATE_DIR", "/var/lib/homeharbor-caddy");
        var caddyfile = RootPathGuard.RequireChildPath(
            Env.String("HOMEHARBOR_CADDYFILE", Path.Combine(caddyState, "Caddyfile")),
            caddyState,
            "Caddyfile");
        await EnsureDirAsync(runner, caddyState, 0750, "root", "caddy", cancellationToken);
        var temp = caddyfile + ".new";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var api = ApiClient();
                await api.DownloadAsync("/api/networking/proxy/caddyfile", temp, cancellationToken);
                break;
            }
            catch (HttpRequestException) when (i < 59)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            throw new InvalidOperationException("rendered Caddyfile was empty");
        }

        await ChownAsync(runner, temp, "root", "caddy", cancellationToken);
        await ChmodAsync(runner, temp, 0640, cancellationToken);
        _ = (await runner.RunAsync(
            "runuser",
            ["-u", "caddy", "--", "env", "HOME=/var/lib/caddy", "caddy", "validate", "--config", temp],
            cancellationToken: cancellationToken))
            .EnsureSuccess("rendered Caddyfile validation failed");
        var backup = caddyfile + ".previous." + Guid.NewGuid().ToString("N");
        var hadExisting = File.Exists(caddyfile);
        if (hadExisting)
        {
            File.Copy(caddyfile, backup, overwrite: false);
        }

        try
        {
            File.Move(temp, caddyfile, overwrite: true);
            var reload = await runner.RunAsync(
                "caddy",
                ["reload", "--address", "unix//run/caddy/admin.sock", "--config", caddyfile, "--force"],
                cancellationToken: cancellationToken);
            if (reload.ExitCode != 0)
            {
                if (hadExisting)
                {
                    File.Move(backup, caddyfile, overwrite: true);
                    _ = await runner.RunAsync(
                        "caddy",
                        ["reload", "--address", "unix//run/caddy/admin.sock", "--config", caddyfile, "--force"],
                        cancellationToken: cancellationToken);
                }
                else if (File.Exists(caddyfile))
                {
                    File.Delete(caddyfile);
                }

                _ = reload.EnsureSuccess("failed to reload Caddy with validated config; restored last-known-good config");
            }
        }
        finally
        {
            DeleteIfExists(temp);
            DeleteIfExists(backup);
        }

        return 0;
    }

    private static async Task<int> StorageHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ApiClient().PostJsonAsync("/api/storage/health/check", new { }, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsExpectedStorageHealthDeferral(ex.StatusCode))
        {
            Console.WriteLine("storage health check deferred until encrypted storage and a family space are ready");
        }
        return 0;
    }

    internal static bool IsExpectedStorageHealthDeferral(System.Net.HttpStatusCode? statusCode)
        => IsExpectedPreOobeDeferral(statusCode);

    internal static bool IsExpectedPreOobeDeferral(System.Net.HttpStatusCode? statusCode)
        => statusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.ServiceUnavailable;

    private static async Task<string?> GetReconcileDesiredOrDeferAsync(
        string path,
        string component,
        CancellationToken cancellationToken)
    {
        using var api = ApiClient();
        try
        {
            return await api.GetStringAsync(path, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsExpectedPreOobeDeferral(ex.StatusCode))
        {
            using var health = JsonDocument.Parse(
                await api.GetStringAsync("/api/system/health", cancellationToken));
            if (health.RootElement.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                string.Equals(status.GetString(), "storage-pending", StringComparison.Ordinal))
            {
                Console.WriteLine($"{component} reconcile deferred until encrypted storage and a family space are ready");
                return null;
            }

            throw;
        }
    }

    private static async Task<int> EnsureSmbConfigAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var state = Env.String("HOMEHARBOR_SMB_STATE_DIR", "/var/lib/homeharbor/samba");
        var conf = RootPathGuard.RequireChildPath(
            Env.String("HOMEHARBOR_SMB_CONF", Path.Combine(state, "smb.conf")),
            state,
            "SMB config");
        foreach (var dir in new[] { state, Path.Combine(state, "private"), Path.Combine(state, "state"), Path.Combine(state, "cache"), Path.Combine(state, "lock") })
        {
            await EnsureDirAsync(runner, dir, 0750, null, null, cancellationToken);
        }

        await EnsureDirAsync(runner, "/var/log/samba", 0755, null, null, cancellationToken);
        if (File.Exists(conf) && new FileInfo(conf).Length > 0)
        {
            return 0;
        }

        await FileWrites.AtomicWriteTextAsync(conf, DefaultSmbConf(), 0640, cancellationToken);
        return 0;
    }

    private static async Task<int> ApplySmbAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var state = Env.String("HOMEHARBOR_SMB_STATE_DIR", "/var/lib/homeharbor/samba");
        var conf = RootPathGuard.RequireChildPath(
            Env.String("HOMEHARBOR_SMB_CONF", Path.Combine(state, "smb.conf")),
            state,
            "SMB config");
        var credentialDir = Env.String("HOMEHARBOR_SMB_CREDENTIAL_DIR", "/run/homeharbor-smb-credentials");
        var dryRun = Env.Flag("HOMEHARBOR_DRY_RUN");
        var configFile = Env.Optional("HOMEHARBOR_SMB_CONFIG_FILE");
        if (!string.IsNullOrWhiteSpace(configFile))
        {
            throw new InvalidOperationException("raw SMB config overrides are not accepted; provide structured desired state instead");
        }

        var desiredFile = Env.Optional("HOMEHARBOR_SMB_DESIRED_FILE");
        var desiredJson = !string.IsNullOrWhiteSpace(desiredFile)
            ? await File.ReadAllTextAsync(desiredFile, cancellationToken)
            : await GetReconcileDesiredOrDeferAsync(
                "/api/smb/reconcile/desired",
                "SMB",
                cancellationToken);
        if (desiredJson is null)
        {
            return 0;
        }

        if (!dryRun)
        {
            foreach (var dir in new[] { state, Path.Combine(state, "private"), Path.Combine(state, "state"), Path.Combine(state, "cache"), Path.Combine(state, "lock") })
            {
                await EnsureDirAsync(runner, dir, 0750, null, null, cancellationToken);
            }

            await EnsureDirAsync(runner, credentialDir, 0700, null, null, cancellationToken);
            await EnsureDirAsync(runner, "/var/log/samba", 0755, null, null, cancellationToken);
        }

        var config = BuildValidatedSmbConfig(
            desiredJson,
            Env.String("HOMEHARBOR_SMB_DATA_ROOT", "/homeharbor-data"));
        if (dryRun)
        {
            Console.WriteLine($"dry-run smb config {conf}");
        }
        else
        {
            var temp = Path.Combine(state, "smb.conf." + Guid.NewGuid().ToString("N"));
            try
            {
                await FileWrites.AtomicWriteTextAsync(temp, config, 0600, cancellationToken);

                _ = (await runner.RunAsync("testparm", ["-s", "--suppress-prompt", temp], cancellationToken: cancellationToken))
                    .EnsureSuccess("SMB config validation failed");
                RefuseReadOnlyRootfsPath(conf, "smb.conf");
                var backup = conf + ".previous." + Guid.NewGuid().ToString("N");
                var hadExisting = File.Exists(conf);
                if (hadExisting)
                {
                    File.Copy(conf, backup, overwrite: false);
                }

                File.Move(temp, conf, overwrite: true);
                var restart = await runner.RunAsync("systemctl", ["restart", "homeharbor-smbd.service", "homeharbor-nmbd.service"], cancellationToken: cancellationToken);
                if (restart.ExitCode != 0)
                {
                    if (hadExisting)
                    {
                        File.Move(backup, conf, overwrite: true);
                        _ = await runner.RunAsync("systemctl", ["restart", "homeharbor-smbd.service", "homeharbor-nmbd.service"], cancellationToken: cancellationToken);
                    }
                    else if (File.Exists(conf))
                    {
                        File.Delete(conf);
                    }

                    _ = restart.EnsureSuccess("SMB services rejected validated config; restored last-known-good config");
                }

                DeleteIfExists(backup);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        var credentialFiles = Directory.Exists(credentialDir)
            ? Directory.EnumerateFiles(credentialDir, "*.json").OrderBy(p => p, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        foreach (var credentialFile in credentialFiles)
        {
            _ = RootPathGuard.RequireNoSymlinkComponents(credentialFile, "SMB credential request");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(credentialFile, cancellationToken));
            var root = doc.RootElement;
            var action = JsonString(root, "action") ?? "upsert";
            var unixUser = JsonString(root, "unixUser") ?? JsonString(root, "username") ?? string.Empty;
            ValidateSmbUnixUser(unixUser);

            if (dryRun)
            {
                Console.WriteLine($"dry-run smb {action} {unixUser}");
                continue;
            }

            switch (action)
            {
                case "upsert":
                    var password = JsonString(root, "password") ?? string.Empty;
                    if (string.IsNullOrEmpty(password))
                    {
                        throw new InvalidOperationException("missing password in " + credentialFile);
                    }

                    _ = (await runner.RunAsync("smbpasswd", ["-s", "--configfile=" + conf, "-a", unixUser], new CommandRunOptions(StandardInput: password + "\n" + password + "\n"), cancellationToken))
                        .EnsureSuccess("smbpasswd add failed");
                    _ = (await runner.RunAsync("smbpasswd", ["--configfile=" + conf, "-e", unixUser], cancellationToken: cancellationToken))
                        .EnsureSuccess("smbpasswd enable failed");
                    File.Delete(credentialFile);
                    break;
                case "revoke":
                    _ = await runner.RunAsync("pdbedit", ["--configfile=" + conf, "-x", unixUser], cancellationToken: cancellationToken);
                    File.Delete(credentialFile);
                    break;
                default:
                    throw new InvalidOperationException("unknown SMB credential action: " + action);
            }
        }

        if (!dryRun)
        {
            await ReconcileSmbResultAsync(cancellationToken);
        }

        return 0;
    }

    private static async Task<int> ApplyContainersAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var user = Env.String("HOMEHARBOR_CONTAINER_USER", "homeharbor-containers");
        var home = Env.String("HOMEHARBOR_CONTAINER_HOME", "/var/lib/homeharbor-containers");
        var quadletDir = Env.String("HOMEHARBOR_QUADLET_DIR", Path.Combine(home, ".config/containers/systemd"));
        var dataRoot = Env.String("HOMEHARBOR_CONTAINER_DATA_ROOT", "/homeharbor-data");
        var dryRun = Env.Flag("HOMEHARBOR_DRY_RUN");
        RefuseReadOnlyRootfsPath(quadletDir, "Quadlet directory");

        var desiredOverride = Env.Optional("HOMEHARBOR_CONTAINER_DESIRED_FILE");
        var desiredJson = !string.IsNullOrWhiteSpace(desiredOverride)
            ? await File.ReadAllTextAsync(desiredOverride, cancellationToken)
            : await GetReconcileDesiredOrDeferAsync(
                "/api/containers/reconcile/desired",
                "container",
                cancellationToken);
        if (desiredJson is null)
        {
            return 0;
        }

        var uid = dryRun
            ? (await runner.RunAsync("id", ["-u", user], cancellationToken: cancellationToken)).Stdout.Trim()
            : (await runner.RunAsync("id", ["-u", user], cancellationToken: cancellationToken)).EnsureSuccess("container user missing").Stdout.Trim();
        var gid = dryRun
            ? (await runner.RunAsync("id", ["-g", user], cancellationToken: cancellationToken)).Stdout.Trim()
            : (await runner.RunAsync("id", ["-g", user], cancellationToken: cancellationToken)).EnsureSuccess("container group missing").Stdout.Trim();
        if (string.IsNullOrWhiteSpace(uid)) uid = "0";
        if (string.IsNullOrWhiteSpace(gid)) gid = "0";

        if (!dryRun)
        {
            await EnsureDirAsync(runner, home, 0750, "root", gid, cancellationToken);
            await EnsureDirAsync(runner, Path.Combine(home, ".config"), 0750, "root", gid, cancellationToken);
            await EnsureDirAsync(runner, Path.Combine(home, ".config", "containers"), 0750, "root", gid, cancellationToken);
            await EnsureDirAsync(runner, Path.Combine(home, ".local", "share", "containers"), 0750, uid, gid, cancellationToken);
        }

        if (!dryRun)
        {
            await EnsureDirAsync(runner, quadletDir, 0750, "root", gid, cancellationToken);
            await EnsureContainerUserManagerAsync(runner, uid, cancellationToken);
        }

        using var doc = JsonDocument.Parse(desiredJson);
        var results = new List<object>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = JsonString(item, "id") ?? string.Empty;
            var serviceName = JsonString(item, "serviceName") ?? string.Empty;
            var unitName = JsonString(item, "unitName") ?? string.Empty;
            var quadletFile = JsonString(item, "quadletFile") ?? string.Empty;
            ValidateContainerReconcileIdentity(id, serviceName, unitName, quadletFile);
            var desiredState = JsonString(item, "desiredState") ?? string.Empty;
            var requestedAction = JsonString(item, "requestedAction") ?? "none";
            if (desiredState is not ("running" or "stopped" or "deleted"))
            {
                throw new InvalidOperationException("unknown container desired state: " + desiredState);
            }
            if (requestedAction is not ("start" or "stop" or "restart" or "reload" or "none" or "delete"))
            {
                throw new InvalidOperationException("unknown container requested action: " + requestedAction);
            }
            var target = CheckedChildPath(quadletDir, quadletFile, "Quadlet file");
            RefuseReadOnlyRootfsPath(target, "Quadlet file");
            var runtimeState = desiredState;
            var error = string.Empty;
            var quadletChanged = false;

            if (desiredState == "deleted" || requestedAction == "delete")
            {
                await SystemctlUserAsync(runner, user, uid, dryRun, ["stop", unitName], cancellationToken, ignoreFailure: true);
                if (!dryRun && File.Exists(target))
                {
                    File.Delete(target);
                }

                runtimeState = "deleted";
            }
            else
            {
                if (!dryRun)
                {
                    await PrepareContainerAppRootAsync(
                        runner,
                        dataRoot,
                        Guid.Parse(id),
                        user,
                        uid,
                        gid,
                        cancellationToken);
                }
                var quadlet = BuildValidatedContainerQuadlet(item, dataRoot);
                quadletChanged = ContainerQuadletNeedsUpdate(target, quadlet);
                if (!dryRun)
                {
                    await FileWrites.AtomicWriteTextAsync(target, quadlet, 0640, cancellationToken);
                    await ChownAsync(runner, target, "root", gid, cancellationToken);
                }
            }

            await SystemctlUserAsync(runner, user, uid, dryRun, ["daemon-reload"], cancellationToken);
            switch (requestedAction)
            {
                case "start":
                    await SystemctlUserAsync(runner, user, uid, dryRun, ["start", unitName], cancellationToken);
                    runtimeState = "running";
                    break;
                case "stop":
                    await SystemctlUserAsync(runner, user, uid, dryRun, ["stop", unitName], cancellationToken, ignoreFailure: true);
                    runtimeState = "stopped";
                    break;
                case "restart":
                    await SystemctlUserAsync(runner, user, uid, dryRun, ["restart", unitName], cancellationToken);
                    runtimeState = "running";
                    break;
                case "reload":
                    if (desiredState == "running")
                    {
                        await SystemctlUserAsync(runner, user, uid, dryRun, ["restart", unitName], cancellationToken);
                        runtimeState = "running";
                    }
                    break;
                case "none":
                    if (desiredState == "running")
                    {
                        await SystemctlUserAsync(
                            runner,
                            user,
                            uid,
                            dryRun,
                            [quadletChanged ? "restart" : "start", unitName],
                            cancellationToken);
                        runtimeState = "running";
                    }
                    else
                    {
                        await SystemctlUserAsync(runner, user, uid, dryRun, ["stop", unitName], cancellationToken, ignoreFailure: true);
                        runtimeState = "stopped";
                    }
                    break;
                case "delete":
                    break;
                default:
                    error = "unknown requested action: " + requestedAction;
                    runtimeState = "failed";
                    break;
            }

            results.Add(new { id, runtimeState, error });
            Console.WriteLine($"processed {serviceName}: {runtimeState}");
        }

        if (!dryRun)
        {
            await ApiClient().PostJsonAsync("/api/containers/reconcile/result", new { containers = results }, cancellationToken);
        }

        return 0;
    }

    internal static bool ContainerQuadletNeedsUpdate(string path, string expected)
        => !File.Exists(path) ||
           !string.Equals(File.ReadAllText(path), expected, StringComparison.Ordinal);

    private static async Task<int> ApplySystemAppsAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var root = Env.String("HOMEHARBOR_SYSTEM_APPS_ROOT", "/homeharbor-data/system-apps");
        var activeRoot = Path.Combine(root, "active");
        var stagedRoot = Path.Combine(root, "staged");
        var versionsRoot = Path.Combine(root, "versions");
        var wrapperRoot = Env.String("HOMEHARBOR_SYSTEM_APP_WRAPPER_DIR", "/run/homeharbor/system-apps/bin");
        var publicKey = Env.String("HOMEHARBOR_RELEASE_PUBLIC_KEY", "/etc/homeharbor/release.pub.pem");
        var releaseChannel = ReleaseChannel.Require(
            ReadFirstToken(Env.String("HOMEHARBOR_OTA_CHANNEL_FILE", "/var/lib/homeharbor/ota/channel")),
            "system app current release channel");
        var kernelChannel = KernelChannel.Require(
            ReadFirstToken(Env.String("HOMEHARBOR_KERNEL_CHANNEL_FILE", "/var/lib/homeharbor/ota/kernel-channel")),
            "system app current kernel channel");
        RootPathGuard.RequireSystemAppRoots(root, wrapperRoot);
        RefuseReadOnlyRootfsPath(root, "system apps root");
        RefuseReadOnlyRootfsPath(wrapperRoot, "system app wrapper directory");

        await EnsureDirAsync(runner, root, 0750, "root", "root", cancellationToken);
        await EnsureDirAsync(runner, activeRoot, 0750, "root", "root", cancellationToken);
        await EnsureDirAsync(runner, stagedRoot, 0700, "root", "root", cancellationToken);
        await EnsureDirAsync(runner, versionsRoot, 0750, "root", "root", cancellationToken);
        DeletePath(wrapperRoot);
        await EnsureDirAsync(runner, wrapperRoot, 0755, "root", "root", cancellationToken);

        using var api = ApiClient();
        using var doc = JsonDocument.Parse(await api.GetStringAsync("/api/apps/reconcile/desired", cancellationToken));
        var results = new List<object>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = JsonString(item, "id") ?? string.Empty;
            var appKey = JsonString(item, "appKey") ?? string.Empty;
            var desiredState = JsonString(item, "desiredState") ?? string.Empty;
            IReadOnlyList<string> commands = [];
            var runtimeState = "unknown";
            var error = string.Empty;
            var installedVersion = JsonString(item, "installedVersion") ?? string.Empty;
            var activeVersion = JsonString(item, "activeVersion") ?? string.Empty;
            var requiresReboot = false;

            try
            {
                ValidateSystemAppKey(appKey);
                var signedHhaf = await VerifyDesiredSystemAppManifestAsync(
                    item,
                    appKey,
                    releaseChannel,
                    publicKey,
                    cancellationToken: cancellationToken);
                var systemInstall = signedHhaf.Install as HomeHarborSystemAppInstall
                    ?? throw new InvalidOperationException("signed HHAF does not describe a system app install");
                var manifestUrl = systemInstall.ManifestUrl;
                commands = SystemAppCommands(appKey, systemInstall.Commands);
                if (desiredState == "deleted")
                {
                    RemoveSystemApp(appKey, activeRoot, wrapperRoot, commands);
                    runtimeState = "remove-pending-reboot";
                    requiresReboot = true;
                    installedVersion = string.Empty;
                    activeVersion = string.Empty;
                }
                else if (desiredState == "installed")
                {
                    var applied = await InstallSystemAppPayloadAsync(
                        appKey,
                        manifestUrl,
                        signedHhaf.Version,
                        publicKey,
                        releaseChannel,
                        kernelChannel,
                        stagedRoot,
                        versionsRoot,
                        activeRoot,
                        wrapperRoot,
                        commands,
                        cancellationToken);
                    runtimeState = applied.RuntimeState;
                    installedVersion = applied.Version;
                    activeVersion = applied.Version;
                    requiresReboot = applied.RequiresReboot;
                    error = applied.Error;
                }
                else
                {
                    runtimeState = "failed";
                    error = "unknown desired state: " + desiredState;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or JsonException or CryptographicException or FormatException)
            {
                runtimeState = "failed";
                error = ex.Message;
                requiresReboot = false;
            }

            results.Add(new
            {
                id,
                runtimeState,
                desiredState,
                installedVersion,
                activeVersion,
                requiresReboot,
                error
            });
            Console.WriteLine($"processed system app {appKey}: {runtimeState}");
        }

        await api.PostJsonAsync("/api/apps/reconcile/result", new { apps = results }, cancellationToken);
        return 0;
    }

    private static async Task<SystemAppApplyState> InstallSystemAppPayloadAsync(
        string appKey,
        string manifestUrl,
        string expectedVersion,
        string publicKey,
        string releaseChannel,
        string kernelChannel,
        string stagedRoot,
        string versionsRoot,
        string activeRoot,
        string wrapperRoot,
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken)
    {
        const long maxSystemAppPayloadBytes = 2L * 1024 * 1024 * 1024;
        var work = Path.Combine(stagedRoot, appKey + "-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var manifestPath = Path.Combine(work, "manifest.json");
            var payloadPath = Path.Combine(work, "payload.tar.gz");
            var fileRoot = Env.Optional("HOMEHARBOR_SYSTEM_APP_FILE_ROOT");
            var manifestUri = BoundedUriFetcher.ValidateUri(
                manifestUrl,
                allowedFileRoot: fileRoot,
                label: "system app manifest URL");
            using var http = BoundedUriFetcher.CreateHttpClient(TimeSpan.FromSeconds(30));
            await BoundedUriFetcher.DownloadToFileAsync(
                http,
                manifestUrl,
                manifestPath,
                SystemAppPackageManifestVerifier.MaxManifestBytes,
                allowedFileRoot: fileRoot,
                label: "system app manifest",
                cancellationToken: cancellationToken);
            var manifest = await new SystemAppPackageManifestVerifier().VerifyAsync(manifestPath, publicKey, cancellationToken);
            if (!string.Equals(manifest.AppKey, appKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"system app manifest appKey {manifest.AppKey} does not match requested app {appKey}");
            }

            if (!string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"system app package version {manifest.Version} does not match signed HHAF version {expectedVersion}");
            }

            RequireSystemAppChannelMatch(manifest, releaseChannel, kernelChannel);

            await BoundedUriFetcher.DownloadToFileAsync(
                http,
                manifest.PayloadUrl,
                payloadPath,
                maxSystemAppPayloadBytes,
                sameOriginAs: manifestUri,
                allowedFileRoot: fileRoot,
                label: "system app payload",
                cancellationToken: cancellationToken);
            var actualPayloadSha = await Sha256FileAsync(payloadPath, cancellationToken);
            if (!string.Equals(actualPayloadSha, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"system app payload hash mismatch: expected {manifest.PayloadSha256}, actual {actualPayloadSha}");
            }

            var versionDir = Path.Combine(
                versionsRoot,
                appKey + "-" + SafeVersion(manifest.Version) + "-" + actualPayloadSha[..12] + "-" + Guid.NewGuid().ToString("N")[..8]);
            var extractedVersionDir = Path.Combine(versionsRoot, "." + appKey + "-" + SafeVersion(manifest.Version) + ".next-" + Guid.NewGuid().ToString("N"));
            try
            {
                await SystemAppPayloadExtractor.ExtractTarGzAsync(payloadPath, extractedVersionDir, cancellationToken);
                Directory.Move(extractedVersionDir, versionDir);
            }
            catch
            {
                DeletePath(extractedVersionDir);
                throw;
            }

            ValidateSystemAppCommands(versionDir, commands);
            ActivateSystemApp(appKey, versionDir, activeRoot);
            WriteSystemAppWrappers(appKey, wrapperRoot, activeRoot, commands);
            DeleteObsoleteSystemAppVersions(versionsRoot, appKey, versionDir);
            return new SystemAppApplyState(
                manifest.Version,
                "active-pending-reboot",
                RequiresReboot: true,
                Error: "root-level hot activation is disabled; reboot is required");
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    internal static async Task<HomeHarborAppManifest> VerifyDesiredSystemAppManifestAsync(
        JsonElement desired,
        string expectedAppKey,
        string currentReleaseChannel,
        string publicKey,
        ICommandRunner? signatureRunner = null,
        CancellationToken cancellationToken = default)
    {
        if (!desired.TryGetProperty("hhafManifest", out var manifestElement) ||
            manifestElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("system app desired state is missing its original signed HHAF manifest");
        }

        var verifier = new HomeHarborAppManifestVerifier(signatureRunner);
        var manifest = await verifier.VerifyAppManifestJsonAsync(
            manifestElement.GetRawText(),
            publicKey,
            source: "automation desired state",
            cancellationToken);
        if (!string.Equals(manifest.AppKey, expectedAppKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"signed HHAF appKey {manifest.AppKey} does not match desired app {expectedAppKey}");
        }

        var releaseChannel = ReleaseChannel.Require(currentReleaseChannel, "current release channel");
        if (!string.Equals(manifest.Channel, releaseChannel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"signed HHAF channel {manifest.Channel} does not match current release channel {releaseChannel}");
        }

        if (manifest.Install is not HomeHarborSystemAppInstall)
        {
            throw new InvalidOperationException("signed HHAF does not describe a system app install");
        }

        return manifest;
    }

    internal static void RequireSystemAppChannelMatch(
        SystemAppPackageManifest manifest,
        string currentReleaseChannel,
        string currentKernelChannel)
    {
        var release = ReleaseChannel.Require(currentReleaseChannel, "current release channel");
        if (!string.Equals(manifest.Channel, release, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"system app channel {manifest.Channel} does not match current release channel {release}");
        }

        if (manifest.KernelChannel is { } requiredKernel)
        {
            var currentKernel = KernelChannel.Require(currentKernelChannel, "current kernel channel");
            if (!string.Equals(requiredKernel, currentKernel, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"system app kernel channel {requiredKernel} does not match current kernel channel {currentKernel}");
            }
        }
    }

    private static string ReadFirstToken(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("required channel file was not found: " + path);
        }

        return File.ReadLines(path)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .FirstOrDefault(token => !string.IsNullOrWhiteSpace(token))
            ?? throw new InvalidOperationException("required channel file is empty: " + path);
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static void ActivateSystemApp(string appKey, string versionDir, string activeRoot)
    {
        var activeLink = Path.Combine(activeRoot, appKey);
        var tempLink = Path.Combine(activeRoot, "." + appKey + ".next-" + Guid.NewGuid().ToString("N"));
        _ = File.CreateSymbolicLink(tempLink, Path.GetRelativePath(activeRoot, versionDir));
        File.Move(tempLink, activeLink, overwrite: true);
    }

    private static void RemoveSystemApp(string appKey, string activeRoot, string wrapperRoot, IReadOnlyList<string> commands)
    {
        DeletePath(Path.Combine(activeRoot, appKey));
        foreach (var command in commands)
        {
            DeletePath(Path.Combine(wrapperRoot, command));
        }
    }

    private static void WriteSystemAppWrappers(string appKey, string wrapperRoot, string activeRoot, IReadOnlyList<string> commands)
    {
        foreach (var command in commands)
        {
            _ = HomeHarborAppManifestVerifier.ValidateCommandName(command);
            var wrapper = Path.Combine(wrapperRoot, command);
            var appRoot = Path.Combine(activeRoot, appKey);
            var target = Path.Combine(appRoot, "usr", "bin", command);
            var script =
                "#!/bin/sh\n" +
                "APP_ROOT=\"" + appRoot + "\"\n" +
                "export LD_LIBRARY_PATH=\"${APP_ROOT}/usr/lib:${APP_ROOT}/usr/lib64${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}\"\n" +
                "exec \"" + target + "\" \"$@\"\n";
            FileWrites.AtomicWriteText(wrapper, script, 0755);
        }
    }

    internal static void ValidateSystemAppCommands(string versionDir, IReadOnlyList<string> commands)
    {
        var versionRoot = RootPathGuard.RequireNoSymlinkComponents(
            versionDir,
            "system app version directory",
            requireLeafDirectory: true);
        foreach (var command in commands)
        {
            _ = HomeHarborAppManifestVerifier.ValidateCommandName(command);
            var target = Path.GetFullPath(Path.Combine(versionRoot, "usr", "bin", command));
            if (!target.StartsWith(versionRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("system app command escapes its version root: " + command);
            }
            _ = RootPathGuard.RequireNoSymlinkComponents(target, "system app command");
            if (!File.Exists(target))
            {
                throw new InvalidOperationException("system app command is missing: " + command);
            }
            var attributes = File.GetAttributes(target);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException("system app command must be a regular file: " + command);
            }
            var mode = File.GetUnixFileMode(target);
            const UnixFileMode executeBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & executeBits) == 0)
            {
                throw new InvalidOperationException("system app command is not executable: " + command);
            }
        }
    }

    private static void DeleteObsoleteSystemAppVersions(string versionsRoot, string appKey, string activeVersionDir)
    {
        var activeFull = Path.GetFullPath(activeVersionDir);
        foreach (var candidate in Directory.EnumerateDirectories(versionsRoot, appKey + "-*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetFullPath(candidate), activeFull, StringComparison.Ordinal))
            {
                DeletePath(candidate);
            }
        }
    }

    private static IReadOnlyList<string> SystemAppCommands(string appKey, IReadOnlyList<string> desiredCommands)
    {
        _ = appKey;
        var commands = desiredCommands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(HomeHarborAppManifestVerifier.ValidateCommandName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return commands.Length > 0
            ? commands
            : [];
    }

    private static string SafeVersion(string version)
    {
        var safe = new string([.. version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')]);
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static void ValidateSystemAppKey(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey) || appKey.Length > 64 ||
            !(char.IsAsciiLetterLower(appKey[0]) || appKey[0] is >= '0' and <= '9') ||
            appKey.Any(c => !(char.IsAsciiLetterLower(c) || c is >= '0' and <= '9' || c is '.' or '_' or '-')))
        {
            throw new InvalidOperationException("system app key is invalid: " + appKey);
        }
    }

    private static void DeletePath(string path)
    {
        var safePath = RootPathGuard.RequireNoSymlinkComponents(path, "root-managed delete path");
        try
        {
            var attributes = File.GetAttributes(safePath);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(safePath, recursive: true);
                return;
            }

            File.Delete(safePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private static async Task<int> BootAttemptAsync(BootAttemptOptions options, ICommandRunner runner, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(options.StateDir);
        var attemptsPath = Path.Combine(options.StateDir, "attempts");
        var cutoff = options.Now - options.WindowSeconds;
        var attempts = File.Exists(attemptsPath)
            ? File.ReadLines(attemptsPath)
                .Select(line => long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (long?)null)
                .Where(value => value is not null && value >= cutoff)
                .Select(value => value!.Value)
                .ToList()
            : [];
        if (attempts.Count < options.Threshold)
        {
            attempts.Add(options.Now);
            await FileWrites.AtomicWriteTextAsync(attemptsPath, string.Join('\n', attempts) + "\n", cancellationToken: cancellationToken);
            return 0;
        }

        await FileWrites.AtomicWriteTextAsync(Path.Combine(options.StateDir, "recovery-requested-at"), DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\n", cancellationToken: cancellationToken);
        var bootState = BootState.Read(options.Esp);
        await EfiBootVariables.SetOneShotAsync(runner, bootState.RecoverySlot, bootState.RecoverySlot, "recovery", cancellationToken);
        DeleteIfExists(attemptsPath);
        if (options.DryRun)
        {
            Console.WriteLine("HomeHarbor bootloop threshold reached; recovery would be booted next.");
            return 0;
        }

        _ = await runner.RunAsync("systemctl", ["--no-block", "reboot"], cancellationToken: cancellationToken);
        return 0;
    }

    private static async Task<int> BootSuccessAsync(BootSuccessOptions options, ICommandRunner runner, CancellationToken cancellationToken)
    {
        using var http = new HomeHarborApiClient(options.ApiUrl, options.ApiSocket, Env.String("HOMEHARBOR_AUTOMATION_TOKEN_PATH", "/run/homeharbor/automation.jwt"));
        var healthUrl = options.HealthUrl;
        if (healthUrl.StartsWith("http://", StringComparison.Ordinal) || healthUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            healthUrl = new Uri(healthUrl).PathAndQuery;
        }

        for (var i = 0; i < options.TimeoutSeconds; i++)
        {
            try
            {
                _ = await http.GetStringAsync(healthUrl, cancellationToken);
                _ = Directory.CreateDirectory(options.StateDir);
                if (!options.DryRun)
                {
                    _ = await OtaCommitAsync(
                        new OtaCommitOptions(options.OtaStateDir, options.Esp, options.BootEnv, options.RunDir),
                        runner,
                        cancellationToken);
                }
                DeleteIfExists(Path.Combine(options.StateDir, "attempts"));
                DeleteIfExists(Path.Combine(options.StateDir, "recovery-requested-at"));
                await FileWrites.AtomicWriteTextAsync(Path.Combine(options.StateDir, "last-success-at"), DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\n", cancellationToken: cancellationToken);
                if (!options.DryRun)
                {
                    await EfiBootVariables.ClearOneShotAsync(runner, cancellationToken);
                }

                return 0;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException("HomeHarbor boot was not marked successful before timeout: " + healthUrl);
    }

    private static async Task<int> OtaCommitAsync(OtaCommitOptions options, ICommandRunner runner, CancellationToken cancellationToken)
    {
        _ = runner;
        _ = Directory.CreateDirectory(options.RunDir);
        var pending = Path.Combine(options.StateDir, "pending.json");
        if (!File.Exists(pending))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pending, cancellationToken));
        var active = BootEnvironment.Read(options.BootEnv);
        var activeBootSlot = GetBoot(active, "HOMEHARBOR_BOOT_SLOT");
        var activeRootSlot = GetBoot(active, "HOMEHARBOR_SLOT");
        var bootMatches = activeBootSlot == JsonString(doc.RootElement, "targetBootSlot");
        var rootMatches = activeRootSlot == JsonString(doc.RootElement, "targetRootSlot") &&
            GetBoot(active, "HOMEHARBOR_ROOT_LOGICAL") == JsonString(doc.RootElement, "rootLogical") &&
            GetBoot(active, "HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST") == JsonString(doc.RootElement, "rootDescriptorDigest");
        var modulesMatches = GetBoot(active, "HOMEHARBOR_MODULES_LOGICAL") == JsonString(doc.RootElement, "modulesLogical") &&
            GetBoot(active, "HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST") == JsonString(doc.RootElement, "modulesDescriptorDigest");
        var firmwareMatches = GetBoot(active, "HOMEHARBOR_FIRMWARE_LOGICAL") == JsonString(doc.RootElement, "firmwareLogical") &&
            GetBoot(active, "HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST") == JsonString(doc.RootElement, "firmwareDescriptorDigest");
        var vbmetaMatches = GetBoot(active, "HOMEHARBOR_VBMETA_PARTITION") == JsonString(doc.RootElement, "vbmetaPartition") &&
            GetBoot(active, "HOMEHARBOR_VBMETA_DIGEST") == JsonString(doc.RootElement, "vbmetaDigest");
        if (bootMatches && rootMatches && modulesMatches && firmwareMatches && vbmetaMatches && !string.IsNullOrWhiteSpace(activeBootSlot))
        {
            BootState.SetDefault(options.Esp, activeBootSlot, activeRootSlot);
            var targetRecoverySlot = JsonString(doc.RootElement, "targetRecoverySlot");
            if (!string.IsNullOrWhiteSpace(targetRecoverySlot))
            {
                BootState.SetRecovery(options.Esp, targetRecoverySlot);
            }

            var committed = Path.Combine(options.StateDir, "last-committed.json");
            File.Move(pending, committed, overwrite: true);
            File.Copy(committed, Path.Combine(options.RunDir, "last-committed-ota"), overwrite: true);
        }

        return 0;
    }

    private static async Task<int> StorageApplyAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var stateDir = Env.String("HOMEHARBOR_STORAGE_STATE_DIR", "/var/lib/homeharbor/storage");
        _ = RootPathGuard.RequireNoSymlinkComponents(stateDir, "storage state directory");
        var pendingPlan = Path.Combine(stateDir, "pending-plan.json");
        var statusFile = Path.Combine(stateDir, "status.json");
        var appliedPlan = Path.Combine(stateDir, "applied-plan.json");
        _ = RootPathGuard.RequireNoSymlinkComponents(pendingPlan, "pending storage plan");
        _ = RootPathGuard.RequireNoSymlinkComponents(statusFile, "storage status file");
        _ = RootPathGuard.RequireNoSymlinkComponents(appliedPlan, "applied storage plan");
        var allowFiles = Env.Flag("HOMEHARBOR_STORAGE_APPLY_ALLOW_FILES");
        var dryRun = Env.Flag("HOMEHARBOR_STORAGE_APPLY_DRY_RUN");
        if (!File.Exists(pendingPlan))
        {
            if (File.Exists(appliedPlan))
            {
                var applied = await ReadAppliedStoragePlanAsync(appliedPlan, cancellationToken);
                try
                {
                    await EnsureAppliedStorageMountedAsync(runner, statusFile, applied, dryRun, cancellationToken);
                }
                catch (Exception ex)
                {
                    await WriteStorageStatusAsync(statusFile, "Failed", 100, "Storage plan failed.", ex.Message, applied.PlanId, cancellationToken);
                    throw;
                }
            }

            return 0;
        }

        async Task Fail(string message, string? planId = null)
        {
            await WriteStorageStatusAsync(statusFile, "Failed", 100, "Storage plan failed.", message, planId, cancellationToken);
            throw new InvalidOperationException(message);
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(pendingPlan, cancellationToken));
        var root = doc.RootElement;
        var planId = JsonString(root, "planId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(planId)) await Fail("pending storage plan is missing planId");
        var confirm = JsonString(root, "confirmPhrase") ?? string.Empty;
        if (confirm != "APPLY STORAGE PLAN " + planId) await Fail("pending storage plan confirmation phrase is invalid", planId);
        var fileSystem = JsonString(root, "fileSystem") ?? "btrfs";
        if (fileSystem is not ("btrfs" or "xfs" or "zfs")) await Fail("unsupported file system: " + fileSystem, planId);
        var dataProfile = JsonString(root, "dataProfile") ?? (fileSystem == "btrfs" ? string.Empty : "single");
        var metadataProfile = JsonString(root, "metadataProfile") ?? (fileSystem == "btrfs" ? string.Empty : fileSystem);
        var raidMode = JsonString(root, "raidMode") ?? fileSystem switch
        {
            "btrfs" => dataProfile == "raid1" ? "mirror" : dataProfile,
            "xfs" => "single",
            "zfs" => dataProfile,
            _ => "single"
        };
        var raidBackend = JsonString(root, "raidBackend") ?? RaidBackendFilesystem;
        if (raidBackend is not (RaidBackendFilesystem or RaidBackendMdadm)) await Fail("unsupported RAID backend: " + raidBackend, planId);
        if (raidBackend == RaidBackendMdadm)
        {
            if (fileSystem is not ("btrfs" or "xfs")) await Fail("mdadm RAID backend supports only Btrfs or XFS data filesystems", planId);
            if (raidMode is not ("raid5" or "raid6")) await Fail("unsupported mdadm RAID mode: " + raidMode, planId);
        }
        if (fileSystem == "btrfs" && dataProfile is not ("single" or "raid1" or "raid10")) await Fail("unsupported data profile: " + dataProfile, planId);
        if (fileSystem == "btrfs" && metadataProfile is not ("dup" or "single" or "raid1" or "raid1c3" or "raid10")) await Fail("unsupported metadata profile: " + metadataProfile, planId);
        if (fileSystem == "xfs" && raidBackend == RaidBackendFilesystem && (raidMode != "single" || dataProfile != "single")) await Fail("unsupported XFS RAID mode: " + raidMode, planId);
        if (fileSystem == "zfs" && (raidBackend != RaidBackendFilesystem || raidMode is not ("single" or "mirror" or "raid10" or "raidz1" or "raidz2"))) await Fail("unsupported ZFS RAID mode: " + raidMode, planId);
        var unlockMode = JsonString(root, "unlockMode") ?? "passphrase";
        if (unlockMode is not ("passphrase" or "tpm2")) await Fail("unsupported data unlock mode: " + unlockMode, planId);
        var plannedTargets = root.GetProperty("devices").EnumerateArray()
            .Select(device => new PendingStorageTarget(
                JsonString(device, "path") ?? string.Empty,
                JsonString(device, "kind") ?? "whole-disk",
                JsonInt64(device, "sizeBytes") ?? 0,
                JsonString(device, "model"),
                JsonString(device, "serial"),
                JsonString(device, "transport"),
                JsonString(device, "stableId")))
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .ToArray();
        if (plannedTargets.Length == 0) await Fail("pending storage plan has no devices", planId);
        if (fileSystem == "xfs" && raidBackend == RaidBackendFilesystem && plannedTargets.Length != 1) await Fail("XFS storage plan requires exactly one device", planId);
        if (raidBackend == RaidBackendMdadm)
        {
            if (raidMode == "raid5" && plannedTargets.Length < 3) await Fail("RAID5 requires at least three devices", planId);
            if (raidMode == "raid6" && plannedTargets.Length < 4) await Fail("RAID6 requires at least four devices", planId);
        }
        if (fileSystem == "zfs")
        {
            if (raidMode == "mirror" && plannedTargets.Length < 2) await Fail("ZFS mirror requires at least two devices", planId);
            if (raidMode == "raid10" && (plannedTargets.Length < 4 || plannedTargets.Length % 2 != 0)) await Fail("ZFS RAID10 requires an even number of at least four devices", planId);
            if (raidMode == "raidz1" && plannedTargets.Length < 3) await Fail("ZFS RAIDZ1 requires at least three devices", planId);
            if (raidMode == "raidz2" && plannedTargets.Length < 4) await Fail("ZFS RAIDZ2 requires at least four devices", planId);
        }

        await WriteStorageStatusAsync(statusFile, "Running", 10, "Validating selected storage devices.", null, planId, cancellationToken);
        var resolvedDevices = new HashSet<string>(StringComparer.Ordinal);
        var validatedTargets = new List<PendingStorageTarget>();
        foreach (var target in plannedTargets)
        {
            var device = target.Path;
            string deviceReal;
            try
            {
                deviceReal = await ValidateStorageTargetIdentityAsync(runner, target, allowFiles, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                await Fail(ex.Message, planId);
                throw;
            }

            if (!resolvedDevices.Add(deviceReal)) await Fail("storage plan contains the same device more than once: " + device, planId);
            validatedTargets.Add(target with { ResolvedPath = deviceReal });
        }

        try
        {
            var rootAncestors = await RootAncestorDevicesAsync(runner, cancellationToken);
            foreach (var target in validatedTargets)
            {
                var device = target.ResolvedPath!;
                if (rootAncestors.Contains(device)) throw new InvalidOperationException("refusing to use the currently booted system disk: " + target.Path);
                if (await DeviceHasProtectedLabelAsync(runner, device, cancellationToken)) throw new InvalidOperationException("refusing to use HomeHarbor protected disk/partition: " + target.Path);
                if (target.Kind == "main-reserved" && !await DeviceHasLabelAsync(runner, device, "data-candidate", cancellationToken))
                {
                    throw new InvalidOperationException("main reserved storage target must be the data-candidate partition: " + target.Path);
                }
                if (await DeviceHasMountsAsync(runner, device, cancellationToken)) throw new InvalidOperationException("refusing to use a disk with mounted filesystems: " + target.Path);
            }
        }
        catch (InvalidOperationException ex)
        {
            await Fail(ex.Message, planId);
            throw;
        }

        try
        {
            await ApplyStoragePlanAsync(
                runner,
                statusFile,
                pendingPlan,
                appliedPlan,
                planId,
                validatedTargets,
                unlockMode,
                fileSystem,
                raidMode,
                raidBackend,
                dataProfile,
                metadataProfile,
                allowFiles,
                dryRun,
                cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteStorageStatusAsync(statusFile, "Failed", 100, "Storage plan failed.", ex.Message, planId, cancellationToken);
            throw;
        }

        return 0;
    }

    private static async Task<int> StoragePostApplyAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        if (!await IsHomeHarborDataMountAsync(runner, cancellationToken))
        {
            Console.WriteLine("homeharbor-data is not mounted; skipping post-apply service start.");
            return 0;
        }

        _ = (await runner.RunAsync(
            "systemctl",
            ["--no-block", "start", "homeharbor-postgresql-bootstrap.service"],
            cancellationToken: cancellationToken)).EnsureSuccess("failed to queue PostgreSQL bootstrap");
        return 0;
    }

    private static async Task MigrateHomeHarborDatabaseAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var apiDll = Env.String("HOMEHARBOR_API_DLL", "/usr/lib/homeharbor/api/HomeHarbor.Api.dll");
        var apiDirectory = Path.GetDirectoryName(apiDll) ?? "/usr/lib/homeharbor/api";
        _ = (await runner.RunAsync(
            "runuser",
            ["-u", "homeharbor", "--", "dotnet", apiDll, "database-migrate"],
            new CommandRunOptions(
                WorkingDirectory: apiDirectory,
                Timeout: TimeSpan.FromMinutes(5),
                StreamOutput: true,
                StreamError: true),
            cancellationToken)).EnsureSuccess("failed to migrate HomeHarbor database");
    }

    private static async Task MarkStorageDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        var statusFile = Env.String("HOMEHARBOR_STORAGE_STATUS_FILE", "/var/lib/homeharbor/storage/status.json");
        await WriteStorageStatusAsync(
            statusFile,
            "Succeeded",
            100,
            "Storage and database are ready.",
            null,
            await ReadStorageStatusPlanIdAsync(statusFile, cancellationToken),
            cancellationToken);
    }

    private static async Task MarkStorageDatabaseFailedAsync(string message, CancellationToken cancellationToken)
    {
        var statusFile = Env.String("HOMEHARBOR_STORAGE_STATUS_FILE", "/var/lib/homeharbor/storage/status.json");
        await WriteStorageStatusAsync(
            statusFile,
            "Failed",
            100,
            "Storage plan failed during database initialization.",
            message,
            await ReadStorageStatusPlanIdAsync(statusFile, cancellationToken),
            cancellationToken);
    }

    private static async Task ApplyStoragePlanAsync(
        ICommandRunner runner,
        string statusFile,
        string pendingPlan,
        string appliedPlan,
        string planId,
        IReadOnlyList<PendingStorageTarget> targets,
        string unlockMode,
        string fileSystem,
        string raidMode,
        string raidBackend,
        string dataProfile,
        string metadataProfile,
        bool allowFiles,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        await WriteStorageStatusAsync(statusFile, "Running", 20, "Preparing data unlock.", null, planId, cancellationToken);

        var devices = targets.Select(target => target.ResolvedPath ?? target.Path).ToArray();
        var mapperNames = MapperNames(devices.Length);
        if (dryRun)
        {
            Console.WriteLine("dry-run storage apply would format: " + string.Join(", ", devices));
            Console.WriteLine("dry-run storage apply would open mappers: " + string.Join(", ", mapperNames));
            Console.WriteLine("dry-run storage apply would configure unlock mode: " + unlockMode);
            Console.WriteLine($"dry-run storage apply would create {fileSystem} storage with RAID mode {raidMode} using {raidBackend} backend");
            if (raidBackend == RaidBackendMdadm)
            {
                Console.WriteLine($"dry-run storage apply would run mdadm --create {MdadmArrayPath} --level={MdadmLevel(raidMode)}");
            }
            if (fileSystem == "btrfs")
            {
                Console.WriteLine($"dry-run storage apply would run mkfs.btrfs -d {dataProfile} -m {metadataProfile}");
            }
            await WriteStorageStatusAsync(statusFile, "Succeeded", 100, "Storage plan dry run completed.", null, planId, cancellationToken);
            DeleteIfExists(pendingPlan);
            return;
        }

        var passphrase = await ReadStorageApplyPassphraseAsync(runner, cancellationToken);
        var keyFile = await WriteStorageApplyKeyFileAsync(passphrase, cancellationToken);
        try
        {
            await WriteStorageStatusAsync(statusFile, "Running", 30, "Formatting encrypted data devices.", null, planId, cancellationToken);
            await CloseDataMountAndMappersAsync(runner, mapperNames, cancellationToken);

            var appliedDevices = new List<AppliedStorageDevice>();
            for (var i = 0; i < devices.Length; i++)
            {
                var target = targets[i];
                var device = await ValidateStorageTargetIdentityAsync(runner, target, allowFiles, cancellationToken);
                var currentRootAncestors = await RootAncestorDevicesAsync(runner, cancellationToken);
                if (currentRootAncestors.Contains(device))
                {
                    throw new InvalidOperationException("refusing to format the currently booted system disk: " + target.Path);
                }
                if (await DeviceHasProtectedLabelAsync(runner, device, cancellationToken))
                {
                    throw new InvalidOperationException("refusing to format a HomeHarbor protected disk/partition: " + target.Path);
                }
                if (target.Kind == "main-reserved" && !await DeviceHasLabelAsync(runner, device, "data-candidate", cancellationToken))
                {
                    throw new InvalidOperationException("main reserved storage target is no longer the data-candidate partition: " + target.Path);
                }
                if (await DeviceHasMountsAsync(runner, device, cancellationToken))
                {
                    throw new InvalidOperationException("refusing to format a disk with mounted filesystems: " + target.Path);
                }

                var mapper = mapperNames[i];
                _ = (await runner.RunAsync("cryptsetup", ["luksFormat", "--type", "luks2", "--batch-mode", "--key-file", keyFile, device], cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to format data device");
                if (unlockMode == "tpm2")
                {
                    _ = (await runner.RunAsync(
                        "systemd-cryptenroll",
                        [device, "--unlock-key-file=" + keyFile, "--tpm2-device=auto", "--tpm2-pcrs=" + Env.String("HOMEHARBOR_TPM2_PCRS", "7")],
                        cancellationToken: cancellationToken))
                        .EnsureSuccess("failed to enroll TPM2 data unlock");
                }
                var luksUuid = (await runner.RunAsync("cryptsetup", ["luksUUID", device], cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to read LUKS UUID")
                    .Stdout.Trim();
                if (string.IsNullOrWhiteSpace(luksUuid))
                {
                    throw new InvalidOperationException("formatted data device did not report a LUKS UUID: " + device);
                }

                _ = (await runner.RunAsync("cryptsetup", ["open", "--key-file", keyFile, device, mapper], cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to open data device");
                appliedDevices.Add(new AppliedStorageDevice(device, luksUuid, mapper));
            }

            await WriteStorageStatusAsync(statusFile, "Running", 60, "Creating " + fileSystem.ToUpperInvariant() + " data filesystem.", null, planId, cancellationToken);
            var mapperPaths = mapperNames.Select(name => "/dev/mapper/" + name).ToArray();
            var createdStorage = await CreateDataFileSystemAsync(runner, fileSystem, raidMode, raidBackend, dataProfile, metadataProfile, mapperPaths, cancellationToken);
            if (string.IsNullOrWhiteSpace(createdStorage.FileSystemUuid))
            {
                throw new InvalidOperationException("new data filesystem did not report a UUID");
            }

            var applied = new AppliedStoragePlan(
                planId,
                unlockMode,
                fileSystem,
                raidMode,
                raidBackend,
                dataProfile,
                metadataProfile,
                createdStorage.FileSystemUuid,
                createdStorage.MdadmName,
                createdStorage.MdadmUuid,
                appliedDevices,
                DateTimeOffset.UtcNow);
            await FileWrites.AtomicWriteTextAsync(appliedPlan, JsonSerializer.Serialize(applied, JsonOptions), 0640, cancellationToken);
            await WriteDataUnlockMetadataAsync(unlockMode, cancellationToken);
            await WriteBootUnlockEnvAsync(applied, cancellationToken);
            await WriteDataUnlockModeEfiVariableAsync(runner, unlockMode, cancellationToken);

            await WriteStorageStatusAsync(statusFile, "Running", 80, "Mounting HomeHarbor data root.", null, planId, cancellationToken);
            await MountAppliedStorageAsync(runner, applied, cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data", 0751, "root", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/apps", 0711, "root", "root", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/families", 0750, "homeharbor", "homeharbor", cancellationToken);

            await WriteStorageStatusAsync(statusFile, "Running", 90, "Creating HomeHarbor database.", null, planId, cancellationToken);
            DeleteIfExists(pendingPlan);
        }
        finally
        {
            DeleteIfExists(keyFile);
        }
    }

    private static async Task EnsureAppliedStorageMountedAsync(
        ICommandRunner runner,
        string statusFile,
        AppliedStoragePlan applied,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (await IsAppliedStorageMountedAsync(runner, applied, cancellationToken))
        {
            return;
        }

        await WriteStorageStatusAsync(statusFile, "Running", 40, "Opening applied data storage.", null, applied.PlanId, cancellationToken);
        if (dryRun)
        {
            Console.WriteLine("dry-run storage apply would mount applied storage plan: " + applied.PlanId);
            await WriteStorageStatusAsync(statusFile, "Succeeded", 100, "Storage plan dry run completed.", null, applied.PlanId, cancellationToken);
            return;
        }

        if (applied.UnlockMode != "passphrase")
        {
            if (applied.Devices.All(device => File.Exists("/dev/mapper/" + device.MapperName)))
            {
                await MountAppliedStorageAsync(runner, applied, cancellationToken);
                await WriteStorageStatusAsync(statusFile, "Succeeded", 100, "Storage plan applied.", null, applied.PlanId, cancellationToken);
                return;
            }

            await CloseDataMountAndMappersAsync(runner, applied.Devices.Select(device => device.MapperName), cancellationToken);
            foreach (var device in applied.Devices)
            {
                _ = (await runner.RunAsync(
                    "cryptsetup",
                    ["open", "--token-only", StorageDeviceSource(device), device.MapperName],
                    cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to open TPM2 data device");
            }

            await MountAppliedStorageAsync(runner, applied, cancellationToken);
            await WriteStorageStatusAsync(statusFile, "Succeeded", 100, "Storage plan applied.", null, applied.PlanId, cancellationToken);
            return;
        }

        if (applied.Devices.All(device => File.Exists("/dev/mapper/" + device.MapperName)))
        {
            await MountAppliedStorageAsync(runner, applied, cancellationToken);
            await WriteStorageStatusAsync(statusFile, "Succeeded", 100, "Storage plan applied.", null, applied.PlanId, cancellationToken);
            return;
        }

        var passphrase = await ReadStorageApplyPassphraseAsync(runner, cancellationToken);
        var keyFile = await WriteStorageApplyKeyFileAsync(passphrase, cancellationToken);
        try
        {
            await CloseDataMountAndMappersAsync(runner, applied.Devices.Select(device => device.MapperName), cancellationToken);
            foreach (var device in applied.Devices)
            {
                _ = (await runner.RunAsync(
                    "cryptsetup",
                    ["open", "--key-file", keyFile, StorageDeviceSource(device), device.MapperName],
                    cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to open applied data device");
            }

            await MountAppliedStorageAsync(runner, applied, cancellationToken);
            await WriteStorageStatusAsync(statusFile, "Succeeded", 100, "Storage plan applied.", null, applied.PlanId, cancellationToken);
        }
        finally
        {
            DeleteIfExists(keyFile);
        }
    }

    private static async Task<CreatedDataStorage> CreateDataFileSystemAsync(
        ICommandRunner runner,
        string fileSystem,
        string raidMode,
        string raidBackend,
        string dataProfile,
        string metadataProfile,
        IReadOnlyList<string> mapperPaths,
        CancellationToken cancellationToken)
    {
        if (raidBackend == RaidBackendMdadm)
        {
            var mdadm = await CreateMdadmArrayAsync(runner, raidMode, mapperPaths, cancellationToken);
            var fsUuid = await CreateBlockFileSystemAsync(runner, fileSystem, dataProfile, metadataProfile, [mdadm.Path], cancellationToken);
            return new CreatedDataStorage(fsUuid, mdadm.Name, mdadm.Uuid);
        }

        switch (fileSystem)
        {
            case "btrfs":
            case "xfs":
                return new CreatedDataStorage(
                    await CreateBlockFileSystemAsync(runner, fileSystem, dataProfile, metadataProfile, mapperPaths, cancellationToken),
                    null,
                    null);
            case "zfs":
                var zpool = ZfsTool("zpool");
                var zfs = ZfsTool("zfs");
                _ = await runner.RunAsync(zpool, ["export", "homeharbor-data"], cancellationToken: cancellationToken);
                _ = (await runner.RunAsync(
                    zpool,
                    ["create", "-f", "-o", "ashift=12", "-O", "mountpoint=/homeharbor-data", "-O", "atime=off", "-O", "compression=zstd", "homeharbor-data", .. ZfsPoolMembers(raidMode, mapperPaths)],
                    cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to create ZFS data pool");
                _ = await runner.RunAsync(zfs, ["mount", "homeharbor-data"], cancellationToken: cancellationToken);
                return new CreatedDataStorage(
                    (await runner.RunAsync(zpool, ["get", "-H", "-o", "value", "guid", "homeharbor-data"], cancellationToken: cancellationToken))
                        .EnsureSuccess("failed to read ZFS pool GUID")
                        .Stdout.Trim(),
                    null,
                    null);
            default:
                throw new InvalidOperationException("unsupported file system: " + fileSystem);
        }
    }

    private static async Task<string> CreateBlockFileSystemAsync(
        ICommandRunner runner,
        string fileSystem,
        string dataProfile,
        string metadataProfile,
        IReadOnlyList<string> blockPaths,
        CancellationToken cancellationToken)
    {
        switch (fileSystem)
        {
            case "btrfs":
                _ = (await runner.RunAsync("mkfs.btrfs", ["-f", "-L", "data", "-d", dataProfile, "-m", metadataProfile, .. blockPaths], cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to create Btrfs data filesystem");
                return (await runner.RunAsync("blkid", ["-s", "UUID", "-o", "value", blockPaths[0]], cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to read Btrfs filesystem UUID")
                    .Stdout.Trim();
            case "xfs":
                if (blockPaths.Count != 1)
                {
                    throw new InvalidOperationException("XFS storage plans require exactly one block device.");
                }
                _ = (await runner.RunAsync("mkfs.xfs", ["-f", "-L", "data", blockPaths[0]], cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to create XFS data filesystem");
                return (await runner.RunAsync("blkid", ["-s", "UUID", "-o", "value", blockPaths[0]], cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to read XFS filesystem UUID")
                    .Stdout.Trim();
            default:
                throw new InvalidOperationException("unsupported block file system: " + fileSystem);
        }
    }

    private static async Task<CreatedMdadmArray> CreateMdadmArrayAsync(
        ICommandRunner runner,
        string raidMode,
        IReadOnlyList<string> mapperPaths,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(MdadmArrayPath) ?? "/dev/md");
        _ = await runner.RunAsync("mdadm", ["--stop", MdadmArrayPath], cancellationToken: cancellationToken);
        _ = (await runner.RunAsync(
            "mdadm",
            ["--create", MdadmArrayPath, "--metadata=1.2", "--name=" + MdadmArrayName, "--level=" + MdadmLevel(raidMode), "--raid-devices=" + mapperPaths.Count.ToString(CultureInfo.InvariantCulture), .. mapperPaths],
            cancellationToken: cancellationToken))
            .EnsureSuccess("failed to create mdadm data array");
        var export = (await runner.RunAsync("mdadm", ["--detail", "--export", MdadmArrayPath], cancellationToken: cancellationToken))
            .EnsureSuccess("failed to read mdadm data array details")
            .Stdout;
        var uuid = ParseMdadmExportValue(export, "MD_UUID");
        return string.IsNullOrWhiteSpace(uuid)
            ? throw new InvalidOperationException("new mdadm data array did not report a UUID")
            : new CreatedMdadmArray(MdadmArrayPath, MdadmArrayName, uuid);
    }

    private static IReadOnlyList<string> ZfsPoolMembers(string raidMode, IReadOnlyList<string> mapperPaths)
    {
        return raidMode switch
        {
            "single" => mapperPaths,
            "mirror" => ["mirror", .. mapperPaths],
            "raidz1" => ["raidz1", .. mapperPaths],
            "raidz2" => ["raidz2", .. mapperPaths],
            "raid10" => ZfsRaid10Members(mapperPaths),
            _ => throw new InvalidOperationException("unsupported ZFS RAID mode: " + raidMode)
        };
    }

    private static IReadOnlyList<string> ZfsRaid10Members(IReadOnlyList<string> mapperPaths)
    {
        if (mapperPaths.Count < 4 || mapperPaths.Count % 2 != 0)
        {
            throw new InvalidOperationException("ZFS RAID10 requires an even number of at least four mappers.");
        }

        var members = new List<string>();
        for (var i = 0; i < mapperPaths.Count; i += 2)
        {
            members.Add("mirror");
            members.Add(mapperPaths[i]);
            members.Add(mapperPaths[i + 1]);
        }

        return members;
    }

    private static int MdadmLevel(string raidMode)
        => raidMode switch
        {
            "raid5" => 5,
            "raid6" => 6,
            _ => throw new InvalidOperationException("unsupported mdadm RAID mode: " + raidMode)
        };

    private static string? ParseMdadmExportValue(string export, string key)
    {
        foreach (var line in export.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            if (string.Equals(line[..separator], key, StringComparison.Ordinal))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    private static async Task<AppliedStoragePlan> ReadAppliedStoragePlanAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        var applied = await JsonSerializer.DeserializeAsync<AppliedStoragePlan>(input, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("applied storage plan could not be read");
        var fileSystem = string.IsNullOrWhiteSpace(applied.FileSystem) ? "btrfs" : applied.FileSystem;
        var raidMode = string.IsNullOrWhiteSpace(applied.RaidMode)
            ? applied.DataProfile == "raid1" ? "mirror" : applied.DataProfile
            : applied.RaidMode;
        var raidBackend = string.IsNullOrWhiteSpace(applied.RaidBackend) ? RaidBackendFilesystem : applied.RaidBackend;
        return applied with { FileSystem = fileSystem, RaidMode = raidMode, RaidBackend = raidBackend };
    }

    private static async Task<string> ReadStorageApplyPassphraseAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var passphraseFile = Env.String("HOMEHARBOR_STORAGE_APPLY_PASSPHRASE_FILE", "/run/homeharbor/storage-apply.passphrase");
        _ = RootPathGuard.RequireNoSymlinkComponents(passphraseFile, "storage apply passphrase file");
        if (!string.IsNullOrWhiteSpace(passphraseFile) && File.Exists(passphraseFile))
        {
            var line = (await File.ReadAllLinesAsync(passphraseFile, cancellationToken)).FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(line))
            {
                throw new InvalidOperationException("storage apply passphrase file is empty: " + passphraseFile);
            }

            DeleteIfExists(passphraseFile);
            return line;
        }

        var result = await runner.RunAsync(
            "systemd-ask-password",
            ["--no-tty", "--timeout=300", "HomeHarbor data passphrase for storage apply:"],
            new CommandRunOptions(Timeout: TimeSpan.FromMinutes(5)),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("storage apply passphrase prompt failed");
        }

        var passphrase = result.Stdout.TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(passphrase)
            ? throw new InvalidOperationException("storage apply passphrase must not be empty")
            : passphrase;
    }

    private static async Task<string> WriteStorageApplyKeyFileAsync(string passphrase, CancellationToken cancellationToken)
    {
        var directory = Env.String("HOMEHARBOR_STORAGE_APPLY_RUNTIME_DIR", "/run/homeharbor");
        _ = RootPathGuard.CreateDirectory(directory, "storage apply runtime directory");
        var path = Path.Combine(directory, "storage-apply.key");
        _ = RootPathGuard.RequireNoSymlinkComponents(path, "storage apply key file");
        await FileWrites.AtomicWriteTextAsync(path, passphrase, 0600, cancellationToken);
        return path;
    }

    private static async Task WriteDataUnlockMetadataAsync(string unlockMode, CancellationToken cancellationToken)
    {
        var path = Env.String("HOMEHARBOR_DATA_UNLOCK_METADATA", "/var/lib/homeharbor/security/data-unlock.json");
        _ = RootPathGuard.CreateDirectory(Path.GetDirectoryName(path) ?? ".", "data unlock metadata directory");
        _ = RootPathGuard.RequireNoSymlinkComponents(path, "data unlock metadata file");
        var payload = JsonSerializer.Serialize(new
        {
            createdAt = DateTimeOffset.UtcNow,
            unlockMode
        }, JsonOptions);
        await FileWrites.AtomicWriteTextAsync(path, payload, 0640, cancellationToken);
    }

    private static async Task WriteBootUnlockEnvAsync(AppliedStoragePlan applied, CancellationToken cancellationToken)
    {
        var path = Env.String("HOMEHARBOR_DATA_BOOT_UNLOCK_ENV", "/var/lib/homeharbor/storage/boot-unlock.env");
        _ = RootPathGuard.CreateDirectory(Path.GetDirectoryName(path) ?? ".", "boot unlock directory");
        _ = RootPathGuard.RequireNoSymlinkComponents(path, "boot unlock environment file");
        var builder = new StringBuilder();
        _ = builder.Append("HOMEHARBOR_DATA_UNLOCK_MODE=").Append(applied.UnlockMode).Append('\n');
        _ = builder.Append("HOMEHARBOR_DATA_FILESYSTEM=").Append(applied.FileSystem).Append('\n');
        _ = builder.Append("HOMEHARBOR_DATA_RAID_MODE=").Append(applied.RaidMode).Append('\n');
        _ = builder.Append("HOMEHARBOR_DATA_RAID_BACKEND=").Append(applied.RaidBackend).Append('\n');
        if (applied.RaidBackend == RaidBackendMdadm)
        {
            if (string.IsNullOrWhiteSpace(applied.MdadmUuid))
            {
                throw new InvalidOperationException("mdadm-backed storage plan is missing mdadm UUID");
            }
            _ = builder.Append("HOMEHARBOR_DATA_MDADM_NAME=").Append(applied.MdadmName ?? MdadmArrayName).Append('\n');
            _ = builder.Append("HOMEHARBOR_DATA_MDADM_UUID=").Append(applied.MdadmUuid).Append('\n');
        }
        _ = builder.Append("HOMEHARBOR_DATA_DEVICE_COUNT=").Append(applied.Devices.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        _ = builder.Append("HOMEHARBOR_DATA_FILESYSTEM_UUID=").Append(applied.FileSystemUuid).Append('\n');
        for (var i = 0; i < applied.Devices.Count; i++)
        {
            var device = applied.Devices[i];
            _ = builder.Append("HOMEHARBOR_DATA_LUKS_UUID_").Append(i.ToString(CultureInfo.InvariantCulture)).Append('=').Append(device.LuksUuid).Append('\n');
            _ = builder.Append("HOMEHARBOR_DATA_MAPPER_").Append(i.ToString(CultureInfo.InvariantCulture)).Append('=').Append(device.MapperName).Append('\n');
        }

        await FileWrites.AtomicWriteTextAsync(path, builder.ToString(), 0640, cancellationToken);
    }

    private static async Task WriteDataUnlockModeEfiVariableAsync(
        ICommandRunner runner,
        string unlockMode,
        CancellationToken cancellationToken)
    {
        try
        {
            await EfiBootVariables.SetDataUnlockModeAsync(runner, unlockMode, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine("warning: failed to write HomeHarbor data unlock EFI variable: " + ex.Message);
        }
    }

    private static async Task CloseDataMountAndMappersAsync(
        ICommandRunner runner,
        IEnumerable<string> mapperNames,
        CancellationToken cancellationToken)
    {
        _ = await runner.RunAsync(ZfsTool("zpool"), ["export", "homeharbor-data"], cancellationToken: cancellationToken);
        _ = await runner.RunAsync("umount", ["/homeharbor-data"], cancellationToken: cancellationToken);
        _ = await runner.RunAsync("mdadm", ["--stop", MdadmArrayPath], cancellationToken: cancellationToken);
        foreach (var mapperName in mapperNames.Prepend("homeharbor-data").Distinct(StringComparer.Ordinal))
        {
            _ = await runner.RunAsync("cryptsetup", ["close", mapperName], cancellationToken: cancellationToken);
        }
    }

    private static async Task MountAppliedStorageAsync(
        ICommandRunner runner,
        AppliedStoragePlan applied,
        CancellationToken cancellationToken)
    {
        var source = applied.RaidBackend == RaidBackendMdadm
            ? await EnsureMdadmArrayAssembledAsync(runner, applied, cancellationToken)
            : "/dev/mapper/" + applied.Devices[0].MapperName;
        _ = Directory.CreateDirectory("/homeharbor-data");
        switch (applied.FileSystem)
        {
            case "btrfs":
                _ = (await runner.RunAsync(
                    "mount",
                    ["-t", "btrfs", "-o", "noatime,compress=zstd", source, "/homeharbor-data"],
                    cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to mount HomeHarbor data root");
                return;
            case "xfs":
                _ = (await runner.RunAsync(
                    "mount",
                    ["-t", "xfs", "-o", "noatime", source, "/homeharbor-data"],
                    cancellationToken: cancellationToken))
                    .EnsureSuccess("failed to mount HomeHarbor data root");
                return;
            case "zfs":
                await MountZfsPoolAsync(runner, cancellationToken);
                return;
            default:
                throw new InvalidOperationException("unsupported applied data file system: " + applied.FileSystem);
        }
    }

    private static async Task MountZfsPoolAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var zpool = ZfsTool("zpool");
        var zfs = ZfsTool("zfs");
        _ = await runner.RunAsync(zpool, ["import", "-N", "-f", "homeharbor-data"], cancellationToken: cancellationToken);
        _ = await runner.RunAsync(zfs, ["set", "mountpoint=/homeharbor-data", "homeharbor-data"], cancellationToken: cancellationToken);
        _ = (await runner.RunAsync(zfs, ["mount", "homeharbor-data"], cancellationToken: cancellationToken))
            .EnsureSuccess("failed to mount HomeHarbor ZFS data root");
    }

    private static async Task<bool> IsAppliedStorageMountedAsync(
        ICommandRunner runner,
        AppliedStoragePlan applied,
        CancellationToken cancellationToken)
    {
        var mountedSource = await runner.RunAsync("findmnt", ["-n", "-o", "SOURCE", "/homeharbor-data"], cancellationToken: cancellationToken);
        var source = mountedSource.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (mountedSource.ExitCode != 0 || string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (applied.FileSystem == "zfs")
        {
            var fstype = await runner.RunAsync("findmnt", ["-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data"], cancellationToken: cancellationToken);
            return fstype.ExitCode == 0 && string.Equals(fstype.Stdout.Trim(), "zfs", StringComparison.OrdinalIgnoreCase);
        }

        var uuidSource = applied.RaidBackend == RaidBackendMdadm ? MdadmArrayPath : source;
        var uuid = await runner.RunAsync("blkid", ["-s", "UUID", "-o", "value", uuidSource], cancellationToken: cancellationToken);
        return uuid.ExitCode == 0 && string.Equals(uuid.Stdout.Trim(), applied.FileSystemUuid, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> EnsureMdadmArrayAssembledAsync(
        ICommandRunner runner,
        AppliedStoragePlan applied,
        CancellationToken cancellationToken)
    {
        if (File.Exists(MdadmArrayPath))
        {
            return MdadmArrayPath;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(MdadmArrayPath) ?? "/dev/md");
        var mapperPaths = applied.Devices.Select(device => "/dev/mapper/" + device.MapperName).ToArray();
        _ = (await runner.RunAsync(
            "mdadm",
            MdadmAssembleArguments(applied.MdadmUuid, mapperPaths),
            cancellationToken: cancellationToken))
            .EnsureSuccess("failed to assemble mdadm data array");
        return MdadmArrayPath;
    }

    internal static IReadOnlyList<string> MdadmAssembleArguments(string? mdadmUuid, IReadOnlyList<string> mapperPaths)
    {
        var args = new List<string> { "--assemble", MdadmArrayPath, "--run" };
        if (!string.IsNullOrWhiteSpace(mdadmUuid))
        {
            args.Add("--uuid=" + mdadmUuid);
        }
        args.AddRange(mapperPaths);
        return args;
    }

    internal static string ZfsTool(string command)
        => command switch
        {
            "zpool" => "/usr/bin/zpool",
            "zfs" => "/usr/bin/zfs",
            _ => throw new InvalidOperationException("unsupported ZFS command: " + command)
        };

    internal static IReadOnlyList<string> MapperNames(int count)
        => Enumerable.Range(0, count)
            .Select(index => index == 0 ? "homeharbor-storage-data" : "homeharbor-storage-data-" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();

    private static string StorageDeviceSource(AppliedStorageDevice device)
    {
        var byUuid = "/dev/disk/by-uuid/" + device.LuksUuid;
        return File.Exists(byUuid) ? byUuid : device.Path;
    }

    private static async Task ReconcileSmbResultAsync(CancellationToken cancellationToken)
    {
        using var api = ApiClient();
        using var doc = JsonDocument.Parse(await api.GetStringAsync("/api/smb/reconcile/desired", cancellationToken));
        var shares = doc.RootElement.GetProperty("shares").EnumerateArray()
            .Select(item => new { id = item.GetProperty("id").GetString(), state = "applied", error = "" })
            .ToArray();
        var credentials = doc.RootElement.GetProperty("credentials").EnumerateArray()
            .Select(item => new
            {
                id = item.GetProperty("id").GetString(),
                state = item.TryGetProperty("revokedAt", out var revoked) && revoked.ValueKind != JsonValueKind.Null ? "revoked" : "applied",
                error = ""
            })
            .ToArray();
        await api.PostJsonAsync("/api/smb/reconcile/result", new { shares, credentials }, cancellationToken);
    }

    private static HomeHarborApiClient ApiClient()
    {
        var explicitUrl = Environment.GetEnvironmentVariable("HOMEHARBOR_API_URL");
        var socket = string.IsNullOrWhiteSpace(explicitUrl)
            ? Env.String("HOMEHARBOR_API_SOCKET", "/run/homeharbor-api/api.sock")
            : Env.Optional("HOMEHARBOR_API_SOCKET");
        return new HomeHarborApiClient(
            Env.String("HOMEHARBOR_API_URL", "http://homeharbor"),
            socket,
            Env.String("HOMEHARBOR_AUTOMATION_TOKEN_PATH", "/run/homeharbor/automation.jwt"));
    }

    private static async Task EnsureDirAsync(
        ICommandRunner runner,
        string path,
        int mode,
        string? owner,
        string? group,
        CancellationToken cancellationToken)
    {
        var safePath = RootPathGuard.CreateDirectory(path, "root-managed directory");
        await ChmodAsync(runner, safePath, mode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(owner) || !string.IsNullOrWhiteSpace(group))
        {
            await ChownAsync(runner, safePath, owner ?? string.Empty, group ?? string.Empty, cancellationToken);
        }
    }

    internal static async Task<bool> IsHomeHarborDataMountAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            "findmnt",
            ["-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data"],
            cancellationToken: cancellationToken);
        var fileSystem = result.Stdout.Trim();
        return result.ExitCode == 0
            && (string.Equals(fileSystem, "btrfs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileSystem, "xfs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileSystem, "zfs", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ChmodAsync(ICommandRunner runner, string path, int mode, CancellationToken cancellationToken)
        => (await runner.RunAsync("chmod", [mode.ToString("0000", CultureInfo.InvariantCulture), path], cancellationToken: cancellationToken))
            .EnsureSuccess("chmod failed");

    private static async Task NormalizeRootReadableFileAsync(ICommandRunner runner, string path, int mode, CancellationToken cancellationToken)
    {
        _ = RootPathGuard.RequireNoSymlinkComponents(path, "root-managed state file");
        await ChmodAsync(runner, path, mode, cancellationToken);
        await ChownAsync(runner, path, "root", "homeharbor", cancellationToken);
    }

    internal static void ValidateContainerReconcileIdentity(
        string id,
        string serviceName,
        string unitName,
        string quadletFile)
    {
        if (!Guid.TryParse(id, out var containerId))
        {
            throw new InvalidOperationException("container reconcile id must be a GUID");
        }

        var expectedServiceName = "homeharbor-" + containerId.ToString("N");
        if (!string.Equals(serviceName, expectedServiceName, StringComparison.Ordinal) ||
            !string.Equals(unitName, expectedServiceName + ".service", StringComparison.Ordinal) ||
            !string.Equals(quadletFile, expectedServiceName + ".container", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("container reconcile service, unit, or Quadlet identity does not match its id");
        }
    }

    internal static void ValidateSmbUnixUser(string unixUser)
    {
        const string prefix = "homeharbor-smb";
        if (!unixUser.StartsWith(prefix, StringComparison.Ordinal) ||
            unixUser.Length != prefix.Length + 3 ||
            !int.TryParse(unixUser.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number is < 1 or > 32)
        {
            throw new InvalidOperationException("SMB unixUser is outside the managed account pool: " + unixUser);
        }
    }

    internal static string BuildValidatedSmbConfig(string desiredJson, string dataRoot)
    {
        using var document = JsonDocument.Parse(desiredJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("shares", out var shareElements) || shareElements.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("credentials", out var credentialElements) || credentialElements.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("SMB desired state must contain share and credential arrays");
        }
        if (shareElements.GetArrayLength() > 256 || credentialElements.GetArrayLength() > 256)
        {
            throw new InvalidOperationException("SMB desired state exceeds managed item limits");
        }

        var familyRoot = Path.GetFullPath(Path.Combine(dataRoot, "families"));
        var shares = new Dictionary<Guid, DesiredSmbShare>();
        var shareNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in shareElements.EnumerateArray())
        {
            if (JsonBoolean(item, "enabled") != true)
            {
                continue;
            }

            var id = RequireGuid(item, "id", "SMB share id");
            var familyId = RequireGuid(item, "familyId", "SMB share familyId");
            var shareName = JsonString(item, "shareName") ?? string.Empty;
            if (!IsSafeSmbShareName(shareName) || !shareNames.Add(shareName))
            {
                throw new InvalidOperationException("SMB share name is invalid or duplicated: " + shareName);
            }

            var name = JsonString(item, "name") ?? string.Empty;
            if (name.Length > 128 || name.Any(char.IsControl))
            {
                throw new InvalidOperationException("SMB share display name is invalid: " + shareName);
            }

            var expectedPath = Path.GetFullPath(Path.Combine(familyRoot, familyId.ToString("N")));
            var path = JsonString(item, "path") ?? string.Empty;
            if (!string.Equals(Path.GetFullPath(path), expectedPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "SMB share path does not match its family: " + shareName +
                    " (expected " + expectedPath + ", received " + Path.GetFullPath(path) + ")");
            }
            RequireDirectoryWithoutSymlinks(expectedPath, "SMB share path");
            if (!shares.TryAdd(id, new DesiredSmbShare(
                id,
                familyId,
                name,
                shareName,
                expectedPath,
                JsonBoolean(item, "readOnly") == true)))
            {
                throw new InvalidOperationException("SMB share id is duplicated: " + id);
            }
        }

        var credentials = new List<DesiredSmbCredential>();
        var credentialIds = new HashSet<Guid>();
        var unixUsers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in credentialElements.EnumerateArray())
        {
            var revoked = item.TryGetProperty("revokedAt", out var revokedAt) && revokedAt.ValueKind != JsonValueKind.Null;
            if (JsonBoolean(item, "enabled") != true || revoked)
            {
                continue;
            }

            var id = RequireGuid(item, "id", "SMB credential id");
            var familyId = RequireGuid(item, "familyId", "SMB credential familyId");
            var shareId = RequireGuid(item, "shareId", "SMB credential shareId");
            if (!credentialIds.Add(id))
            {
                throw new InvalidOperationException("SMB credential id is duplicated: " + id);
            }
            if (!shares.TryGetValue(shareId, out var share) || share.FamilyId != familyId)
            {
                throw new InvalidOperationException("SMB credential does not belong to its declared family share: " + id);
            }

            var unixUser = JsonString(item, "unixUser") ?? string.Empty;
            ValidateSmbUnixUser(unixUser);
            if (!unixUsers.Add(unixUser))
            {
                throw new InvalidOperationException("SMB unixUser is assigned more than once: " + unixUser);
            }
            credentials.Add(new DesiredSmbCredential(
                id,
                familyId,
                shareId,
                unixUser,
                JsonBoolean(item, "readOnly") == true));
        }

        var builder = new StringBuilder(DefaultSmbConf());
        foreach (var share in shares.Values.OrderBy(value => value.ShareName, StringComparer.Ordinal))
        {
            var users = credentials
                .Where(credential => credential.ShareId == share.Id)
                .OrderBy(credential => credential.UnixUser, StringComparer.Ordinal)
                .ToArray();
            _ = builder.AppendLine("[" + share.ShareName + "]");
            _ = builder.AppendLine("   comment = " + share.Name);
            _ = builder.AppendLine("   path = " + share.Path);
            _ = builder.AppendLine("   browseable = yes");
            _ = builder.AppendLine("   guest ok = no");
            _ = builder.AppendLine("   read only = " + (share.ReadOnly ? "yes" : "no"));
            _ = builder.AppendLine("   force user = homeharbor");
            _ = builder.AppendLine("   force group = homeharbor");
            _ = builder.AppendLine("   create mask = 0660");
            _ = builder.AppendLine("   directory mask = 0770");
            _ = builder.AppendLine("   valid users = " + (users.Length == 0 ? "nobody" : string.Join(' ', users.Select(user => user.UnixUser))));
            var readUsers = users.Where(user => share.ReadOnly || user.ReadOnly).Select(user => user.UnixUser).ToArray();
            var writeUsers = share.ReadOnly ? [] : users.Where(user => !user.ReadOnly).Select(user => user.UnixUser).ToArray();
            if (readUsers.Length > 0) _ = builder.AppendLine("   read list = " + string.Join(' ', readUsers));
            if (writeUsers.Length > 0) _ = builder.AppendLine("   write list = " + string.Join(' ', writeUsers));
            _ = builder.AppendLine();
        }

        return builder.ToString();
    }

    private static bool IsSafeSmbShareName(string value)
        => value.Length is >= 1 and <= 64 &&
           value is not ("ipc$" or "admin$") &&
           value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static Guid RequireGuid(JsonElement item, string property, string label)
        => Guid.TryParse(JsonString(item, property), out var value) && value != Guid.Empty
            ? value
            : throw new InvalidOperationException(label + " must be a non-empty GUID");

    private static void RequireDirectoryWithoutSymlinks(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException(label + " does not exist: " + fullPath);
        }

        var current = Path.GetPathRoot(fullPath) ?? Path.DirectorySeparatorChar.ToString();
        foreach (var segment in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(label + " contains a symbolic link: " + current);
            }
        }
    }

    private static string CheckedChildPath(string root, string fileName, string label)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(label + " must be a single file name");
        }

        var rootFull = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(rootFull, fileName));
        if (!SecurityGuards.IsInsideDirectory(path, rootFull) || string.Equals(path, rootFull, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(label + " escapes its managed directory");
        }

        RefuseReadOnlyRootfsPath(path, label);
        return path;
    }

    internal static async Task EnsureContainerUserManagerAsync(
        ICommandRunner runner,
        string uid,
        CancellationToken cancellationToken)
    {
        if (!uint.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException("container user id is not numeric: " + uid);
        }

        _ = (await runner.RunAsync("systemctl", ["start", "user@" + uid + ".service"], cancellationToken: cancellationToken))
            .EnsureSuccess("failed to start rootless container user manager");
        var bus = "/run/user/" + uid + "/bus";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if ((await runner.RunAsync("test", ["-S", bus], cancellationToken: cancellationToken)).ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new InvalidOperationException("rootless container user bus did not become ready: " + bus);
    }

    private static async Task ChownAsync(ICommandRunner runner, string path, string owner, string group, CancellationToken cancellationToken)
    {
        var spec = string.IsNullOrEmpty(group) ? owner : owner + ":" + group;
        if (string.IsNullOrEmpty(spec) || spec == ":")
        {
            return;
        }

        _ = (await runner.RunAsync("chown", [spec, path], cancellationToken: cancellationToken))
            .EnsureSuccess("chown failed");
    }

    private static async Task SystemctlUserAsync(
        ICommandRunner runner,
        string user,
        string uid,
        bool dryRun,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        bool ignoreFailure = false)
    {
        if (dryRun)
        {
            Console.WriteLine("dry-run systemctl --user " + string.Join(' ', args));
            return;
        }

        var result = await runner.RunAsync(
            "runuser",
            ["-u", user, "--", "env", "XDG_RUNTIME_DIR=/run/user/" + uid, "DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/" + uid + "/bus", "systemctl", "--user", .. args],
            cancellationToken: cancellationToken);
        if (!ignoreFailure)
        {
            _ = result.EnsureSuccess("systemctl --user failed");
        }
    }

    internal static string DefaultCaddyfile()
        => """
            {
                auto_https disable_redirects
                admin unix//run/caddy/admin.sock
            }

            """.Replace("            ", string.Empty, StringComparison.Ordinal) +
            CaddyTrustConfiguration.HttpSiteBlock() + Environment.NewLine + Environment.NewLine +
            """
            homeharbor.local {
                tls internal
                reverse_proxy unix//run/homeharbor-api/api.sock {
                    header_up Host {host}
                    header_up X-Forwarded-For {remote_host}
                    header_up X-Forwarded-Proto {scheme}
                }
            }
            """.Replace("            ", string.Empty, StringComparison.Ordinal);

    private static string DefaultSmbConf()
        => """
            [global]
               server role = standalone server
               workgroup = WORKGROUP
               netbios name = HOMEHARBOR
               security = user
               map to guest = never
               server min protocol = SMB3_00
               server signing = mandatory
               smb encrypt = required
               ntlm auth = ntlmv2-only
               passdb backend = tdbsam
               private dir = /var/lib/homeharbor/samba/private
               state directory = /var/lib/homeharbor/samba/state
               cache directory = /var/lib/homeharbor/samba/cache
               lock directory = /var/lib/homeharbor/samba/lock
               log file = /var/log/samba/homeharbor-%m.log
               max log size = 1000
               disable spoolss = yes
               load printers = no
               printing = bsd
               dns proxy = no
               smb ports = 445
               follow symlinks = no
               wide links = no
               unix extensions = no

            """.Replace("            ", string.Empty, StringComparison.Ordinal);

    private static void RefuseReadOnlyRootfsPath(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath == "/etc" || fullPath.StartsWith("/etc/", StringComparison.Ordinal) ||
            fullPath == "/usr" || fullPath.StartsWith("/usr/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"refusing to write {label} under read-only rootfs path {fullPath}");
        }
    }

    private static string? JsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;

    private static long? JsonInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => null
        };
    }

    private static bool? JsonBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static void DeleteIfExists(string path)
    {
        var safePath = RootPathGuard.RequireNoSymlinkComponents(path, "root-managed file path");
        if (File.Exists(safePath))
        {
            File.Delete(safePath);
        }
    }

    private static string GetBoot(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string EntryIdForSlot(string slot)
        => "homeharbor-" + slot.ToLowerInvariant() + ".conf";

    private static void ValidateSqlIdentifier(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException($"PostgreSQL {label} must contain only letters, numbers, or underscore.");
        }
    }

    private static string SqlString(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SqlIdentifier(string value)
        => value.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static async Task WriteStorageStatusAsync(
        string path,
        string state,
        int progress,
        string message,
        string? error,
        string? planId,
        CancellationToken cancellationToken)
    {
        var status = new
        {
            state,
            progress,
            message,
            error,
            planId,
            updatedAt = DateTimeOffset.UtcNow
        };
        await FileWrites.AtomicWriteTextAsync(path, JsonSerializer.Serialize(status, JsonOptions), 0644, cancellationToken);
    }

    private static async Task<string?> ReadStorageStatusPlanIdAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var input = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken);
            return JsonString(doc.RootElement, "planId");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static Task<IReadOnlySet<string>> RootAncestorDevicesAsync(ICommandRunner runner, CancellationToken cancellationToken)
        => BlockDeviceSafety.RootAncestorDevicesAsync(runner, cancellationToken);

    private static async Task<bool> DeviceExistsAsync(ICommandRunner runner, string path, bool allowFiles, CancellationToken cancellationToken)
    {
        var block = await runner.RunAsync("test", ["-b", path], cancellationToken: cancellationToken);
        return block.ExitCode == 0 || (allowFiles && File.Exists(path));
    }

    internal static async Task<string> ValidateStorageTargetIdentityAsync(
        ICommandRunner runner,
        PendingStorageTarget target,
        bool allowFiles,
        CancellationToken cancellationToken)
    {
        if (target.Kind is not ("whole-disk" or "main-reserved"))
        {
            throw new InvalidOperationException("unsupported storage target kind: " + target.Kind);
        }
        if (!target.Path.StartsWith("/dev/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage plan device is not an absolute /dev path: " + target.Path);
        }
        if (target.SizeBytes <= 0)
        {
            throw new InvalidOperationException("storage plan device is missing a valid planned size: " + target.Path);
        }

        var expectedSerial = NormalizeStorageIdentity(target.Serial);
        var expectedStableId = NormalizeStorageIdentity(target.StableId);
        if (target.Kind == "main-reserved" && expectedStableId is null)
        {
            throw new InvalidOperationException("main reserved storage target is missing its planned PARTUUID: " + target.Path);
        }
        if (target.Kind == "whole-disk" && expectedSerial is null && expectedStableId is null &&
            !target.Path.StartsWith("/dev/disk/by-id/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("whole-disk storage target has no stable serial, WWN, or by-id path: " + target.Path);
        }
        if (!await DeviceExistsAsync(runner, target.Path, allowFiles, cancellationToken))
        {
            throw new InvalidOperationException("storage plan device does not exist: " + target.Path);
        }

        var resolve = await runner.RunAsync("readlink", ["-f", target.Path], cancellationToken: cancellationToken);
        var resolvedPath = resolve.Stdout.Trim();
        if (resolve.ExitCode != 0 || !resolvedPath.StartsWith("/dev/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage plan device could not be resolved safely: " + target.Path);
        }
        if (target.ResolvedPath is not null && !string.Equals(target.ResolvedPath, resolvedPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage target path resolved to a different device after validation: " + target.Path);
        }

        var identity = await runner.RunAsync(
            "lsblk",
            ["--json", "--bytes", "--nodeps", "--output", "PATH,SIZE,TYPE,MODEL,SERIAL,WWN,TRAN,PARTUUID", resolvedPath],
            cancellationToken: cancellationToken);
        if (identity.ExitCode != 0)
        {
            throw new InvalidOperationException("failed to read current storage target identity: " + target.Path);
        }

        JsonElement actual;
        try
        {
            using var document = JsonDocument.Parse(identity.Stdout);
            if (!document.RootElement.TryGetProperty("blockdevices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array ||
                devices.GetArrayLength() != 1)
            {
                throw new InvalidOperationException("lsblk did not return exactly one storage target: " + target.Path);
            }

            actual = devices[0].Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("lsblk returned invalid storage target identity JSON: " + target.Path, ex);
        }

        var actualPath = NormalizeStorageIdentity(JsonString(actual, "path"));
        var actualType = NormalizeStorageIdentity(JsonString(actual, "type"));
        var expectedType = target.Kind == "whole-disk" ? "disk" : "part";
        if (!string.Equals(actualPath, resolvedPath, StringComparison.Ordinal) ||
            !string.Equals(actualType, expectedType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage target path or device type changed after planning: " + target.Path);
        }

        var actualSize = JsonInt64(actual, "size") ?? 0;
        if (actualSize != target.SizeBytes)
        {
            throw new InvalidOperationException(
                $"storage target size changed after planning: {target.Path} (planned {target.SizeBytes}, current {actualSize})");
        }

        RequireStorageIdentityMatch("model", target.Path, target.Model, JsonString(actual, "model"));
        RequireStorageIdentityMatch("transport", target.Path, target.Transport, JsonString(actual, "tran"));
        RequireStorageIdentityMatch("serial", target.Path, expectedSerial, JsonString(actual, "serial"));
        RequireStorageIdentityMatch(
            target.Kind == "main-reserved" ? "PARTUUID" : "WWN",
            target.Path,
            expectedStableId,
            JsonString(actual, target.Kind == "main-reserved" ? "partuuid" : "wwn"));
        return resolvedPath;
    }

    private static void RequireStorageIdentityMatch(string label, string path, string? expected, string? actual)
    {
        expected = NormalizeStorageIdentity(expected);
        if (expected is not null && !string.Equals(expected, NormalizeStorageIdentity(actual), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("storage target " + label + " changed after planning: " + path);
        }
    }

    private static string? NormalizeStorageIdentity(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<bool> DeviceHasMountsAsync(ICommandRunner runner, string path, CancellationToken cancellationToken)
        => await BlockDeviceSafety.DeviceHasMountsAsync(runner, path, cancellationToken);

    private static async Task<bool> DeviceHasProtectedLabelAsync(ICommandRunner runner, string path, CancellationToken cancellationToken)
    {
        var labels = await DeviceLabelsAsync(runner, path, cancellationToken);
        return labels.Any(label => label is
            "esp" or
            "boot_a" or
            "boot_b" or
            "super" or
            "state" or
            "recovery_a" or
            "recovery_b" or
            "vbmeta_a" or
            "vbmeta_b" or
            "data");
    }

    private static async Task<bool> DeviceHasLabelAsync(
        ICommandRunner runner,
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        var labels = await DeviceLabelsAsync(runner, path, cancellationToken);
        return labels.Any(label => string.Equals(label, expected, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<string>> DeviceLabelsAsync(
        ICommandRunner runner,
        string path,
        CancellationToken cancellationToken)
        => await BlockDeviceSafety.DeviceLabelsAsync(runner, path, cancellationToken);

}
