namespace HomeHarbor.Tests;

using HomeHarbor.Tooling;
using System.Buffers.Binary;
using System.Text;

[TestClass]
[DoNotParallelize]
public sealed class RecoverySecurityTests
{
    [TestMethod]
    public void FastbootUnlockGate_Uses_One_Time_Hashed_Token_And_Expires()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-fastboot-gate-" + Guid.NewGuid().ToString("N"));
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero));
        var gate = new FastbootUnlockGate(Path.Combine(tempDir, "unlocked"), clock);
        try
        {
            Assert.IsFalse(gate.IsUnlocked(out _));

            var grant = gate.Grant(TimeSpan.FromMinutes(10));
            Assert.AreEqual(64, grant.AuthorizationToken.Length);
            Assert.IsTrue(grant.AuthorizationToken.All(Uri.IsHexDigit));
            Assert.IsTrue(gate.IsUnlocked(out var remaining));
            Assert.AreEqual(TimeSpan.FromMinutes(10), remaining);
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(gate.Path));
            Assert.DoesNotContain(grant.AuthorizationToken, File.ReadAllText(gate.Path));

            var wrongToken = Encoding.ASCII.GetBytes(new string('0', 64));
            Assert.IsFalse(gate.TryAuthorizeSession(wrongToken, out _));
            Assert.IsFalse(gate.TryAuthorizeSession(new byte[65], out _));

            var token = Encoding.ASCII.GetBytes(grant.AuthorizationToken);
            Assert.IsTrue(gate.TryAuthorizeSession(token, out var authorization));
            Assert.IsNotNull(authorization);
            Assert.IsTrue(gate.IsSessionAuthorized(authorization, out remaining));
            Assert.AreEqual(TimeSpan.FromMinutes(10), remaining);
            Assert.IsFalse(gate.TryAuthorizeSession(token, out _), "the physical token must be single use");

            clock.Advance(TimeSpan.FromMinutes(11));
            Assert.IsFalse(gate.IsSessionAuthorized(authorization, out _));
            Assert.IsFalse(File.Exists(gate.Path));
            authorization.Dispose();
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
    public async Task FastbootActions_Requires_Physical_Gate_And_Per_Session_Authentication()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-fastboot-gate-" + Guid.NewGuid().ToString("N"));
        var gate = new FastbootUnlockGate(Path.Combine(tempDir, "unlocked"));
        var previousState = Environment.GetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", tempDir);
            var first = new FastbootActions(gate);
            var second = new FastbootActions(gate);

            Assert.AreNotEqual(first.DownloadPath, second.DownloadPath);
            Assert.AreEqual("HomeHarbor", first.GetVar("product"), "read-only getvar must work while locked");
            Assert.AreEqual("no", first.GetVar("unlocked"));
            var result = await first.FlashAsync("boot_a", CancellationToken.None);
            Assert.IsFalse(result.Ok);
            Assert.Contains("physical recovery console", result.Message);

            var grant = gate.Grant(TimeSpan.FromMinutes(10));
            Assert.AreEqual("no", first.GetVar("unlocked"), "a physical gate alone must not authorize a LAN client");
            result = await first.FlashAsync("boot_a", CancellationToken.None);
            Assert.IsFalse(result.Ok);
            Assert.Contains("not authorized", result.Message);

            var authentication = first.AuthenticateSession(Encoding.ASCII.GetBytes(grant.AuthorizationToken));
            Assert.IsTrue(authentication.Ok);
            Assert.AreEqual("yes", first.GetVar("unlocked"));
            Assert.AreEqual("no", second.GetVar("unlocked"));
            Assert.IsFalse(second.AuthenticateSession(Encoding.ASCII.GetBytes(grant.AuthorizationToken)).Ok,
                "another TCP session must not replay the one-time token");

            first.CleanupSession();
            Assert.IsFalse(gate.IsUnlocked(out _), "disconnecting the authenticated TCP session must revoke its grant");
            Assert.AreEqual("no", first.GetVar("unlocked"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", previousState);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void FastbootAuthorization_Regrant_And_Revoke_Invalidate_Existing_Session()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-fastboot-gate-" + Guid.NewGuid().ToString("N"));
        var gate = new FastbootUnlockGate(Path.Combine(tempDir, "unlocked"));
        var previousState = Environment.GetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", tempDir);
            var actions = new FastbootActions(gate);
            var firstGrant = gate.Grant(TimeSpan.FromMinutes(10));
            Assert.IsTrue(actions.AuthenticateSession(Encoding.ASCII.GetBytes(firstGrant.AuthorizationToken)).Ok);
            Assert.IsTrue(actions.DestructiveActionsAllowed(out _));

            var secondGrant = gate.Grant(TimeSpan.FromMinutes(10));
            Assert.IsFalse(actions.DestructiveActionsAllowed(out var failure));
            Assert.Contains("not authorized", failure);
            Assert.IsTrue(actions.AuthenticateSession(Encoding.ASCII.GetBytes(secondGrant.AuthorizationToken)).Ok);
            Assert.IsTrue(actions.DestructiveActionsAllowed(out _));

            gate.Revoke();
            Assert.IsFalse(actions.DestructiveActionsAllowed(out failure));
            Assert.Contains("locked", failure);
            actions.CleanupSession();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", previousState);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void FastbootAuthorization_Command_Is_Bounded_Redacted_And_Zeroed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-fastboot-gate-" + Guid.NewGuid().ToString("N"));
        var gate = new FastbootUnlockGate(Path.Combine(tempDir, "unlocked"));
        var previousState = Environment.GetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", tempDir);
            var actions = new FastbootActions(gate);
            var grant = gate.Grant(TimeSpan.FromMinutes(10));
            var command = Encoding.ASCII.GetBytes("oem auth " + grant.AuthorizationToken + "\n");

            Assert.IsTrue(FastbootTcpServer.TryHandleAuthorizationCommand(command, actions, out var status));
            Assert.IsTrue(status.Ok);
            Assert.IsTrue(command.All(value => value == 0), "the network buffer containing the token must be zeroed");
            Assert.AreEqual("oem", FastbootTcpServer.CommandNameForLog("oem auth " + grant.AuthorizationToken));
            Assert.AreEqual("flash", FastbootTcpServer.CommandNameForLog("flash:root_a"));
            Assert.AreEqual("unknown", FastbootTcpServer.CommandNameForLog("attacker " + grant.AuthorizationToken));

            var oversized = Encoding.ASCII.GetBytes("oem auth " + new string('a', 65));
            Assert.IsTrue(FastbootTcpServer.TryHandleAuthorizationCommand(oversized, new FastbootActions(gate), out status));
            Assert.IsFalse(status.Ok);
            Assert.IsTrue(oversized.All(value => value == 0));
            actions.CleanupSession();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", previousState);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task FastbootAuthorization_Revoke_Cancels_An_Operation_In_Progress()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-fastboot-gate-" + Guid.NewGuid().ToString("N"));
        var gate = new FastbootUnlockGate(Path.Combine(tempDir, "unlocked"));
        var previousState = Environment.GetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", tempDir);
            var actions = new FastbootActions(gate);
            var grant = gate.Grant(TimeSpan.FromMinutes(10));
            Assert.IsTrue(actions.AuthenticateSession(Encoding.ASCII.GetBytes(grant.AuthorizationToken)).Ok);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = actions.RunAuthorizedOperationAsync(
                async cancellationToken =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return FastbootStatus.Okay("unexpected");
                },
                CancellationToken.None);

            await started.Task;
            gate.Revoke();
            var status = await operation.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsFalse(status.Ok);
            Assert.Contains("revoked", status.Message);
            Assert.IsFalse(actions.DestructiveActionsAllowed(out _));
            actions.CleanupSession();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_RECOVERY_STATE_DIR", previousState);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProcessCommandRunner_Cancellation_Kills_The_Process_Tree()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-process-cancel-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(tempDir, "should-not-exist");
        try
        {
            _ = Directory.CreateDirectory(tempDir);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var runner = new ProcessCommandRunner();
            _ = await runner.RunAsync(
                "/bin/sh",
                ["-c", "sleep 1; printf done > \"$1\"", "sh", marker],
                cancellationToken: cancellation.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            Assert.IsFalse(File.Exists(marker), "a canceled command must not continue running in the background");
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
    public async Task FastbootAuthenticatedProxy_Authenticates_Before_Forwarding_A_Stock_Client()
    {
        var response = Encoding.ASCII.GetBytes("OKAYsession authorized");
        var input = new byte[4 + 8 + response.Length];
        "FB01"u8.CopyTo(input);
        BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(4, 8), (ulong)response.Length);
        response.CopyTo(input, 12);
        await using var stream = new ScriptedDuplexStream(input);
        var token = Encoding.ASCII.GetBytes(new string('a', 64));

        await FastbootAuthenticatedProxy.AuthenticateAsync(stream, token, CancellationToken.None);

        var output = stream.Written.ToArray();
        CollectionAssert.AreEqual("FB01"u8.ToArray(), output[..4]);
        var commandLength = BinaryPrimitives.ReadUInt64BigEndian(output.AsSpan(4, 8));
        Assert.AreEqual(73UL, commandLength);
        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("oem auth " + new string('a', 64)),
            output[12..]);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            FastbootAuthenticatedProxy.AuthenticateAsync(
                new ScriptedDuplexStream(input),
                Encoding.ASCII.GetBytes(new string('z', 64)),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task RecoveryPrivilegedAction_Allows_Only_Fixed_Physical_Requests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-recovery-action-" + Guid.NewGuid().ToString("N"));
        var request = Path.Combine(tempDir, "action.request");
        var esp = Path.Combine(tempDir, "esp");
        var runner = new RecordingRunner();
        try
        {
            _ = Directory.CreateDirectory(tempDir);
            BootState.Initialize(esp, "B", "B", "A");
            await File.WriteAllTextAsync(request, "normal\n");

            Assert.AreEqual(0, await RecoveryPrivilegedAction.ApplyAsync(request, runner, CancellationToken.None));
            Assert.IsFalse(File.Exists(request));
            Assert.AreEqual("B", BootState.Read(esp).DefaultSlot);
            Assert.AreEqual("B", BootState.Read(esp).DefaultRootSlot);
            CollectionAssert.AreEqual(new[] { "reboot" }, runner.Arguments);

            await File.WriteAllTextAsync(request, "shell /bin/sh\n");
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                RecoveryPrivilegedAction.ApplyAsync(request, runner, CancellationToken.None));
            Assert.IsTrue(File.Exists(request));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingRunner : ICommandRunner
    {
        public string[] Arguments { get; private set; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("systemctl", fileName);
            Arguments = arguments.ToArray();
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, fileName));
        }
    }

    private sealed class ScriptedDuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);

        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => Written.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                Written.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
