using HomeHarbor.Api.Auth;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class AuthenticationFailureThrottleTests
{
    [TestMethod]
    public void Blocks_After_Repeated_Failures_And_Success_Clears_Account_State()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new AuthenticationFailureThrottle(clock);
        for (var attempt = 0; attempt < 10; attempt++)
            throttle.RecordFailure("login", "client:owner");

        Assert.IsFalse(throttle.TryAcquire("login", "client:owner", out var retryAfter));
        Assert.IsGreaterThan(TimeSpan.Zero, retryAfter);

        throttle.RecordSuccess("login", "client:owner");

        Assert.IsTrue(throttle.TryAcquire("login", "client:owner", out _));
    }

    [TestMethod]
    public void Unique_Attacker_Identifiers_Do_Not_Grow_State_Without_Bound()
    {
        var throttle = new AuthenticationFailureThrottle();

        for (var index = 0; index < 20_000; index++)
            throttle.RecordFailure("login", $"attacker-{index}");

        Assert.IsLessThanOrEqualTo(4096, throttle.EntryCount);
        Assert.IsTrue(throttle.TryAcquire("login", "fresh-legitimate-identity", out _));
    }

    [TestMethod]
    public void Client_Bucket_Blocks_Username_Rotation()
    {
        var throttle = new AuthenticationFailureThrottle();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            throttle.RecordFailure("login-client", "192.0.2.1");
            throttle.RecordFailure("login", $"192.0.2.1:rotating-{attempt}");
        }

        Assert.IsFalse(throttle.TryAcquire("login-client", "192.0.2.1", out _));
        Assert.IsTrue(throttle.TryAcquire("login-client", "192.0.2.2", out _));
    }

    [TestMethod]
    public void Account_Identifier_Flood_Does_Not_Evict_Blocked_Client_Bucket()
    {
        var throttle = new AuthenticationFailureThrottle();
        for (var attempt = 0; attempt < 50; attempt++)
            throttle.RecordFailure("login-client", "192.0.2.1");

        for (var index = 0; index < 20_000; index++)
            throttle.RecordFailure("login", $"attacker-{index}");

        Assert.IsFalse(throttle.TryAcquire("login-client", "192.0.2.1", out _));
        Assert.IsLessThanOrEqualTo(4096, throttle.EntryCount);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
