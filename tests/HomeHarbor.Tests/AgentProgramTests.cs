using System.Globalization;
using System.IO.Compression;
using System.Text;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class AgentProgramTests
{
    [TestMethod]
    public async Task EnsureOtaChannelFileAsync_Normalizes_Existing_Installer_Channel_File()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var channelFile = Path.Combine(tempDir, "channel");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(channelFile, "stable\n");

            await AgentProgram.EnsureOtaChannelFileAsync(runner, channelFile, "dev", CancellationToken.None);

            Assert.AreEqual("stable\n", await File.ReadAllTextAsync(channelFile));
            Assert.HasCount(2, runner.Calls);
            Assert.AreEqual("chmod", runner.Calls[0].FileName);
            CollectionAssert.AreEqual(new[] { "0640", channelFile }, runner.Calls[0].Arguments);
            Assert.AreEqual("chown", runner.Calls[1].FileName);
            CollectionAssert.AreEqual(new[] { "homeharbor:homeharbor", channelFile }, runner.Calls[1].Arguments);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task EnsureOtaChannelFileAsync_Writes_Default_Channel_With_Service_Owner()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var channelFile = Path.Combine(tempDir, "channel");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(tempDir);

            await AgentProgram.EnsureOtaChannelFileAsync(runner, channelFile, "daily", CancellationToken.None);

            Assert.AreEqual("daily\n", await File.ReadAllTextAsync(channelFile));
            Assert.HasCount(2, runner.Calls);
            Assert.AreEqual("chmod", runner.Calls[0].FileName);
            CollectionAssert.AreEqual(new[] { "0640", channelFile }, runner.Calls[0].Arguments);
            Assert.AreEqual("chown", runner.Calls[1].FileName);
            CollectionAssert.AreEqual(new[] { "homeharbor:homeharbor", channelFile }, runner.Calls[1].Arguments);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void MapperNames_Use_StorageApply_Namespace()
    {
        var mapperNames = AgentProgram.MapperNames(2);

        CollectionAssert.AreEqual(new[] { "homeharbor-storage-data", "homeharbor-storage-data-1" }, mapperNames.ToArray());
        CollectionAssert.DoesNotContain(mapperNames.ToArray(), "homeharbor-data");
    }

    [TestMethod]
    public async Task BootAttempt_Uses_Explicit_Arguments_Instead_Of_Environment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempDir, "state");
        var esp = Path.Combine(tempDir, "esp");
        var envStateDir = Path.Combine(tempDir, "env-state");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(tempDir);
            BootState.Initialize(esp, "A", "A", "B");

            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_BOOTLOOP_STATE_DIR", envStateDir),
                ("HOMEHARBOR_BOOTLOOP_NOW", "9999"),
                ("HOMEHARBOR_BOOTLOOP_THRESHOLD", "0"),
                ("HOMEHARBOR_BOOTLOOP_DRY_RUN", "0"));

            var exitCode = await AgentProgram.RunAsync([
                "boot-attempt",
                "--state-dir", stateDir,
                "--esp", esp,
                "--window-seconds", "600",
                "--threshold", "2",
                "--now", "1234",
                "--dry-run"
            ], runner, CancellationToken.None);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual("1234\n", await File.ReadAllTextAsync(Path.Combine(stateDir, "attempts")));
            Assert.IsFalse(Directory.Exists(envStateDir));
            Assert.IsEmpty(runner.Calls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task BootAttempt_Allows_Current_Threshold_Boot_To_Mark_Success()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempDir, "state");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(stateDir);
            await File.WriteAllTextAsync(Path.Combine(stateDir, "attempts"), "1000\n1010\n");

            var exitCode = await AgentProgram.RunAsync([
                "boot-attempt",
                "--state-dir", stateDir,
                "--window-seconds", "600",
                "--threshold", "3",
                "--now", "1020",
                "--dry-run"
            ], runner, CancellationToken.None);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual("1000\n1010\n1020\n", await File.ReadAllTextAsync(Path.Combine(stateDir, "attempts")));
            Assert.IsFalse(File.Exists(Path.Combine(stateDir, "recovery-requested-at")));
            Assert.IsEmpty(runner.Calls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task BootAttempt_Requests_Recovery_After_Threshold_Prior_Attempts_And_Resets_Counter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempDir, "state");
        var esp = Path.Combine(tempDir, "esp");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(stateDir);
            BootState.Initialize(esp, "A", "A", "B");
            await File.WriteAllTextAsync(Path.Combine(stateDir, "attempts"), "1000\n1010\n1020\n");

            var exitCode = await AgentProgram.RunAsync([
                "boot-attempt",
                "--state-dir", stateDir,
                "--esp", esp,
                "--window-seconds", "600",
                "--threshold", "3",
                "--now", "1030",
                "--dry-run"
            ], runner, CancellationToken.None);

            Assert.AreEqual(0, exitCode);
            Assert.IsFalse(File.Exists(Path.Combine(stateDir, "attempts")));
            Assert.IsTrue(File.Exists(Path.Combine(stateDir, "recovery-requested-at")));
            Assert.HasCount(1, runner.Calls);
            Assert.AreEqual("efivar", runner.Calls[0].FileName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OtaCommit_Uses_Explicit_Arguments_Instead_Of_Environment()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempDir, "ota");
        var envStateDir = Path.Combine(tempDir, "env-ota");
        var runDir = Path.Combine(tempDir, "run");
        var esp = Path.Combine(tempDir, "esp");
        var bootEnv = Path.Combine(tempDir, "boot.env");
        var envBootEnv = Path.Combine(tempDir, "env-boot.env");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(stateDir);
            _ = Directory.CreateDirectory(envStateDir);
            BootState.Initialize(esp, "A", "A", "A");
            await File.WriteAllTextAsync(Path.Combine(stateDir, "pending.json"), """
                {
                  "targetBootSlot": "B",
                  "targetRootSlot": "B",
                  "targetRecoverySlot": "B",
                  "rootLogical": "root_b",
                  "rootDescriptorDigest": "root-desc",
                  "modulesLogical": "modules_b",
                  "modulesDescriptorDigest": "modules-desc",
                  "firmwareLogical": "firmware_b",
                  "firmwareDescriptorDigest": "firmware-desc",
                  "vbmetaPartition": "vbmeta_b",
                  "vbmetaDigest": "vbmeta-desc"
                }
                """);
            await File.WriteAllTextAsync(bootEnv, """
                HOMEHARBOR_BOOT_SLOT=B
                HOMEHARBOR_SLOT=B
                HOMEHARBOR_ROOT_LOGICAL=root_b
                HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST=root-desc
                HOMEHARBOR_MODULES_LOGICAL=modules_b
                HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST=modules-desc
                HOMEHARBOR_FIRMWARE_LOGICAL=firmware_b
                HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST=firmware-desc
                HOMEHARBOR_VBMETA_PARTITION=vbmeta_b
                HOMEHARBOR_VBMETA_DIGEST=vbmeta-desc
                """);
            await File.WriteAllTextAsync(envBootEnv, "HOMEHARBOR_BOOT_SLOT=A\n");

            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_OTA_STATE_DIR", envStateDir),
                ("HOMEHARBOR_ESP_PATH", Path.Combine(tempDir, "env-esp")),
                ("HOMEHARBOR_OTA_BOOT_ENV", envBootEnv));

            var exitCode = await AgentProgram.RunAsync([
                "ota-commit",
                "--state-dir", stateDir,
                "--esp", esp,
                "--boot-env", bootEnv,
                "--run-dir", runDir
            ], runner, CancellationToken.None);

            Assert.AreEqual(0, exitCode);
            Assert.IsFalse(File.Exists(Path.Combine(stateDir, "pending.json")));
            Assert.IsTrue(File.Exists(Path.Combine(stateDir, "last-committed.json")));
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "last-committed-ota")));
            Assert.IsFalse(File.Exists(Path.Combine(envStateDir, "last-committed.json")));

            var bootState = BootState.Read(esp);
            Assert.AreEqual("B", bootState.DefaultSlot);
            Assert.AreEqual("B", bootState.DefaultRootSlot);
            Assert.AreEqual("B", bootState.RecoverySlot);
            Assert.IsEmpty(runner.Calls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task OtaApply_Verifies_Manifest_Before_Bounded_Extraction()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(tempDir, "homeharbor-system-ota-0.1.0.tar.gz");
        var publicKey = Path.Combine(tempDir, "release.pub.pem");
        var verifiedManifestWasReadable = false;
        string? verifiedManifestText = null;
        var runner = new RecordingCommandRunner((fileName, args, _) =>
        {
            if (fileName == "verify-ota-manifest")
            {
                verifiedManifestWasReadable = File.Exists(args[0]);
                verifiedManifestText = File.ReadAllText(args[0]);
            }

            return new CommandResult(0, string.Empty, string.Empty, fileName);
        });

        try
        {
            _ = Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(publicKey, "test public key");
            WriteOtaWithOversizedMember(bundle);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                AgentProgram.RunAsync([
                    "ota-apply",
                    bundle,
                    "--public-key", publicKey,
                    "--verify-script", "verify-ota-manifest"
                ], runner, CancellationToken.None));

            Assert.Contains("exceeds maximum size", ex.Message);
            Assert.HasCount(1, runner.Calls);
            Assert.AreEqual("verify-ota-manifest", runner.Calls[0].FileName);
            Assert.AreEqual(publicKey, runner.Calls[0].Arguments[1]);
            Assert.IsTrue(verifiedManifestWasReadable);
            Assert.Contains("\"channel\":\"dev\"", verifiedManifestText ?? string.Empty);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ApplySmb_DryRun_Does_Not_Replace_Config_Or_Delete_Credentials()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempDir, "samba");
        var credentialDir = Path.Combine(tempDir, "credentials");
        var conf = Path.Combine(stateDir, "smb.conf");
        var configFile = Path.Combine(tempDir, "desired-smb.conf");
        var credentialFile = Path.Combine(credentialDir, "queued.json");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(stateDir);
            _ = Directory.CreateDirectory(credentialDir);
            await File.WriteAllTextAsync(conf, "existing smb config\n");
            await File.WriteAllTextAsync(configFile, "desired smb config\n");
            await File.WriteAllTextAsync(credentialFile, """
                {
                  "action": "upsert",
                  "unixUser": "homeharbor-smb001",
                  "password": "secret"
                }
                """);

            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_DRY_RUN", "1"),
                ("HOMEHARBOR_SMB_STATE_DIR", stateDir),
                ("HOMEHARBOR_SMB_CONF", conf),
                ("HOMEHARBOR_SMB_CREDENTIAL_DIR", credentialDir),
                ("HOMEHARBOR_SMB_CONFIG_FILE", configFile));

            var exitCode = await AgentProgram.RunAsync(["apply-smb"], runner, CancellationToken.None);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual("existing smb config\n", await File.ReadAllTextAsync(conf));
            Assert.IsTrue(File.Exists(credentialFile));
            Assert.IsEmpty(runner.Calls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ApplyContainers_DryRun_Does_Not_Write_Or_Delete_Quadlet_Files()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(tempDir, "home");
        var quadletDir = Path.Combine(home, ".config", "containers", "systemd");
        var desiredFile = Path.Combine(tempDir, "desired-containers.json");
        var keepFile = Path.Combine(quadletDir, "keep.container");
        var deleteFile = Path.Combine(quadletDir, "delete.container");
        var runner = new RecordingCommandRunner((fileName, args, _) =>
            fileName == "id" && args.SequenceEqual(["-u", "homeharbor-containers"])
                ? new CommandResult(0, "1000\n", string.Empty, fileName)
                : fileName == "id" && args.SequenceEqual(["-g", "homeharbor-containers"])
                    ? new CommandResult(0, "1001\n", string.Empty, fileName)
                    : new CommandResult(0, string.Empty, string.Empty, fileName));

        try
        {
            _ = Directory.CreateDirectory(quadletDir);
            await File.WriteAllTextAsync(keepFile, "[Container]\nImage=old\n");
            await File.WriteAllTextAsync(deleteFile, "[Container]\nImage=delete\n");
            await File.WriteAllTextAsync(desiredFile, """
                [
                  {
                    "id": "keep",
                    "serviceName": "Keep",
                    "unitName": "keep.service",
                    "quadletFile": "keep.container",
                    "desiredState": "running",
                    "requestedAction": "none",
                    "quadlet": "[Container]\nImage=new\n"
                  },
                  {
                    "id": "delete",
                    "serviceName": "Delete",
                    "unitName": "delete.service",
                    "quadletFile": "delete.container",
                    "desiredState": "deleted",
                    "requestedAction": "delete"
                  }
                ]
                """);

            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_DRY_RUN", "1"),
                ("HOMEHARBOR_CONTAINER_HOME", home),
                ("HOMEHARBOR_QUADLET_DIR", quadletDir),
                ("HOMEHARBOR_CONTAINER_DESIRED_FILE", desiredFile));

            var exitCode = await AgentProgram.RunAsync(["apply-containers"], runner, CancellationToken.None);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual("[Container]\nImage=old\n", await File.ReadAllTextAsync(keepFile));
            Assert.AreEqual("[Container]\nImage=delete\n", await File.ReadAllTextAsync(deleteFile));
            Assert.IsTrue(File.Exists(desiredFile));
            Assert.HasCount(2, runner.Calls);
            Assert.AreEqual("id", runner.Calls[0].FileName);
            CollectionAssert.AreEqual(new[] { "-u", "homeharbor-containers" }, runner.Calls[0].Arguments);
            Assert.AreEqual("id", runner.Calls[1].FileName);
            CollectionAssert.AreEqual(new[] { "-g", "homeharbor-containers" }, runner.Calls[1].Arguments);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void MdadmAssembleArguments_Use_Opened_Mapper_Sources()
    {
        var args = AgentProgram.MdadmAssembleArguments(
            "11111111:22222222:33333333:44444444",
            ["/dev/mapper/homeharbor-storage-data", "/dev/mapper/homeharbor-storage-data-1"]);

        CollectionAssert.AreEqual(
            new[]
            {
                "--assemble",
                "/dev/md/homeharbor-data",
                "--run",
                "--uuid=11111111:22222222:33333333:44444444",
                "/dev/mapper/homeharbor-storage-data",
                "/dev/mapper/homeharbor-storage-data-1"
            },
            args.ToArray());
    }

    [TestMethod]
    public async Task StoragePostApply_Skips_Service_Start_When_Data_Is_Not_Mounted()
    {
        var runner = new RecordingCommandRunner((fileName, args, _) =>
            fileName == "findmnt"
                && args.SequenceEqual(["-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data"])
                ? new CommandResult(1, string.Empty, string.Empty, fileName)
                : new CommandResult(0, string.Empty, string.Empty, fileName));

        var exitCode = await AgentProgram.RunAsync(["storage-postapply"], runner, CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, runner.Calls);
        Assert.AreEqual("findmnt", runner.Calls[0].FileName);
        CollectionAssert.AreEqual(new[] { "-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data" }, runner.Calls[0].Arguments);
    }

    [TestMethod]
    public async Task StoragePostApply_Queues_Full_Service_Path_Without_Blocking()
    {
        var runner = new RecordingCommandRunner((fileName, args, _) =>
            fileName == "findmnt"
                && args.SequenceEqual(["-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data"])
                ? new CommandResult(0, "btrfs\n", string.Empty, fileName)
                : new CommandResult(0, string.Empty, string.Empty, fileName));

        var exitCode = await AgentProgram.RunAsync(["storage-postapply"], runner, CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(2, runner.Calls);
        Assert.AreEqual("findmnt", runner.Calls[0].FileName);
        CollectionAssert.AreEqual(new[] { "-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data" }, runner.Calls[0].Arguments);
        Assert.AreEqual("systemctl", runner.Calls[1].FileName);
        CollectionAssert.AreEqual(new[] { "--no-block", "start", "homeharbor-postgresql-bootstrap.service" }, runner.Calls[1].Arguments);
    }

    [TestMethod]
    public async Task PostgresBootstrap_Creates_Database_Migrates_And_Marks_Storage_Ready()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var statusFile = Path.Combine(tempDir, "status.json");
        var apiDll = Path.Combine(tempDir, "api", "HomeHarbor.Api.dll");
        var runner = new RecordingCommandRunner((fileName, _, _) =>
            new CommandResult(0, string.Empty, string.Empty, fileName));

        using var environment = new TemporaryEnvironment(
            ("HOMEHARBOR_STORAGE_STATUS_FILE", statusFile),
            ("HOMEHARBOR_API_DLL", apiDll));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(apiDll)!);
        _ = Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(statusFile, """
            {
              "state": "Running",
              "progress": 90,
              "message": "Creating HomeHarbor database.",
              "planId": "plan-123"
            }
            """);

        var exitCode = await AgentProgram.RunAsync(["postgres-bootstrap"], runner, CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.Contains(call =>
            call.FileName == "runuser"
            && call.Arguments.SequenceEqual(["-u", "postgres", "--", "psql", "-h", "/run/postgresql", "-p", "5432", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", "CREATE ROLE \"homeharbor\" LOGIN;"]), runner.Calls);
        Assert.Contains(call =>
            call.FileName == "runuser"
            && call.Arguments.SequenceEqual(["-u", "postgres", "--", "createdb", "-h", "/run/postgresql", "-p", "5432", "-O", "homeharbor", "homeharbor"]), runner.Calls);
        var migrate = runner.Calls.Single(call =>
            call.FileName == "runuser"
            && call.Arguments.SequenceEqual(["-u", "homeharbor", "--", "dotnet", apiDll, "database-migrate"]));
        Assert.AreEqual(Path.GetDirectoryName(apiDll), migrate.Options?.WorkingDirectory);
        Assert.AreEqual(TimeSpan.FromMinutes(5), migrate.Options?.Timeout);
        Assert.Contains(call =>
            call.FileName == "systemctl"
            && call.Arguments.SequenceEqual(["--no-block", "try-restart", "homeharbor-api.service"]), runner.Calls);
        Assert.Contains(call =>
            call.FileName == "systemctl"
            && call.Arguments.SequenceEqual(["--no-block", "start", "homeharbor-caddy-render.service"]), runner.Calls);

        var status = await File.ReadAllTextAsync(statusFile);
        Assert.Contains("\"state\": \"Succeeded\"", status);
        Assert.Contains("\"message\": \"Storage and database are ready.\"", status);
        Assert.Contains("\"planId\": \"plan-123\"", status);
    }

    [TestMethod]
    [DataRow("btrfs")]
    [DataRow("xfs")]
    [DataRow("zfs")]
    public async Task IsHomeHarborDataMountAsync_Accepts_Supported_Data_File_Systems(string fileSystem)
    {
        var runner = new RecordingCommandRunner((fileName, args, _) =>
            fileName == "findmnt"
                && args.SequenceEqual(["-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data"])
                ? new CommandResult(0, fileSystem + "\n", string.Empty, fileName)
                : new CommandResult(1, string.Empty, string.Empty, fileName));

        var mounted = await AgentProgram.IsHomeHarborDataMountAsync(runner, CancellationToken.None);

        Assert.IsTrue(mounted);
        Assert.AreEqual("findmnt", runner.Calls[0].FileName);
        CollectionAssert.AreEqual(new[] { "-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data" }, runner.Calls[0].Arguments);
    }

    [TestMethod]
    public async Task IsHomeHarborDataMountAsync_Rejects_Service_Namespace_Bind_Mount()
    {
        var runner = new RecordingCommandRunner((fileName, args, _) =>
            fileName == "findmnt"
                && args.SequenceEqual(["-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data"])
                ? new CommandResult(0, "erofs\n", string.Empty, fileName)
                : new CommandResult(1, string.Empty, string.Empty, fileName));

        var mounted = await AgentProgram.IsHomeHarborDataMountAsync(runner, CancellationToken.None);

        Assert.IsFalse(mounted);
        Assert.AreEqual("findmnt", runner.Calls[0].FileName);
        CollectionAssert.AreEqual(new[] { "-n", "-o", "FSTYPE", "--mountpoint", "/homeharbor-data" }, runner.Calls[0].Arguments);
    }

    private static void WriteOtaWithOversizedMember(string path)
    {
        const string top = "homeharbor-system-ota-0.1.0";
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        WriteTarEntry(gzip, top + "/", [], '5');
        WriteTarEntry(gzip, top + "/manifest.json", Encoding.UTF8.GetBytes("{\"channel\":\"dev\"}\n"), '0');
        WriteTarHeader(gzip, top + "/rootfs.img", 5L * 1024 * 1024 * 1024, '0');
    }

    private static void WriteTarEntry(Stream output, string name, byte[] payload, char typeFlag)
    {
        WriteTarHeader(output, name, payload.Length, typeFlag);
        output.Write(payload);
        var padding = 512 - payload.Length % 512;
        if (padding != 512)
        {
            output.Write(new byte[padding]);
        }
    }

    private static void WriteTarHeader(Stream output, string name, long size, char typeFlag)
    {
        var header = new byte[512];
        WriteAscii(header, 0, 100, name);
        WriteOctal(header, 100, 8, typeFlag == '5' ? 493 : 420);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, size);
        WriteOctal(header, 136, 12, 0);
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)typeFlag;
        WriteAscii(header, 257, 6, "ustar");
        WriteAscii(header, 263, 2, "00");

        var checksum = header.Sum(value => value);
        var checksumText = Convert.ToString(checksum, 8).PadLeft(6, '0');
        WriteAscii(header, 148, 6, checksumText);
        header[154] = 0;
        header[155] = (byte)' ';
        output.Write(header);
    }

    private static void WriteAscii(byte[] header, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > length)
        {
            throw new InvalidOperationException("tar header value is too long: " + value);
        }

        Array.Copy(bytes, 0, header, offset, bytes.Length);
    }

    private static void WriteOctal(byte[] header, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8);
        if (text.Length > length - 1)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"tar header value is too large: {value}"));
        }

        WriteAscii(header, offset, length - 1, text.PadLeft(length - 1, '0'));
    }

    private sealed class RecordingCommandRunner(Func<string, string[], CommandRunOptions?, CommandResult>? handler = null) : ICommandRunner
    {
        private readonly Func<string, string[], CommandRunOptions?, CommandResult>? handler = handler;

        public List<CommandCall> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            Calls.Add(new CommandCall(fileName, args, options));
            return Task.FromResult(handler?.Invoke(fileName, args, options) ?? new CommandResult(0, string.Empty, string.Empty, fileName));
        }
    }

    private sealed record CommandCall(string FileName, string[] Arguments, CommandRunOptions? Options);

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly (string Name, string? Value)[] previous;

        public TemporaryEnvironment(params (string Name, string? Value)[] values)
        {
            previous = [.. values.Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))];
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
