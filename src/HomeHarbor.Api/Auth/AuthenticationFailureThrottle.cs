using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace HomeHarbor.Api.Auth;

public sealed class AuthenticationFailureThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);
    private const int MaximumAccountFailures = 10;
    private const int MaximumClientFailures = 50;
    private const int MaximumAccountEntries = 3072;
    private const int MaximumClientEntries = 1024;

    private readonly ConcurrentDictionary<string, FailureState> _failures = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private int _operations;

    public AuthenticationFailureThrottle() : this(TimeProvider.System)
    {
    }

    internal AuthenticationFailureThrottle(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool TryAcquire(string realm, string identifier, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        CleanupPeriodically();
        var key = Key(realm, identifier);
        if (!_failures.TryGetValue(key, out var state)) return true;

        var now = _timeProvider.GetUtcNow();
        if (state.BlockedUntil is { } blockedUntil && blockedUntil > now)
        {
            retryAfter = blockedUntil - now;
            return false;
        }

        if (now - state.WindowStarted >= Window)
            _ = _failures.TryRemove(key, out _);
        return true;
    }

    public void RecordFailure(string realm, string identifier)
    {
        CleanupPeriodically();
        var key = Key(realm, identifier);
        var clientBucket = IsClientRealm(realm);
        EnsureCapacityFor(key, clientBucket);
        var now = _timeProvider.GetUtcNow();
        _ = _failures.AddOrUpdate(
            key,
            _ => new FailureState(now, 1, null, now, clientBucket),
            (_, current) =>
            {
                if (now - current.WindowStarted >= Window)
                    return new FailureState(now, 1, null, now, clientBucket);

                var count = current.Count + 1;
                return new FailureState(
                    current.WindowStarted,
                    count,
                    count >= FailureLimit(realm) ? now.Add(BlockDuration) : current.BlockedUntil,
                    now,
                    clientBucket);
            });
    }

    public void RecordSuccess(string realm, string identifier)
        => _ = _failures.TryRemove(Key(realm, identifier), out _);

    internal int EntryCount => _failures.Count;

    private void CleanupPeriodically()
    {
        if (Interlocked.Increment(ref _operations) % 128 != 0) return;
        var cutoff = _timeProvider.GetUtcNow() - Window - BlockDuration;
        foreach (var entry in _failures)
        {
            if (entry.Value.LastSeen < cutoff)
                _ = _failures.TryRemove(entry.Key, out _);
        }
    }

    private void EnsureCapacityFor(string key, bool clientBucket)
    {
        if (_failures.ContainsKey(key)) return;

        var capacity = clientBucket ? MaximumClientEntries : MaximumAccountEntries;
        var categoryCount = _failures.Count(entry => entry.Value.ClientBucket == clientBucket);
        if (categoryCount < capacity) return;

        KeyValuePair<string, FailureState>? oldest = null;
        foreach (var entry in _failures)
        {
            if (entry.Value.ClientBucket != clientBucket) continue;
            if (oldest is null || entry.Value.LastSeen < oldest.Value.Value.LastSeen) oldest = entry;
        }
        if (oldest is { } candidate)
            _ = _failures.TryRemove(candidate.Key, out _);
    }

    private static int FailureLimit(string realm)
        => IsClientRealm(realm) ? MaximumClientFailures : MaximumAccountFailures;

    private static bool IsClientRealm(string realm)
        => realm.EndsWith("-client", StringComparison.Ordinal);

    private static string Key(string realm, string identifier)
    {
        var canonical = $"{realm.Trim().ToUpperInvariant()}\0{identifier.Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record FailureState(
        DateTimeOffset WindowStarted,
        int Count,
        DateTimeOffset? BlockedUntil,
        DateTimeOffset LastSeen,
        bool ClientBucket);
}
