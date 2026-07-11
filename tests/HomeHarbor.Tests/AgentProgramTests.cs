using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AgentProgramTests
{
    [TestMethod]
    public void StorageHealth_Defers_Only_Expected_PreOobe_Statuses()
    {
        Assert.IsTrue(AgentProgram.IsExpectedPreOobeDeferral(HttpStatusCode.Conflict));
        Assert.IsTrue(AgentProgram.IsExpectedPreOobeDeferral(HttpStatusCode.ServiceUnavailable));
        Assert.IsFalse(AgentProgram.IsExpectedPreOobeDeferral(HttpStatusCode.BadRequest));
        Assert.IsFalse(AgentProgram.IsExpectedPreOobeDeferral(HttpStatusCode.Unauthorized));
        Assert.IsFalse(AgentProgram.IsExpectedPreOobeDeferral(HttpStatusCode.Forbidden));
        Assert.IsFalse(AgentProgram.IsExpectedPreOobeDeferral(HttpStatusCode.InternalServerError));
        Assert.IsFalse(AgentProgram.IsExpectedPreOobeDeferral(null));

        Assert.IsTrue(AgentProgram.IsExpectedStorageHealthDeferral(HttpStatusCode.Conflict));
        Assert.IsTrue(AgentProgram.IsExpectedStorageHealthDeferral(HttpStatusCode.ServiceUnavailable));
        Assert.IsFalse(AgentProgram.IsExpectedStorageHealthDeferral(HttpStatusCode.InternalServerError));
        Assert.IsFalse(AgentProgram.IsExpectedStorageHealthDeferral(null));
    }

    [TestMethod]
    [DataRow((int)HttpStatusCode.Conflict)]
    [DataRow((int)HttpStatusCode.ServiceUnavailable)]
    public async Task Runtime_Reconcile_Commands_Defer_Only_When_Health_Confirms_PreOobe(int desiredStatusCode)
    {
        foreach (var command in new[] { "apply-smb", "apply-containers" })
        {
            var tempDir = Directory.CreateTempSubdirectory("homeharbor-pre-oobe-reconcile-");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var requestsTask = ServeHttpResponsesAsync(
                listener,
                [
                    new TestHttpResponse((HttpStatusCode)desiredStatusCode, "{\"error\":\"storage is not ready\"}"),
                    new TestHttpResponse(HttpStatusCode.OK, "{\"status\":\"storage-pending\"}")
                ],
                timeout.Token);
            var runner = new RecordingCommandRunner();

            try
            {
                using var environment = new TemporaryEnvironment(
                    ("HOMEHARBOR_DRY_RUN", "1"),
                    ("HOMEHARBOR_API_URL", $"http://127.0.0.1:{endpoint.Port}"),
                    ("HOMEHARBOR_API_SOCKET", null),
                    ("HOMEHARBOR_AUTOMATION_TOKEN_PATH", Path.Combine(tempDir.FullName, "missing.jwt")),
                    ("HOMEHARBOR_SMB_STATE_DIR", Path.Combine(tempDir.FullName, "samba")),
                    ("HOMEHARBOR_SMB_CONF", Path.Combine(tempDir.FullName, "samba", "smb.conf")),
                    ("HOMEHARBOR_SMB_CREDENTIAL_DIR", Path.Combine(tempDir.FullName, "credentials")),
                    ("HOMEHARBOR_SMB_DESIRED_FILE", null),
                    ("HOMEHARBOR_CONTAINER_HOME", Path.Combine(tempDir.FullName, "containers")),
                    ("HOMEHARBOR_QUADLET_DIR", Path.Combine(tempDir.FullName, "containers", "quadlets")),
                    ("HOMEHARBOR_CONTAINER_DESIRED_FILE", null));

                var exitCode = await AgentProgram.RunAsync([command], runner, timeout.Token);
                var requests = await requestsTask;

                Assert.AreEqual(0, exitCode, command);
                Assert.IsEmpty(runner.Calls, command);
                Assert.IsTrue(requests[0]?.StartsWith("GET /api/", StringComparison.Ordinal) ?? false, command);
                Assert.IsTrue(requests[1]?.StartsWith("GET /api/system/health ", StringComparison.Ordinal) ?? false, command);
            }
            finally
            {
                tempDir.Delete(recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Runtime_Reconcile_Does_Not_Defer_A_Service_Failure_After_Storage_Is_Ready()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-ready-reconcile-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestsTask = ServeHttpResponsesAsync(
            listener,
            [
                new TestHttpResponse(HttpStatusCode.ServiceUnavailable, "{\"error\":\"temporary failure\"}"),
                new TestHttpResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}")
            ],
            timeout.Token);

        try
        {
            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_DRY_RUN", "1"),
                ("HOMEHARBOR_API_URL", $"http://127.0.0.1:{endpoint.Port}"),
                ("HOMEHARBOR_API_SOCKET", null),
                ("HOMEHARBOR_AUTOMATION_TOKEN_PATH", Path.Combine(tempDir.FullName, "missing.jwt")),
                ("HOMEHARBOR_SMB_STATE_DIR", Path.Combine(tempDir.FullName, "samba")),
                ("HOMEHARBOR_SMB_CONF", Path.Combine(tempDir.FullName, "samba", "smb.conf")),
                ("HOMEHARBOR_SMB_DESIRED_FILE", null));

            var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
                AgentProgram.RunAsync(["apply-smb"], new RecordingCommandRunner(), timeout.Token));
            var requests = await requestsTask;

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.HasCount(2, requests);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void DefaultCaddyfile_Redirects_Plaintext_Instead_Of_Proxying_Credentials()
    {
        var caddyfile = AgentProgram.DefaultCaddyfile();

        Assert.Contains(":80 {", caddyfile);
        Assert.Contains("redir https://homeharbor.local{uri} permanent", caddyfile);
        Assert.Contains("@homeHarborCa path /homeharbor-ca.crt", caddyfile);
        Assert.Contains("root * /var/lib/caddy/pki/authorities/local", caddyfile);
        Assert.Contains("rewrite * /root.crt", caddyfile);
        Assert.Contains("admin unix//run/caddy/admin.sock", caddyfile);
        Assert.DoesNotContain("admin off", caddyfile);
        Assert.AreEqual(1, caddyfile.Split("reverse_proxy unix//run/homeharbor-api/api.sock", StringSplitOptions.None).Length - 1);
        Assert.Contains("header_up X-Forwarded-For {remote_host}", caddyfile);
        Assert.Contains("header_up X-Forwarded-Proto {scheme}", caddyfile);
    }

    [TestMethod]
    public async Task TlsTrustBootstrap_Displays_A_CA_Fingerprint_And_Safe_Enrollment_Warning()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-tls-trust-");
        try
        {
            var certificatePath = Path.Combine(tempDir.FullName, "root.crt");
            var consolePath = Path.Combine(tempDir.FullName, "console");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=HomeHarbor test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(consolePath, string.Empty);

            var fingerprint = await TlsTrustBootstrap.DisplayAsync(certificatePath, [consolePath], CancellationToken.None);
            var output = await File.ReadAllTextAsync(consolePath);

            Assert.AreEqual(TlsTrustBootstrap.FormatFingerprint(SHA256.HashData(certificate.RawData)), fingerprint);
            Assert.Contains(CaddyTrustConfiguration.CertificateDownloadUrl, output);
            Assert.Contains(fingerprint, output);
            Assert.Contains("Do not enter the setup code or a password", output);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void RootPathGuard_Rejects_Symlinked_Root_Managed_Directory()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-root-path-");
        try
        {
            var victim = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "victim"));
            var state = Path.Combine(tempDir.FullName, "samba");
            _ = Directory.CreateSymbolicLink(state, victim.FullName);
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                RootPathGuard.CreateDirectory(Path.Combine(state, "private"), "SMB state directory"));

            Assert.Contains("symbolic link", ex.Message);
            Assert.IsFalse(File.Exists(Path.Combine(victim.FullName, "smb.conf")));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void OtaApply_CreateWorkDirectory_Creates_A_Private_Unique_Child()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-ota-work-");
        try
        {
            var workRoot = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "work")).FullName;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    workRoot,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

            var first = OtaApplyCommand.CreateWorkDirectory(workRoot);
            var second = OtaApplyCommand.CreateWorkDirectory(workRoot);

            Assert.IsTrue(Directory.Exists(first));
            Assert.IsTrue(Directory.Exists(second));
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(workRoot, Path.GetDirectoryName(first));
            Assert.AreEqual(workRoot, Path.GetDirectoryName(second));
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode privateDirectory =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Assert.AreEqual(privateDirectory, File.GetUnixFileMode(workRoot) & (UnixFileMode)0x1FF);
                Assert.AreEqual(privateDirectory, File.GetUnixFileMode(first) & (UnixFileMode)0x1FF);
                Assert.AreEqual(privateDirectory, File.GetUnixFileMode(second) & (UnixFileMode)0x1FF);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void OtaApply_CreateWorkDirectory_Rejects_A_Symlinked_Work_Root()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-ota-work-link-");
        try
        {
            var victim = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "victim"));
            var workRoot = Path.Combine(tempDir.FullName, "work");
            _ = Directory.CreateSymbolicLink(workRoot, victim.FullName);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                OtaApplyCommand.CreateWorkDirectory(workRoot));

            Assert.Contains("symbolic link", ex.Message);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(victim.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task EnsureCaddyGroupIsolation_Verifies_Immutable_Group_Membership_Without_Mutating_Accounts()
    {
        var runner = new RecordingCommandRunner((fileName, _, _) =>
            new CommandResult(0, fileName == "id" ? "caddy homeharbor-api\n" : string.Empty, string.Empty, fileName));

        await AgentProgram.EnsureCaddyGroupIsolationAsync(runner, CancellationToken.None);

        Assert.HasCount(1, runner.Calls);
        Assert.AreEqual("id", runner.Calls[0].FileName);
        CollectionAssert.AreEqual(new[] { "-nG", "caddy" }, runner.Calls[0].Arguments);
    }

    [TestMethod]
    [DataRow("caddy", "missing the dedicated homeharbor-api group")]
    [DataRow("caddy homeharbor-api homeharbor", "must not belong to the broad homeharbor group")]
    public async Task EnsureCaddyGroupIsolation_Fails_Closed_For_Invalid_Image_Membership(
        string groups,
        string expectedError)
    {
        var runner = new RecordingCommandRunner((fileName, _, _) =>
            new CommandResult(0, groups + "\n", string.Empty, fileName));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentProgram.EnsureCaddyGroupIsolationAsync(runner, CancellationToken.None));

        Assert.Contains(expectedError, error.Message);
        Assert.DoesNotContain(call => call.FileName is "usermod" or "gpasswd", runner.Calls);
    }

    [TestMethod]
    public void Reconcile_Identity_Validators_Reject_Path_And_Option_Injection()
    {
        const string id = "11111111-1111-1111-1111-111111111111";
        const string service = "homeharbor-11111111111111111111111111111111";
        AgentProgram.ValidateContainerReconcileIdentity(
            id,
            service,
            service + ".service",
            service + ".container");

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.ValidateContainerReconcileIdentity(
                id,
                service,
                "../../attacker.service",
                "../../authorized_keys"));
        AgentProgram.ValidateSmbUnixUser("homeharbor-smb001");
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => AgentProgram.ValidateSmbUnixUser("--configfile=/tmp/evil"));
    }

    [TestMethod]
    public async Task ContainerUserManager_Starts_User_Service_And_Waits_For_Bus()
    {
        var runner = new RecordingCommandRunner((fileName, args, _) =>
            fileName == "test" && args.SequenceEqual(["-S", "/run/user/1001/bus"])
                ? new CommandResult(0, string.Empty, string.Empty, fileName)
                : new CommandResult(0, string.Empty, string.Empty, fileName));

        await AgentProgram.EnsureContainerUserManagerAsync(runner, "1001", CancellationToken.None);

        Assert.HasCount(2, runner.Calls);
        CollectionAssert.AreEqual(new[] { "start", "user@1001.service" }, runner.Calls[0].Arguments);
        CollectionAssert.AreEqual(new[] { "-S", "/run/user/1001/bus" }, runner.Calls[1].Arguments);
    }



    [TestMethod]
    public void OtaApply_Rejects_Kernel_Channel_And_Boot_Mode_Crossgrades()
    {
        Assert.AreEqual("generic", OtaApplyCommand.RequireMatchingKernelChannel("generic", "generic"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            OtaApplyCommand.RequireMatchingKernelChannel("zfs", "generic"));

        OtaApplyCommand.RequireMatchingBootMode("secure-boot-raw-uki", "secure-boot-raw-uki");
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            OtaApplyCommand.RequireMatchingBootMode("raw-uki", "secure-boot-raw-uki"));
    }

    [TestMethod]
    public void SystemApp_Rejects_Release_And_Kernel_Channel_Crossgrades()
    {
        var manifest = new SystemAppPackageManifest(
            1,
            "zfs",
            "1.0.0",
            "stable",
            "system-app",
            "https://example.invalid/payload.tar.gz",
            new string('a', 64),
            "zfs",
            "2026-07-11T00:00:00Z");

        AgentProgram.RequireSystemAppChannelMatch(manifest, "stable", "zfs");
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.RequireSystemAppChannelMatch(manifest, "beta", "zfs"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.RequireSystemAppChannelMatch(manifest, "stable", "generic"));
    }

    [TestMethod]
    public async Task SystemApp_Uses_Only_Commands_From_Original_Signed_Hhaf()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-signed-hhaf-" + Guid.NewGuid().ToString("N"));
        var publicKey = Path.Combine(tempDir, "release.pub.pem");
        try
        {
            _ = Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(publicKey, "test-public-key");
            var manifest = JsonNode.Parse("""
                {
                  "schemaVersion": 1,
                  "kind": "homeharbor.app",
                  "appKey": "safe-system-app",
                  "version": "1.2.3",
                  "channel": "dev",
                  "displayName": "Safe system app",
                  "title": "Safe system app",
                  "description": "Signed desired-state test",
                  "category": "system",
                  "recommendedInSetup": false,
                  "visibleRoles": ["Owner"],
                  "install": {
                    "type": "system",
                    "mode": "usr-overlay",
                    "manifestUrl": "https://updates.example.com/safe-system-app.json",
                    "commands": ["safe-command"],
                    "hotCheck": {
                      "command": "safe-command",
                      "args": ["--version"]
                    }
                  }
                }
                """)!.AsObject();
            using (var unsignedDocument = JsonDocument.Parse(manifest.ToJsonString()))
            {
                var payload = HomeHarborAppManifestVerifier.CanonicalAppPayload(unsignedDocument.RootElement);
                manifest["signatureAlgorithm"] = "Ed25519";
                manifest["signedPayloadSha256"] = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
                manifest["signature"] = Convert.ToBase64String(new byte[64]);
            }

            var desired = new JsonObject
            {
                ["appKey"] = "safe-system-app",
                ["manifestUrl"] = "https://attacker.invalid/evil.json",
                ["commands"] = new JsonArray("attacker-command"),
                ["hotCheck"] = new JsonObject
                {
                    ["command"] = "attacker-command",
                    ["args"] = new JsonArray("--overwrite-root")
                },
                ["hhafManifest"] = manifest.DeepClone()
            };
            using var desiredDocument = JsonDocument.Parse(desired.ToJsonString());

            var verified = await AgentProgram.VerifyDesiredSystemAppManifestAsync(
                desiredDocument.RootElement,
                "safe-system-app",
                "dev",
                publicKey,
                new RecordingCommandRunner());
            var install = (HomeHarborSystemAppInstall)verified.Install;

            Assert.AreEqual("https://updates.example.com/safe-system-app.json", install.ManifestUrl);
            CollectionAssert.AreEqual(new[] { "safe-command" }, install.Commands.ToArray());
            Assert.AreEqual("safe-command", install.HotCheck?.Command);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SystemApp_Rejects_Desired_State_Without_Original_Signed_Hhaf()
    {
        using var desired = JsonDocument.Parse("""
            {
              "appKey": "unsafe",
              "commands": ["attacker-command"],
              "hotCheck": { "command": "attacker-command", "args": ["--root"] }
            }
            """);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentProgram.VerifyDesiredSystemAppManifestAsync(
                desired.RootElement,
                "unsafe",
                "dev",
                "/does/not/matter"));

        Assert.Contains("original signed HHAF", exception.Message);
    }


    [TestMethod]
    public async Task SetupBootstrapCode_Creates_Once_Displays_And_Consumes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-setup-bootstrap-" + Guid.NewGuid().ToString("N"));
        var codePath = Path.Combine(tempDir, "setup", "bootstrap-code");
        var completePath = Path.Combine(tempDir, "setup", "bootstrap-complete");
        var requestPath = Path.Combine(tempDir, "consume-request");
        var consolePath = Path.Combine(tempDir, "physical-console");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(consolePath, string.Empty);

            var code = await SetupBootstrapCode.EnsureAndDisplayAsync(
                runner,
                codePath,
                completePath,
                [consolePath],
                CancellationToken.None);

            Assert.IsNotNull(code);
            Assert.IsTrue(SetupBootstrapCode.IsValid(code));
            Assert.AreEqual(code + Environment.NewLine, await File.ReadAllTextAsync(codePath));
            Assert.Contains(code, await File.ReadAllTextAsync(consolePath));
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                File.GetUnixFileMode(codePath));

            await File.WriteAllTextAsync(consolePath, string.Empty);
            var existing = await SetupBootstrapCode.EnsureAndDisplayAsync(
                runner,
                codePath,
                completePath,
                [consolePath],
                CancellationToken.None);
            Assert.AreEqual(code, existing);

            await File.WriteAllTextAsync(requestPath, "consume\n");
            await SetupBootstrapCode.ConsumeAsync(runner, requestPath, codePath, completePath, CancellationToken.None);
            Assert.IsFalse(File.Exists(codePath));
            Assert.IsFalse(File.Exists(requestPath));
            Assert.IsTrue(File.Exists(completePath));

            await File.WriteAllTextAsync(consolePath, string.Empty);
            var afterConsume = await SetupBootstrapCode.EnsureAndDisplayAsync(
                runner,
                codePath,
                completePath,
                [consolePath],
                CancellationToken.None);
            Assert.IsNull(afterConsume);
            Assert.AreEqual(string.Empty, await File.ReadAllTextAsync(consolePath));
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
    public async Task SetupBootstrapCode_Rejects_Symlink_Instead_Of_Overwriting_Target()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-setup-bootstrap-" + Guid.NewGuid().ToString("N"));
        var codePath = Path.Combine(tempDir, "bootstrap-code");
        var targetPath = Path.Combine(tempDir, "target");
        var consolePath = Path.Combine(tempDir, "console");
        try
        {
            _ = Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(targetPath, "do-not-change\n");
            await File.WriteAllTextAsync(consolePath, string.Empty);
            _ = File.CreateSymbolicLink(codePath, targetPath);

            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SetupBootstrapCode.EnsureAndDisplayAsync(
                    new RecordingCommandRunner(),
                    codePath,
                    Path.Combine(tempDir, "complete"),
                    [consolePath],
                    CancellationToken.None));
            Assert.AreEqual("do-not-change\n", await File.ReadAllTextAsync(targetPath));
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
            CollectionAssert.AreEqual(new[] { "root:homeharbor", channelFile }, runner.Calls[1].Arguments);
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
    public async Task EnsureOtaChannelFileAsync_Writes_Default_Channel_With_Root_Owner()
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
            CollectionAssert.AreEqual(new[] { "root:homeharbor", channelFile }, runner.Calls[1].Arguments);
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
    public void ZfsTool_Never_Executes_A_Mutable_State_Directory_Binary()
    {
        using var environment = new TemporaryEnvironment(
            ("HOMEHARBOR_STORAGE_ZFS_TOOL_DIR", "/var/lib/homeharbor/storage/oobe-tools/bin"));

        Assert.AreEqual("/usr/bin/zpool", AgentProgram.ZfsTool("zpool"));
        Assert.AreEqual("/usr/bin/zfs", AgentProgram.ZfsTool("zfs"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => AgentProgram.ZfsTool("attacker"));
    }

    [TestMethod]
    public async Task ValidateStorageTargetIdentityAsync_Verifies_Planned_Identity()
    {
        var runner = StorageIdentityRunner("disk-serial", "wwn-123", 4096);
        var target = new AgentProgram.PendingStorageTarget(
            "/dev/sdb",
            "whole-disk",
            4096,
            "Test Disk",
            "disk-serial",
            "sata",
            "wwn-123");

        var resolved = await AgentProgram.ValidateStorageTargetIdentityAsync(
            runner,
            target,
            allowFiles: false,
            CancellationToken.None);

        Assert.AreEqual("/dev/sdb", resolved);
        Assert.Contains(call =>
            call.FileName == "lsblk" &&
            call.Arguments.SequenceEqual([
                "--json", "--bytes", "--nodeps", "--output",
                "PATH,SIZE,TYPE,MODEL,SERIAL,WWN,TRAN,PARTUUID", "/dev/sdb"
            ]), runner.Calls);
    }

    [TestMethod]
    public async Task ValidateStorageTargetIdentityAsync_Rejects_Reordered_Device()
    {
        var runner = StorageIdentityRunner("replacement-serial", "wwn-replacement", 4096);
        var target = new AgentProgram.PendingStorageTarget(
            "/dev/sdb",
            "whole-disk",
            4096,
            "Test Disk",
            "planned-serial",
            "sata",
            "wwn-planned");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentProgram.ValidateStorageTargetIdentityAsync(
                runner,
                target,
                allowFiles: false,
                CancellationToken.None));

        Assert.Contains("serial changed after planning", ex.Message);
    }

    [TestMethod]
    public async Task ValidateStorageTargetIdentityAsync_Rejects_Unstable_Whole_Disk_And_Missing_Partuuid()
    {
        var runner = new RecordingCommandRunner();
        var unstableDisk = new AgentProgram.PendingStorageTarget(
            "/dev/sdb", "whole-disk", 4096, "Test Disk", null, "sata", null);
        var candidateWithoutPartuuid = new AgentProgram.PendingStorageTarget(
            "/dev/vda10", "main-reserved", 4096, null, "system-serial", "virtio", null);

        var diskEx = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentProgram.ValidateStorageTargetIdentityAsync(
                runner,
                unstableDisk,
                allowFiles: false,
                CancellationToken.None));
        var partitionEx = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentProgram.ValidateStorageTargetIdentityAsync(
                runner,
                candidateWithoutPartuuid,
                allowFiles: false,
                CancellationToken.None));

        Assert.Contains("no stable serial, WWN, or by-id path", diskEx.Message);
        Assert.Contains("missing its planned PARTUUID", partitionEx.Message);
        Assert.IsEmpty(runner.Calls);
    }

    [TestMethod]
    [DataRow("findmnt")]
    [DataRow("lsblk-labels")]
    public async Task StorageApply_Aborts_When_A_Safety_Probe_Fails_After_Identity_Validation(string failedProbe)
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-storage-safety-");
        var stateDir = Path.Combine(tempDir.FullName, "storage");
        _ = Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "pending-plan.json"), """
            {
              "planId": "safety-test",
              "confirmPhrase": "APPLY STORAGE PLAN safety-test",
              "fileSystem": "xfs",
              "dataProfile": "single",
              "metadataProfile": "xfs",
              "raidMode": "single",
              "raidBackend": "filesystem",
              "unlockMode": "passphrase",
              "devices": [
                {
                  "path": "/dev/sdb",
                  "kind": "whole-disk",
                  "sizeBytes": 4096,
                  "model": "Test Disk",
                  "serial": "disk-serial",
                  "transport": "sata",
                  "stableId": "wwn-123"
                }
              ]
            }
            """);

        var runner = new RecordingCommandRunner((fileName, args, _) => (fileName, args) switch
        {
            ("test", ["-b", "/dev/sdb"]) => Success(fileName),
            ("readlink", ["-f", var path]) => new CommandResult(0, path + "\n", string.Empty, fileName),
            ("lsblk", ["--json", "--bytes", "--nodeps", "--output", _, "/dev/sdb"]) =>
                new CommandResult(0, """
                    {"blockdevices":[{"path":"/dev/sdb","size":4096,"type":"disk","model":"Test Disk","serial":"disk-serial","wwn":"wwn-123","tran":"sata","partuuid":null}]}
                    """, string.Empty, fileName),
            ("findmnt", _) when failedProbe == "findmnt" => new CommandResult(1, string.Empty, "probe failed", fileName),
            ("findmnt", ["-n", "-o", "SOURCE", "/"]) => new CommandResult(0, "/dev/vda2\n", string.Empty, fileName),
            ("lsblk", ["-nrpo", "PATH", "-s", "/dev/vda2"]) => new CommandResult(0, "/dev/vda2\n/dev/vda\n", string.Empty, fileName),
            ("lsblk", ["--json", "--tree", "--output", "PATH,LABEL,PARTLABEL", "/dev/sdb"]) when failedProbe == "lsblk-labels" =>
                new CommandResult(1, string.Empty, "probe failed", fileName),
            ("lsblk", ["--json", "--tree", "--output", "PATH,LABEL,PARTLABEL", "/dev/sdb"]) =>
                new CommandResult(0, "{\"blockdevices\":[{\"path\":\"/dev/sdb\",\"label\":null,\"partlabel\":null}]}", string.Empty, fileName),
            ("lsblk", ["--json", "--tree", "--output", "PATH,MOUNTPOINTS", "/dev/sdb"]) =>
                new CommandResult(0, "{\"blockdevices\":[{\"path\":\"/dev/sdb\",\"mountpoints\":[null]}]}", string.Empty, fileName),
            _ => new CommandResult(1, string.Empty, "unexpected command", fileName)
        });

        try
        {
            using var environment = new TemporaryEnvironment(("HOMEHARBOR_STORAGE_STATE_DIR", stateDir));

            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                AgentProgram.RunAsync(["storage-apply"], runner, CancellationToken.None));

            Assert.Contains(call => call.FileName == "lsblk" && call.Arguments.Contains("--bytes"), runner.Calls);
            Assert.DoesNotContain(call =>
                call.FileName == "cryptsetup" && call.Arguments.FirstOrDefault() == "luksFormat", runner.Calls);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }

        static CommandResult Success(string command)
            => new(0, string.Empty, string.Empty, command);
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
        var workRoot = Path.Combine(tempDir, "ota-work");
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
                    "--work-dir", workRoot,
                    "--verify-script", "verify-ota-manifest"
                ], runner, CancellationToken.None));

            Assert.Contains("exceeds maximum size", ex.Message);
            Assert.HasCount(1, runner.Calls);
            Assert.AreEqual("verify-ota-manifest", runner.Calls[0].FileName);
            Assert.AreEqual(publicKey, runner.Calls[0].Arguments[1]);
            Assert.IsTrue(verifiedManifestWasReadable);
            Assert.Contains("\"channel\":\"dev\"", verifiedManifestText ?? string.Empty);
            Assert.IsTrue(Directory.Exists(workRoot));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(workRoot));
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
        var desiredFile = Path.Combine(tempDir, "desired-smb.json");
        var dataRoot = Path.Combine(tempDir, "data");
        var familyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var familyPath = Path.Combine(dataRoot, "families", familyId.ToString("N"));
        var credentialFile = Path.Combine(credentialDir, "queued.json");
        var runner = new RecordingCommandRunner();

        try
        {
            _ = Directory.CreateDirectory(stateDir);
            _ = Directory.CreateDirectory(credentialDir);
            _ = Directory.CreateDirectory(familyPath);
            await File.WriteAllTextAsync(conf, "existing smb config\n");
            await File.WriteAllTextAsync(desiredFile, $$"""
                {
                  "shares": [
                    {
                      "id": "22222222-2222-2222-2222-222222222222",
                      "familyId": "{{familyId}}",
                      "name": "Family data",
                      "shareName": "family-data",
                      "path": "{{familyPath}}",
                      "readOnly": false,
                      "enabled": true
                    }
                  ],
                  "credentials": []
                }
                """);
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
                ("HOMEHARBOR_SMB_DESIRED_FILE", desiredFile),
                ("HOMEHARBOR_SMB_DATA_ROOT", dataRoot));

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
    public async Task ApplySmb_Validates_The_Temporary_Config_As_A_Positional_Testparm_Argument()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempDir, "samba");
        var credentialDir = Path.Combine(tempDir, "credentials");
        var conf = Path.Combine(stateDir, "smb.conf");
        var desiredFile = Path.Combine(tempDir, "desired-smb.json");
        var dataRoot = Path.Combine(tempDir, "data");
        var runner = new RecordingCommandRunner();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestsTask = ServeHttpResponsesAsync(
            listener,
            [
                new TestHttpResponse(HttpStatusCode.OK, "{\"shares\":[],\"credentials\":[]}"),
                new TestHttpResponse(HttpStatusCode.OK, "{}")
            ],
            timeout.Token);

        try
        {
            _ = Directory.CreateDirectory(stateDir);
            _ = Directory.CreateDirectory(dataRoot);
            await File.WriteAllTextAsync(desiredFile, """
                {
                  "shares": [],
                  "credentials": []
                }
                """);

            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_DRY_RUN", null),
                ("HOMEHARBOR_SMB_STATE_DIR", stateDir),
                ("HOMEHARBOR_SMB_CONF", conf),
                ("HOMEHARBOR_SMB_CREDENTIAL_DIR", credentialDir),
                ("HOMEHARBOR_SMB_DESIRED_FILE", desiredFile),
                ("HOMEHARBOR_SMB_DATA_ROOT", dataRoot),
                ("HOMEHARBOR_API_URL", $"http://127.0.0.1:{endpoint.Port}"),
                ("HOMEHARBOR_API_SOCKET", null),
                ("HOMEHARBOR_AUTOMATION_TOKEN_PATH", Path.Combine(tempDir, "missing.jwt")));

            var exitCode = await AgentProgram.RunAsync(["apply-smb"], runner, timeout.Token);
            var requests = await requestsTask;

            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(requests[0]?.StartsWith("GET /api/smb/reconcile/desired ", StringComparison.Ordinal) ?? false);
            Assert.IsTrue(requests[1]?.StartsWith("POST /api/smb/reconcile/result ", StringComparison.Ordinal) ?? false);
            var validations = runner.Calls.Where(call => call.FileName == "testparm").ToArray();
            Assert.HasCount(1, validations);
            var validation = validations.Single();
            Assert.HasCount(3, validation.Arguments);
            var temporaryConfig = validation.Arguments[2];
            CollectionAssert.AreEqual(
                new[] { "-s", "--suppress-prompt", temporaryConfig },
                validation.Arguments);
            Assert.AreEqual(stateDir, Path.GetDirectoryName(temporaryConfig));
            Assert.IsTrue(Path.GetFileName(temporaryConfig).StartsWith("smb.conf.", StringComparison.Ordinal));
            Assert.DoesNotContain(argument => argument.StartsWith("--configfile=", StringComparison.Ordinal), validation.Arguments);
            Assert.IsTrue(File.Exists(conf));
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
    public async Task ApplySmb_Propagates_Result_Failure_So_Systemd_Can_Retry()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-smb-result-retry-");
        var stateDir = Path.Combine(tempDir.FullName, "samba");
        var credentialDir = Path.Combine(tempDir.FullName, "credentials");
        var desiredFile = Path.Combine(tempDir.FullName, "desired-smb.json");
        var dataRoot = Path.Combine(tempDir.FullName, "data");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestsTask = ServeHttpResponsesAsync(
            listener,
            [
                new TestHttpResponse(HttpStatusCode.OK, "{\"shares\":[],\"credentials\":[]}"),
                new TestHttpResponse(HttpStatusCode.InternalServerError, "{\"error\":\"database unavailable\"}")
            ],
            timeout.Token);

        try
        {
            _ = Directory.CreateDirectory(stateDir);
            _ = Directory.CreateDirectory(dataRoot);
            await File.WriteAllTextAsync(desiredFile, "{\"shares\":[],\"credentials\":[]}", timeout.Token);

            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_DRY_RUN", null),
                ("HOMEHARBOR_SMB_STATE_DIR", stateDir),
                ("HOMEHARBOR_SMB_CONF", Path.Combine(stateDir, "smb.conf")),
                ("HOMEHARBOR_SMB_CREDENTIAL_DIR", credentialDir),
                ("HOMEHARBOR_SMB_DESIRED_FILE", desiredFile),
                ("HOMEHARBOR_SMB_DATA_ROOT", dataRoot),
                ("HOMEHARBOR_API_URL", $"http://127.0.0.1:{endpoint.Port}"),
                ("HOMEHARBOR_API_SOCKET", null),
                ("HOMEHARBOR_AUTOMATION_TOKEN_PATH", Path.Combine(tempDir.FullName, "missing.jwt")));

            var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
                AgentProgram.RunAsync(["apply-smb"], new RecordingCommandRunner(), timeout.Token));
            var requests = await requestsTask;

            Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);
            Assert.IsTrue(requests[0]?.StartsWith("GET /api/smb/reconcile/desired ", StringComparison.Ordinal) ?? false);
            Assert.IsTrue(requests[1]?.StartsWith("POST /api/smb/reconcile/result ", StringComparison.Ordinal) ?? false);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task RenderCaddyfile_Validates_With_The_Caddy_Identity_And_Writable_Home()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(tempDir, "caddy");
        var caddyfile = Path.Combine(stateDir, "Caddyfile");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestTask = ServeSingleHttpResponseAsync(
            listener,
            "homeharbor.local { respond \"ok\" 200 }\n",
            timeout.Token);
        var runner = new RecordingCommandRunner();

        try
        {
            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_CADDY_STATE_DIR", stateDir),
                ("HOMEHARBOR_CADDYFILE", caddyfile),
                ("HOMEHARBOR_API_URL", $"http://127.0.0.1:{endpoint.Port}"),
                ("HOMEHARBOR_API_SOCKET", null),
                ("HOMEHARBOR_AUTOMATION_TOKEN_PATH", Path.Combine(tempDir, "missing.jwt")));

            var exitCode = await AgentProgram.RunAsync(["render-caddyfile"], runner, timeout.Token);
            var requestLine = await requestTask;

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual("GET /api/networking/proxy/caddyfile HTTP/1.1", requestLine);
            var validationIndex = runner.Calls.FindIndex(call =>
                call.FileName == "runuser" && call.Arguments.Contains("validate", StringComparer.Ordinal));
            Assert.IsGreaterThanOrEqualTo(2, validationIndex);
            var temporaryConfig = caddyfile + ".new";
            CollectionAssert.AreEqual(
                new[] { "root:caddy", temporaryConfig },
                runner.Calls[validationIndex - 2].Arguments);
            Assert.AreEqual("chown", runner.Calls[validationIndex - 2].FileName);
            CollectionAssert.AreEqual(
                new[] { "0640", temporaryConfig },
                runner.Calls[validationIndex - 1].Arguments);
            Assert.AreEqual("chmod", runner.Calls[validationIndex - 1].FileName);
            CollectionAssert.AreEqual(
                new[]
                {
                    "-u", "caddy", "--", "env", "HOME=/var/lib/caddy",
                    "caddy", "validate", "--config", temporaryConfig
                },
                runner.Calls[validationIndex].Arguments);
            Assert.DoesNotContain(call =>
                call.FileName == "caddy" && call.Arguments.FirstOrDefault() == "validate", runner.Calls);
            Assert.IsTrue(File.Exists(caddyfile));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildValidatedSmbConfig_Rebuilds_Safe_Config_And_Rejects_Family_Path_Escape()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-smb-validation-");
        try
        {
            var dataRoot = Path.Combine(tempDir.FullName, "data");
            var familyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var familyPath = Path.Combine(dataRoot, "families", familyId.ToString("N"));
            _ = Directory.CreateDirectory(familyPath);
            var desired = JsonNode.Parse($$"""
                {
                  "shares": [
                    {
                      "id": "22222222-2222-2222-2222-222222222222",
                      "familyId": "{{familyId}}",
                      "name": "Family data",
                      "shareName": "family-data",
                      "path": "{{familyPath}}",
                      "readOnly": false,
                      "enabled": true
                    }
                  ],
                  "credentials": [
                    {
                      "id": "33333333-3333-3333-3333-333333333333",
                      "familyId": "{{familyId}}",
                      "shareId": "22222222-2222-2222-2222-222222222222",
                      "unixUser": "homeharbor-smb001",
                      "readOnly": false,
                      "enabled": true,
                      "revokedAt": null
                    }
                  ]
                }
                """)!;

            var config = AgentProgram.BuildValidatedSmbConfig(desired.ToJsonString(), dataRoot);
            Assert.Contains("path = " + familyPath, config);
            Assert.Contains("force user = homeharbor", config);
            Assert.Contains("valid users = homeharbor-smb001", config);
            Assert.IsFalse(config.Contains("include =", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(config.Contains("preexec", StringComparison.OrdinalIgnoreCase));

            desired["shares"]![0]!["path"] = "/";
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                AgentProgram.BuildValidatedSmbConfig(desired.ToJsonString(), dataRoot));
            Assert.Contains("does not match its family", ex.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplyContainers_DryRun_Does_Not_Write_Or_Delete_Quadlet_Files()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-agent-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(tempDir, "home");
        var quadletDir = Path.Combine(home, ".config", "containers", "systemd");
        var desiredFile = Path.Combine(tempDir, "desired-containers.json");
        var dataRoot = Path.Combine(tempDir, "data");
        var appRoot = Path.Combine(dataRoot, "apps", "11111111111111111111111111111111");
        const string image = "docker.io/library/alpine@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var quadlet = $$"""
            [Unit]
            Description=HomeHarbor container Test
            After=network-online.target
            Wants=network-online.target

            [Container]
            ContainerName=homeharbor-11111111111111111111111111111111
            Image={{image}}
            Pull=missing
            NoNewPrivileges=true
            UserNS=auto
            Volume="{{appRoot}}:/data:rw,U"

            [Service]
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target
            """ + "\n";
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
            _ = Directory.CreateDirectory(appRoot);
            await File.WriteAllTextAsync(keepFile, "[Container]\nImage=old\n");
            await File.WriteAllTextAsync(deleteFile, "[Container]\nImage=delete\n");
            await File.WriteAllTextAsync(desiredFile, $$"""
                [
                  {
                    "id": "11111111-1111-1111-1111-111111111111",
                    "familyId": "33333333-3333-3333-3333-333333333333",
                    "serviceName": "homeharbor-11111111111111111111111111111111",
                    "unitName": "homeharbor-11111111111111111111111111111111.service",
                    "quadletFile": "homeharbor-11111111111111111111111111111111.container",
                    "desiredState": "running",
                    "requestedAction": "none",
                    "definition": {
                      "name": "Test",
                      "image": "{{image}}",
                      "environment": {},
                      "ports": [],
                      "volumes": [
                        {
                          "hostPath": "{{appRoot}}",
                          "containerPath": "/data",
                          "readOnly": false
                        }
                      ],
                      "command": []
                    },
                    "quadlet": {{JsonSerializer.Serialize(quadlet)}}
                  },
                  {
                    "id": "22222222-2222-2222-2222-222222222222",
                    "serviceName": "homeharbor-22222222222222222222222222222222",
                    "unitName": "homeharbor-22222222222222222222222222222222.service",
                    "quadletFile": "homeharbor-22222222222222222222222222222222.container",
                    "desiredState": "deleted",
                    "requestedAction": "delete"
                  }
                ]
                """);

            using var environment = new TemporaryEnvironment(
                ("HOMEHARBOR_DRY_RUN", "1"),
                ("HOMEHARBOR_CONTAINER_HOME", home),
                ("HOMEHARBOR_QUADLET_DIR", quadletDir),
                ("HOMEHARBOR_CONTAINER_DATA_ROOT", dataRoot),
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
    public void BuildValidatedContainerQuadlet_Rejects_Raw_Service_Command_Injection()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-container-validation-");
        try
        {
            var item = ValidContainerDesiredItem(tempDir.FullName, out var dataRoot);
            item["quadlet"] = item["quadlet"]!.GetValue<string>() + "ExecStartPre=/bin/sh -c evil\n";
            using var document = JsonDocument.Parse(item.ToJsonString());

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                AgentProgram.BuildValidatedContainerQuadlet(document.RootElement, dataRoot));

            Assert.Contains("does not match", ex.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void BuildValidatedContainerQuadlet_Rejects_Host_Volume_And_PodmanArgs()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-container-validation-");
        try
        {
            var hostVolume = ValidContainerDesiredItem(tempDir.FullName, out var dataRoot);
            hostVolume["definition"]!["volumes"]![0]!["hostPath"] = "/";
            using (var document = JsonDocument.Parse(hostVolume.ToJsonString()))
            {
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    AgentProgram.BuildValidatedContainerQuadlet(document.RootElement, dataRoot));
                Assert.Contains("exact app data root", ex.Message);
            }

            var podmanArgs = ValidContainerDesiredItem(tempDir.FullName, out dataRoot);
            podmanArgs["definition"]!["podmanArgs"] = "--privileged --network=host";
            using (var document = JsonDocument.Parse(podmanArgs.ToJsonString()))
            {
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    AgentProgram.BuildValidatedContainerQuadlet(document.RootElement, dataRoot));
                Assert.Contains("forbidden property: podmanArgs", ex.Message);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void BuildValidatedContainerQuadlet_Rejects_Unpinned_Image()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-container-validation-");
        try
        {
            var item = ValidContainerDesiredItem(tempDir.FullName, out var dataRoot);
            item["definition"]!["image"] = "docker.io/library/alpine:latest";
            using var document = JsonDocument.Parse(item.ToJsonString());

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                AgentProgram.BuildValidatedContainerQuadlet(document.RootElement, dataRoot));

            Assert.Contains("pinned", ex.Message);

            item = ValidContainerDesiredItem(tempDir.FullName, out dataRoot);
            item["definition"]!["image"] =
                "registry.example:5000/library/alpine:latest@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            using var taggedDocument = JsonDocument.Parse(item.ToJsonString());
            var taggedEx = Assert.ThrowsExactly<InvalidOperationException>(() =>
                AgentProgram.BuildValidatedContainerQuadlet(taggedDocument.RootElement, dataRoot));
            Assert.Contains("pinned", taggedEx.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void BuildValidatedContainerQuadlet_Does_Not_Autostart_Stopped_Container()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-container-validation-");
        try
        {
            var item = ValidContainerDesiredItem(tempDir.FullName, out var dataRoot);
            item["desiredState"] = "stopped";
            item["quadlet"] = item["quadlet"]!.GetValue<string>().Replace(
                "\n\n[Install]\nWantedBy=default.target\n",
                "\n",
                StringComparison.Ordinal);
            using var document = JsonDocument.Parse(item.ToJsonString());

            var quadlet = AgentProgram.BuildValidatedContainerQuadlet(document.RootElement, dataRoot);

            Assert.IsFalse(quadlet.Contains("[Install]", StringComparison.Ordinal));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void BuildValidatedContainerQuadlet_Binds_Published_Ports_Only_To_Loopback()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-container-validation-");
        try
        {
            var item = ValidContainerDesiredItem(tempDir.FullName, out var dataRoot);
            item["definition"]!["ports"] = new JsonArray
            {
                new JsonObject
                {
                    ["hostPort"] = 8443,
                    ["targetPort"] = 443,
                    ["protocol"] = "tcp"
                },
                new JsonObject
                {
                    ["hostPort"] = 5353,
                    ["targetPort"] = 53,
                    ["protocol"] = "udp"
                }
            };
            item["quadlet"] = item["quadlet"]!.GetValue<string>().Replace(
                "UserNS=auto\n",
                "UserNS=auto\nPublishPort=127.0.0.1:8443:443\nPublishPort=127.0.0.1:5353:53/udp\n",
                StringComparison.Ordinal);
            using var document = JsonDocument.Parse(item.ToJsonString());

            var quadlet = AgentProgram.BuildValidatedContainerQuadlet(document.RootElement, dataRoot);

            Assert.Contains("PublishPort=127.0.0.1:8443:443\n", quadlet);
            Assert.Contains("PublishPort=127.0.0.1:5353:53/udp\n", quadlet);
            Assert.DoesNotContain("PublishPort=8443:443", quadlet);
            Assert.DoesNotContain("PublishPort=5353:53/udp", quadlet);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void BuildValidatedContainerQuadlet_Uses_Auto_Userns_And_U_Ownership_Mount()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-container-validation-");
        try
        {
            var item = ValidContainerDesiredItem(tempDir.FullName, out var dataRoot);
            using var document = JsonDocument.Parse(item.ToJsonString());

            var quadlet = AgentProgram.BuildValidatedContainerQuadlet(document.RootElement, dataRoot);

            Assert.Contains("UserNS=auto\n", quadlet);
            Assert.Contains(":/data:rw,U\"\n", quadlet);
            Assert.DoesNotContain("keep-groups", quadlet);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ContainerAppRoot_Metadata_Requires_Root_Parent_And_Container_Namespace_Owners()
    {
        var parentMetadata = AgentProgram.ParseContainerPathMetadata("0:0:711\n", "/data/apps");
        Assert.AreEqual(0711, parentMetadata.Mode);
        AgentProgram.ValidateContainerAppsRootMetadata(parentMetadata);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.ValidateContainerAppsRootMetadata(new AgentProgram.ContainerPathMetadata(1000, 1000, 0770)));

        var uidRanges = AgentProgram.ParseSubIdRanges(
            "other:100000:65536\nhomeharbor-containers:200000:65536\n",
            "homeharbor-containers",
            "subuid");
        var gidRanges = AgentProgram.ParseSubIdRanges(
            "homeharbor-containers:300000:65536\n",
            "homeharbor-containers",
            "subgid");

        AgentProgram.ValidateContainerAppRootOwnership(
            new AgentProgram.ContainerPathMetadata(200123, 300123, 0700),
            990,
            991,
            uidRanges,
            gidRanges);
        AgentProgram.ValidateContainerAppRootOwnership(
            new AgentProgram.ContainerPathMetadata(990, 991, 0700),
            990,
            991,
            uidRanges,
            gidRanges);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.ValidateContainerAppRootOwnership(
                new AgentProgram.ContainerPathMetadata(1234, 300123, 0700),
                990,
                991,
                uidRanges,
                gidRanges));
    }

    [TestMethod]
    public void ContainerAppRoot_SubId_Parser_Fails_Closed()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.ParseSubIdRanges(string.Empty, "homeharbor-containers", "subuid"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.ParseSubIdRanges(
                "homeharbor-containers:200000:100\nhomeharbor-containers:200050:100\n",
                "homeharbor-containers",
                "subuid"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentProgram.ParseSubIdRanges(
                "homeharbor-containers:invalid:65536\n",
                "homeharbor-containers",
                "subuid"));
    }

    [TestMethod]
    public void ContainerQuadletNeedsUpdate_Detects_User_Namespace_Policy_Migration()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "[Container]\nVolume=/old:/data:rw\n");

            Assert.IsTrue(AgentProgram.ContainerQuadletNeedsUpdate(
                tempFile,
                "[Container]\nUserNS=auto\nVolume=/new:/data:rw,U\n"));
            File.WriteAllText(tempFile, "[Container]\nUserNS=auto\nVolume=/new:/data:rw,U\n");
            Assert.IsFalse(AgentProgram.ContainerQuadletNeedsUpdate(
                tempFile,
                "[Container]\nUserNS=auto\nVolume=/new:/data:rw,U\n"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void ValidateSystemAppCommands_Requires_Executable_Regular_File_Inside_Version()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-system-app-command-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "usr", "bin"));
            var command = Path.Combine(bin.FullName, "worker");
            File.WriteAllText(command, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            AgentProgram.ValidateSystemAppCommands(tempDir.FullName, ["worker"]);

            File.Delete(command);
            _ = File.CreateSymbolicLink(command, "/bin/sh");
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                AgentProgram.ValidateSystemAppCommands(tempDir.FullName, ["worker"]));
            Assert.Contains("symbolic link", ex.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
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

    private static RecordingCommandRunner StorageIdentityRunner(
        string serial,
        string wwn,
        long sizeBytes)
        => new((fileName, args, _) => fileName switch
        {
            "test" when args.SequenceEqual(["-b", "/dev/sdb"]) =>
                new CommandResult(0, string.Empty, string.Empty, fileName),
            "readlink" when args.SequenceEqual(["-f", "/dev/sdb"]) =>
                new CommandResult(0, "/dev/sdb\n", string.Empty, fileName),
            "lsblk" when args.LastOrDefault() == "/dev/sdb" =>
                new CommandResult(0, $$"""
                    {
                      "blockdevices": [
                        {
                          "path": "/dev/sdb",
                          "size": {{sizeBytes}},
                          "type": "disk",
                          "model": "Test Disk",
                          "serial": "{{serial}}",
                          "wwn": "{{wwn}}",
                          "tran": "sata",
                          "partuuid": null
                        }
                      ]
                    }
                    """, string.Empty, fileName),
            _ => new CommandResult(1, string.Empty, "unexpected command", fileName)
        });

    private static JsonObject ValidContainerDesiredItem(string tempDir, out string dataRoot)
    {
        const string id = "11111111-1111-1111-1111-111111111111";
        const string familyId = "33333333-3333-3333-3333-333333333333";
        const string serviceName = "homeharbor-11111111111111111111111111111111";
        const string image = "docker.io/library/alpine@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        dataRoot = Path.Combine(tempDir, "data");
        var appRoot = Path.Combine(dataRoot, "apps", "11111111111111111111111111111111");
        _ = Directory.CreateDirectory(appRoot);
        var quadlet = string.Join('\n',
        [
            "[Unit]",
            "Description=HomeHarbor container Test",
            "After=network-online.target",
            "Wants=network-online.target",
            "",
            "[Container]",
            "ContainerName=" + serviceName,
            "Image=" + image,
            "Pull=missing",
            "NoNewPrivileges=true",
            "UserNS=auto",
            "Volume=\"" + appRoot + ":/data:rw,U\"",
            "",
            "[Service]",
            "Restart=on-failure",
            "RestartSec=5",
            "",
            "[Install]",
            "WantedBy=default.target",
            ""
        ]);

        return new JsonObject
        {
            ["id"] = id,
            ["familyId"] = familyId,
            ["serviceName"] = serviceName,
            ["unitName"] = serviceName + ".service",
            ["quadletFile"] = serviceName + ".container",
            ["desiredState"] = "running",
            ["requestedAction"] = "none",
            ["definition"] = new JsonObject
            {
                ["name"] = "Test",
                ["image"] = image,
                ["environment"] = new JsonObject(),
                ["ports"] = new JsonArray(),
                ["volumes"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["hostPath"] = appRoot,
                        ["containerPath"] = "/data",
                        ["readOnly"] = false
                    }
                },
                ["command"] = new JsonArray()
            },
            ["quadlet"] = quadlet
        };
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

    private static async Task<string?> ServeSingleHttpResponseAsync(
        TcpListener listener,
        string body,
        CancellationToken cancellationToken)
    {
        var requests = await ServeHttpResponsesAsync(
            listener,
            [new TestHttpResponse(HttpStatusCode.OK, body, "text/plain; charset=utf-8")],
            cancellationToken);
        return requests.Single();
    }

    private static async Task<IReadOnlyList<string?>> ServeHttpResponsesAsync(
        TcpListener listener,
        IReadOnlyList<TestHttpResponse> responses,
        CancellationToken cancellationToken)
    {
        var requestLines = new List<string?>(responses.Count);
        foreach (var response in responses)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            requestLines.Add(await reader.ReadLineAsync(cancellationToken));
            var contentLength = 0;
            while (true)
            {
                var header = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(header))
                {
                    break;
                }

                const string contentLengthPrefix = "Content-Length:";
                if (header.StartsWith(contentLengthPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(header[contentLengthPrefix.Length..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            while (contentLength > 0)
            {
                var buffer = new char[Math.Min(contentLength, 4096)];
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                contentLength -= read;
            }

            var payload = Encoding.UTF8.GetBytes(response.Body);
            var reasonPhrase = response.StatusCode switch
            {
                HttpStatusCode.OK => "OK",
                HttpStatusCode.Conflict => "Conflict",
                HttpStatusCode.Unauthorized => "Unauthorized",
                HttpStatusCode.Forbidden => "Forbidden",
                HttpStatusCode.InternalServerError => "Internal Server Error",
                HttpStatusCode.ServiceUnavailable => "Service Unavailable",
                _ => response.StatusCode.ToString()
            };
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)response.StatusCode} {reasonPhrase}\r\n" +
                "Content-Type: " + response.ContentType + "\r\n" +
                "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        return requestLines;
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

    private sealed record TestHttpResponse(
        HttpStatusCode StatusCode,
        string Body,
        string ContentType = "application/json; charset=utf-8");

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
