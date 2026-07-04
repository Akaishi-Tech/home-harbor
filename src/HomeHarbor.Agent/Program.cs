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
            _ => "/run/homeharbor/api.sock"
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
              postgres-init
              postgres-bootstrap
              ensure-caddy-config
              render-caddyfile
              storage-health
              ensure-smb-config
              apply-smb
              apply-containers
              apply-system-apps
              boot-attempt [--state-dir PATH] [--esp PATH] [--window-seconds N] [--threshold N] [--now UNIX_SECONDS] [--dry-run]
              boot-success [--state-dir PATH] [--timeout-seconds N] [--health-url PATH_OR_URL] [--api-url URL] [--api-socket PATH|--no-api-socket] [--dry-run]
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
        await EnsureDirAsync(runner, "/var/lib/homeharbor", 0750, "homeharbor", "homeharbor", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/homeharbor/ota", 0750, "homeharbor", "homeharbor", cancellationToken);
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
        await EnsureDirAsync(runner, "/var/lib/caddy/homeharbor", 0750, "caddy", "caddy", cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/NetworkManager", 0755, null, null, cancellationToken);
        await EnsureDirAsync(runner, "/var/lib/containers", 0755, null, null, cancellationToken);
        await EnsureDirAsync(runner, "/run/homeharbor", 0750, "homeharbor", "homeharbor", cancellationToken);
        await EnsureDirAsync(runner, "/run/homeharbor/smb-credentials", 0700, "homeharbor", "homeharbor", cancellationToken);

        if ((await runner.RunAsync("id", ["-u", "homeharbor-containers"], cancellationToken: cancellationToken)).ExitCode == 0)
        {
            await EnsureDirAsync(runner, "/var/lib/homeharbor-containers", 0750, "homeharbor-containers", "homeharbor-containers", cancellationToken);
            await EnsureDirAsync(runner, "/var/lib/homeharbor-containers/.config/containers/systemd", 0750, "homeharbor-containers", "homeharbor-containers", cancellationToken);
            await EnsureDirAsync(runner, "/var/lib/systemd/linger", 0755, null, null, cancellationToken);
            File.WriteAllText("/var/lib/systemd/linger/homeharbor-containers", string.Empty);
        }

        if (await IsHomeHarborDataMountAsync(runner, cancellationToken))
        {
            await EnsureDirAsync(runner, "/homeharbor-data", 0711, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/apps", 0750, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/families", 0750, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps", 0750, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps/active", 0750, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps/staged", 0750, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/system-apps/versions", 0750, "homeharbor", "homeharbor", cancellationToken);
        }
        else
        {
            Console.WriteLine("homeharbor-data is not mounted; skipping data directory normalization.");
        }

        return 0;
    }

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
        if (File.Exists(path))
        {
            _ = ReleaseChannel.Require((await File.ReadAllLinesAsync(path, cancellationToken)).FirstOrDefault(), "OTA channel file");
            await NormalizeHomeHarborFileAsync(runner, path, 0640, cancellationToken);
            return;
        }

        var channel = ReleaseChannel.Require(defaultChannel, "HOMEHARBOR_CHANNEL");
        await FileWrites.AtomicWriteTextAsync(path, channel + Environment.NewLine, 0640, cancellationToken);
        await NormalizeHomeHarborFileAsync(runner, path, 0640, cancellationToken);
    }

    internal static async Task EnsureKernelChannelFileAsync(
        ICommandRunner runner,
        string path,
        string defaultChannel,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            _ = KernelChannel.Require((await File.ReadAllLinesAsync(path, cancellationToken)).FirstOrDefault(), "kernel channel file");
            await NormalizeHomeHarborFileAsync(runner, path, 0640, cancellationToken);
            return;
        }

        var channel = KernelChannel.Require(defaultChannel, "HOMEHARBOR_KERNEL_CHANNEL");
        await FileWrites.AtomicWriteTextAsync(path, channel + Environment.NewLine, 0640, cancellationToken);
        await NormalizeHomeHarborFileAsync(runner, path, 0640, cancellationToken);
    }

    private static async Task<int> PostgresInitAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var dataDir = Env.String("HOMEHARBOR_POSTGRES_DATA_DIR", "/homeharbor-data/postgresql/data");
        var parent = Path.GetDirectoryName(dataDir) ?? "/homeharbor-data/postgresql";
        await EnsureDirAsync(runner, parent, 0700, "postgres", "postgres", cancellationToken);
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
        var caddyState = Env.String("HOMEHARBOR_CADDY_STATE_DIR", "/var/lib/caddy/homeharbor");
        var caddyfile = Env.String("HOMEHARBOR_CADDYFILE", Path.Combine(caddyState, "Caddyfile"));
        await EnsureDirAsync(runner, caddyState, 0750, "caddy", "caddy", cancellationToken);
        if (File.Exists(caddyfile) && new FileInfo(caddyfile).Length > 0)
        {
            return 0;
        }

        await FileWrites.AtomicWriteTextAsync(caddyfile, DefaultCaddyfile(), 0640, cancellationToken);
        await ChownAsync(runner, caddyfile, "caddy", "caddy", cancellationToken);
        return 0;
    }

    private static async Task<int> RenderCaddyfileAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var caddyState = Env.String("HOMEHARBOR_CADDY_STATE_DIR", "/var/lib/caddy/homeharbor");
        var caddyfile = Env.String("HOMEHARBOR_CADDYFILE", Path.Combine(caddyState, "Caddyfile"));
        await EnsureDirAsync(runner, caddyState, 0750, "caddy", "caddy", cancellationToken);
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

        await ChownAsync(runner, temp, "caddy", "caddy", cancellationToken);
        await ChmodAsync(runner, temp, 0640, cancellationToken);
        File.Move(temp, caddyfile, overwrite: true);
        var reload = await runner.RunAsync("caddy", ["reload", "--config", caddyfile, "--force"], cancellationToken: cancellationToken);
        if (reload.ExitCode != 0)
        {
            var systemReload = await runner.RunAsync("systemctl", ["reload", "caddy.service"], cancellationToken: cancellationToken);
            if (systemReload.ExitCode != 0)
            {
                _ = (await runner.RunAsync("systemctl", ["restart", "caddy.service"], cancellationToken: cancellationToken)).EnsureSuccess("failed to reload caddy");
            }
        }

        return 0;
    }

    private static async Task<int> StorageHealthAsync(CancellationToken cancellationToken)
    {
        await ApiClient().PostJsonAsync("/api/storage/health/check", new { }, cancellationToken);
        return 0;
    }

    private static async Task<int> EnsureSmbConfigAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var state = Env.String("HOMEHARBOR_SMB_STATE_DIR", "/var/lib/homeharbor/samba");
        var conf = Env.String("HOMEHARBOR_SMB_CONF", Path.Combine(state, "smb.conf"));
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
        var conf = Env.String("HOMEHARBOR_SMB_CONF", Path.Combine(state, "smb.conf"));
        var credentialDir = Env.String("HOMEHARBOR_SMB_CREDENTIAL_DIR", "/run/homeharbor/smb-credentials");
        var dryRun = Env.Flag("HOMEHARBOR_DRY_RUN");
        if (!dryRun)
        {
            foreach (var dir in new[] { state, Path.Combine(state, "private"), Path.Combine(state, "state"), Path.Combine(state, "cache"), Path.Combine(state, "lock") })
            {
                await EnsureDirAsync(runner, dir, 0750, null, null, cancellationToken);
            }

            await EnsureDirAsync(runner, credentialDir, 0700, null, null, cancellationToken);
            await EnsureDirAsync(runner, "/var/log/samba", 0755, null, null, cancellationToken);
        }

        var configFile = Env.Optional("HOMEHARBOR_SMB_CONFIG_FILE");
        if (dryRun)
        {
            var config = !string.IsNullOrWhiteSpace(configFile)
                ? await File.ReadAllTextAsync(configFile, cancellationToken)
                : await ApiClient().GetStringAsync("/api/smb/config/smb.conf", cancellationToken);
            if (config.Length == 0)
            {
                throw new InvalidOperationException("SMB config was empty");
            }

            Console.WriteLine($"dry-run smb config {conf}");
        }
        else
        {
            var temp = Path.Combine(state, "smb.conf." + Guid.NewGuid().ToString("N"));
            try
            {
                if (!string.IsNullOrWhiteSpace(configFile))
                {
                    File.Copy(configFile, temp, overwrite: true);
                }
                else
                {
                    await ApiClient().DownloadAsync("/api/smb/config/smb.conf", temp, cancellationToken);
                }

                if (new FileInfo(temp).Length == 0)
                {
                    throw new InvalidOperationException("SMB config was empty");
                }

                RefuseReadOnlyRootfsPath(conf, "smb.conf");
                File.Move(temp, conf, overwrite: true);
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
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(credentialFile, cancellationToken));
            var root = doc.RootElement;
            var action = JsonString(root, "action") ?? "upsert";
            var unixUser = JsonString(root, "unixUser") ?? JsonString(root, "username") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(unixUser))
            {
                throw new InvalidOperationException("missing unixUser in " + credentialFile);
            }

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
            _ = await runner.RunAsync("systemctl", ["restart", "homeharbor-smbd.service", "homeharbor-nmbd.service"], cancellationToken: cancellationToken);
            await ReconcileSmbResultAsync(cancellationToken);
        }

        return 0;
    }

    private static async Task<int> ApplyContainersAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var user = Env.String("HOMEHARBOR_CONTAINER_USER", "homeharbor-containers");
        var home = Env.String("HOMEHARBOR_CONTAINER_HOME", "/var/lib/homeharbor-containers");
        var quadletDir = Env.String("HOMEHARBOR_QUADLET_DIR", Path.Combine(home, ".config/containers/systemd"));
        var dryRun = Env.Flag("HOMEHARBOR_DRY_RUN");
        RefuseReadOnlyRootfsPath(quadletDir, "Quadlet directory");

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
            await EnsureDirAsync(runner, home, 0750, uid, gid, cancellationToken);
        }

        if (!dryRun)
        {
            await EnsureDirAsync(runner, quadletDir, 0750, null, null, cancellationToken);
            _ = await runner.RunAsync("chown", ["-R", uid + ":" + gid, home], cancellationToken: cancellationToken);
            await EnsureDirAsync(runner, "/run/user/" + uid, 0700, uid, gid, cancellationToken);
        }

        var desiredOverride = Env.Optional("HOMEHARBOR_CONTAINER_DESIRED_FILE");
        var desiredJson = !string.IsNullOrWhiteSpace(desiredOverride)
            ? await File.ReadAllTextAsync(desiredOverride, cancellationToken)
            : await ApiClient().GetStringAsync("/api/containers/reconcile/desired", cancellationToken);
        using var doc = JsonDocument.Parse(desiredJson);
        var results = new List<object>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = JsonString(item, "id") ?? string.Empty;
            var serviceName = JsonString(item, "serviceName") ?? string.Empty;
            var unitName = JsonString(item, "unitName") ?? string.Empty;
            var quadletFile = JsonString(item, "quadletFile") ?? string.Empty;
            var desiredState = JsonString(item, "desiredState") ?? string.Empty;
            var requestedAction = JsonString(item, "requestedAction") ?? "none";
            var target = Path.Combine(quadletDir, quadletFile);
            RefuseReadOnlyRootfsPath(target, "Quadlet file");
            var runtimeState = desiredState;
            var error = string.Empty;

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
                var quadlet = JsonString(item, "quadlet") ?? string.Empty;
                if (!dryRun)
                {
                    await FileWrites.AtomicWriteTextAsync(target, quadlet, 0640, cancellationToken);
                    await ChownAsync(runner, target, uid, gid, cancellationToken);
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
                case "none":
                case "delete":
                    if (desiredState == "running" && requestedAction == "reload")
                    {
                        await SystemctlUserAsync(runner, user, uid, dryRun, ["restart", unitName], cancellationToken);
                        runtimeState = "running";
                    }
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

    private static async Task<int> ApplySystemAppsAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var root = Env.String("HOMEHARBOR_SYSTEM_APPS_ROOT", "/homeharbor-data/system-apps");
        var activeRoot = Path.Combine(root, "active");
        var stagedRoot = Path.Combine(root, "staged");
        var versionsRoot = Path.Combine(root, "versions");
        var wrapperRoot = Env.String("HOMEHARBOR_SYSTEM_APP_WRAPPER_DIR", "/run/homeharbor/system-apps/bin");
        var publicKey = Env.String("HOMEHARBOR_RELEASE_PUBLIC_KEY", "/etc/homeharbor/release.pub.pem");
        RefuseReadOnlyRootfsPath(root, "system apps root");
        RefuseReadOnlyRootfsPath(wrapperRoot, "system app wrapper directory");

        _ = Directory.CreateDirectory(activeRoot);
        _ = Directory.CreateDirectory(stagedRoot);
        _ = Directory.CreateDirectory(versionsRoot);
        _ = Directory.CreateDirectory(wrapperRoot);

        using var api = ApiClient();
        using var doc = JsonDocument.Parse(await api.GetStringAsync("/api/apps/reconcile/desired", cancellationToken));
        var results = new List<object>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = JsonString(item, "id") ?? string.Empty;
            var appKey = JsonString(item, "appKey") ?? string.Empty;
            var desiredState = JsonString(item, "desiredState") ?? string.Empty;
            var manifestUrl = JsonString(item, "manifestUrl") ?? string.Empty;
            IReadOnlyList<string> commands = [];
            SystemAppHotCheck? hotCheck = null;
            var runtimeState = "unknown";
            var error = string.Empty;
            var installedVersion = JsonString(item, "installedVersion") ?? string.Empty;
            var activeVersion = JsonString(item, "activeVersion") ?? string.Empty;
            var requiresReboot = false;

            try
            {
                ValidateSystemAppKey(appKey);
                commands = SystemAppCommands(appKey, JsonStringArray(item, "commands"));
                hotCheck = ReadSystemAppHotCheck(item);
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
                    if (string.IsNullOrWhiteSpace(manifestUrl))
                    {
                        throw new InvalidOperationException("system app manifest URL is missing");
                    }

                    var applied = await InstallSystemAppPayloadAsync(
                        runner,
                        appKey,
                        manifestUrl,
                        publicKey,
                        stagedRoot,
                        versionsRoot,
                        activeRoot,
                        wrapperRoot,
                        commands,
                        hotCheck,
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
        ICommandRunner runner,
        string appKey,
        string manifestUrl,
        string publicKey,
        string stagedRoot,
        string versionsRoot,
        string activeRoot,
        string wrapperRoot,
        IReadOnlyList<string> commands,
        SystemAppHotCheck? hotCheck,
        CancellationToken cancellationToken)
    {
        var work = Path.Combine(stagedRoot, appKey + "-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var manifestPath = Path.Combine(work, "manifest.json");
            var payloadPath = Path.Combine(work, "payload.tar.gz");
            await DownloadUriAsync(manifestUrl, manifestPath, cancellationToken);
            var manifest = await new SystemAppPackageManifestVerifier().VerifyAsync(manifestPath, publicKey, cancellationToken);
            if (!string.Equals(manifest.AppKey, appKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"system app manifest appKey {manifest.AppKey} does not match requested app {appKey}");
            }

            await DownloadUriAsync(manifest.PayloadUrl, payloadPath, cancellationToken);
            var actualPayloadSha = await Sha256FileAsync(payloadPath, cancellationToken);
            if (!string.Equals(actualPayloadSha, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"system app payload hash mismatch: expected {manifest.PayloadSha256}, actual {actualPayloadSha}");
            }

            var versionDir = Path.Combine(versionsRoot, appKey + "-" + SafeVersion(manifest.Version) + "-" + actualPayloadSha[..12]);
            if (!Directory.Exists(versionDir))
            {
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
            }
            ActivateSystemApp(appKey, versionDir, activeRoot);
            WriteSystemAppWrappers(appKey, wrapperRoot, activeRoot, commands);
            var hot = await ValidateSystemAppHotActivationAsync(runner, commands, hotCheck, wrapperRoot, cancellationToken);
            return hot
                ? new SystemAppApplyState(manifest.Version, "active-hot", RequiresReboot: true, Error: string.Empty)
                : new SystemAppApplyState(manifest.Version, "active-pending-reboot", RequiresReboot: true, Error: "hot activation check did not pass; reboot required");
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private static async Task DownloadUriAsync(string uriText, string destination, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("system app download URL must be absolute: " + uriText);
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        if (uri.Scheme == Uri.UriSchemeFile)
        {
            await FileWrites.CopyFileAsync(uri.LocalPath, destination, 0640, cancellationToken);
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("system app download URL scheme is not allowed: " + uri.Scheme);
        }

        using var http = new HttpClient();
        await using var input = await http.GetStreamAsync(uri, cancellationToken);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
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

    private static async Task<bool> ValidateSystemAppHotActivationAsync(
        ICommandRunner runner,
        IReadOnlyList<string> commands,
        SystemAppHotCheck? hotCheck,
        string wrapperRoot,
        CancellationToken cancellationToken)
    {
        if (hotCheck is null)
        {
            return false;
        }

        if (!commands.Contains(hotCheck.Command, StringComparer.Ordinal))
        {
            return false;
        }

        var command = Path.Combine(wrapperRoot, hotCheck.Command);
        if (!File.Exists(command))
        {
            return false;
        }

        var result = await runner.RunAsync(command, hotCheck.Args, cancellationToken: cancellationToken);
        return result.ExitCode == 0;
    }

    private static string SafeVersion(string version)
    {
        var safe = new string([.. version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-')]);
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static void ValidateSystemAppKey(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey) || appKey.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
        {
            throw new InvalidOperationException("system app key is invalid: " + appKey);
        }
    }

    private static void DeletePath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, recursive: true);
                return;
            }

            File.Delete(path);
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
                DeleteIfExists(Path.Combine(options.StateDir, "attempts"));
                DeleteIfExists(Path.Combine(options.StateDir, "recovery-requested-at"));
                await FileWrites.AtomicWriteTextAsync(Path.Combine(options.StateDir, "last-success-at"), DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\n", cancellationToken: cancellationToken);
                await EfiBootVariables.ClearOneShotAsync(runner, cancellationToken);
                if (!options.DryRun)
                {
                    _ = await runner.RunAsync("systemctl", ["start", "systemd-bless-boot.service"], cancellationToken: cancellationToken);
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
        var pendingPlan = Path.Combine(stateDir, "pending-plan.json");
        var statusFile = Path.Combine(stateDir, "status.json");
        var appliedPlan = Path.Combine(stateDir, "applied-plan.json");
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
                JsonString(device, "kind") ?? "whole-disk"))
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
        var rootParent = await RootParentDeviceAsync(runner, cancellationToken);
        foreach (var target in plannedTargets)
        {
            var device = target.Path;
            if (!device.StartsWith("/dev/", StringComparison.Ordinal)) await Fail("storage plan device is not an absolute /dev path: " + device, planId);
            if (!await DeviceExistsAsync(runner, device, allowFiles, cancellationToken)) await Fail("storage plan device does not exist: " + device, planId);
            var deviceReal = (await runner.RunAsync("readlink", ["-f", device], cancellationToken: cancellationToken)).Stdout.Trim();
            if (!string.IsNullOrWhiteSpace(rootParent) && deviceReal == rootParent) await Fail("refusing to use the currently booted system disk: " + device, planId);
            if (await DeviceHasProtectedLabelAsync(runner, device, cancellationToken)) await Fail("refusing to use HomeHarbor protected disk/partition: " + device, planId);
            if (target.Kind == "main-reserved" && !await DeviceHasLabelAsync(runner, device, "data-candidate", cancellationToken))
            {
                await Fail("main reserved storage target must be the data-candidate partition: " + device, planId);
            }
            if (await DeviceHasMountsAsync(runner, device, cancellationToken)) await Fail("refusing to use a disk with mounted filesystems: " + device, planId);
        }

        try
        {
            await ApplyStoragePlanAsync(
                runner,
                statusFile,
                pendingPlan,
                appliedPlan,
                planId,
                plannedTargets,
                unlockMode,
                fileSystem,
                raidMode,
                raidBackend,
                dataProfile,
                metadataProfile,
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
        bool dryRun,
        CancellationToken cancellationToken)
    {
        await WriteStorageStatusAsync(statusFile, "Running", 20, "Preparing data unlock.", null, planId, cancellationToken);

        var devices = targets.Select(target => target.Path).ToArray();
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
                var device = devices[i];
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
            await EnsureDirAsync(runner, "/homeharbor-data", 0711, "homeharbor", "homeharbor", cancellationToken);
            await EnsureDirAsync(runner, "/homeharbor-data/apps", 0750, "homeharbor", "homeharbor", cancellationToken);
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
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage-apply.key");
        await FileWrites.AtomicWriteTextAsync(path, passphrase, 0600, cancellationToken);
        return path;
    }

    private static async Task WriteDataUnlockMetadataAsync(string unlockMode, CancellationToken cancellationToken)
    {
        var path = Env.String("HOMEHARBOR_DATA_UNLOCK_METADATA", "/var/lib/homeharbor/security/data-unlock.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
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
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
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

    private static string ZfsTool(string command)
    {
        var root = Env.String("HOMEHARBOR_STORAGE_ZFS_TOOL_DIR", "/var/lib/homeharbor/storage/oobe-tools/bin");
        var path = Path.Combine(root, command);
        return File.Exists(path) ? path : command;
    }

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
        try
        {
            var api = ApiClient();
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
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
        }
    }

    private static HomeHarborApiClient ApiClient()
    {
        var explicitUrl = Environment.GetEnvironmentVariable("HOMEHARBOR_API_URL");
        var socket = string.IsNullOrWhiteSpace(explicitUrl)
            ? Env.String("HOMEHARBOR_API_SOCKET", "/run/homeharbor/api.sock")
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
        _ = Directory.CreateDirectory(path);
        await ChmodAsync(runner, path, mode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(owner) || !string.IsNullOrWhiteSpace(group))
        {
            await ChownAsync(runner, path, owner ?? string.Empty, group ?? string.Empty, cancellationToken);
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

    private static async Task NormalizeHomeHarborFileAsync(ICommandRunner runner, string path, int mode, CancellationToken cancellationToken)
    {
        await ChmodAsync(runner, path, mode, cancellationToken);
        await ChownAsync(runner, path, "homeharbor", "homeharbor", cancellationToken);
    }

    private static async Task ChownAsync(ICommandRunner runner, string path, string owner, string group, CancellationToken cancellationToken)
    {
        var spec = string.IsNullOrEmpty(group) ? owner : owner + ":" + group;
        if (string.IsNullOrEmpty(spec) || spec == ":")
        {
            return;
        }

        _ = await runner.RunAsync("chown", [spec, path], cancellationToken: cancellationToken);
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

    private static string DefaultCaddyfile()
        => """
            {
                auto_https disable_redirects
            }

            :80 {
                reverse_proxy unix//run/homeharbor/api.sock {
                    header_up Host {host}
                }
            }

            homeharbor.local {
                tls internal
                reverse_proxy unix//run/homeharbor/api.sock {
                    header_up Host {host}
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
               passdb backend = tdbsam
               private dir = /var/lib/homeharbor/samba/private
               state directory = /var/lib/homeharbor/samba/state
               cache directory = /var/lib/homeharbor/samba/cache
               lock directory = /var/lib/homeharbor/samba/lock
               disable spoolss = yes
               load printers = no
            """.Replace("            ", string.Empty, StringComparison.Ordinal);

    private static void RefuseReadOnlyRootfsPath(string path, string label)
    {
        if (path.StartsWith("/etc/", StringComparison.Ordinal) || path.StartsWith("/usr/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"refusing to write {label} under read-only rootfs path {path}");
        }
    }

    private static string? JsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;

    private static IReadOnlyList<string> JsonStringArray(JsonElement element, string name)
    {
        return !element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array
            ? []
            : [.. property.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))];
    }

    private static SystemAppHotCheck? ReadSystemAppHotCheck(JsonElement element)
    {
        if (!element.TryGetProperty("hotCheck", out var hotCheck) || hotCheck.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var command = JsonString(hotCheck, "command");
        return string.IsNullOrWhiteSpace(command)
            ? null
            : new SystemAppHotCheck(
            HomeHarborAppManifestVerifier.ValidateCommandName(command),
            JsonStringArray(hotCheck, "args"));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
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

    private static async Task<string?> RootParentDeviceAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var sourceResult = await runner.RunAsync("findmnt", ["-n", "-o", "SOURCE", "/"], cancellationToken: cancellationToken);
        var source = sourceResult.Stdout.Trim();
        if (sourceResult.ExitCode != 0 || string.IsNullOrWhiteSpace(source) || !source.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return null;
        }

        var parent = await runner.RunAsync("lsblk", ["-no", "PKNAME", source], cancellationToken: cancellationToken);
        var parentName = parent.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        var candidate = parent.ExitCode == 0 && !string.IsNullOrWhiteSpace(parentName) ? "/dev/" + parentName : source;
        var real = await runner.RunAsync("readlink", ["-f", candidate], cancellationToken: cancellationToken);
        return real.ExitCode == 0 ? real.Stdout.Trim() : candidate;
    }

    private static async Task<bool> DeviceExistsAsync(ICommandRunner runner, string path, bool allowFiles, CancellationToken cancellationToken)
    {
        var block = await runner.RunAsync("test", ["-b", path], cancellationToken: cancellationToken);
        return block.ExitCode == 0 || (allowFiles && File.Exists(path));
    }

    private static async Task<bool> DeviceHasMountsAsync(ICommandRunner runner, string path, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("lsblk", ["-nrpo", "MOUNTPOINTS", path], cancellationToken: cancellationToken);
        return result.ExitCode == 0 && result.Stdout.Split('\n').Any(line => !string.IsNullOrWhiteSpace(line));
    }

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
    {
        var result = await runner.RunAsync("lsblk", ["-nrpo", "LABEL,PARTLABEL", path], cancellationToken: cancellationToken);
        return result.ExitCode != 0
            ? []
            : [.. result.Stdout
            .Split(['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(label => !string.IsNullOrWhiteSpace(label))];
    }

}
