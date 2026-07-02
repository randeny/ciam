using System.Collections.Concurrent;

namespace OpenIdServer.Saml;

/// <summary>
/// Tracks already-consumed SAML assertion IDs to prevent replay of a captured assertion.
/// </summary>
public interface IReplayCache
{
    /// <summary>
    /// Atomically registers an assertion ID. Returns <c>true</c> if the ID was seen for the
    /// first time, or <c>false</c> if it has already been consumed (a replay).
    /// </summary>
    bool TryRegister(string assertionId, DateTimeOffset expiresAt);
}

/// <summary>
/// Process-local, thread-safe replay cache. Suitable for a single-instance app.
/// For multi-instance deployments back this with a distributed store (e.g. Redis or Table Storage).
/// </summary>
public sealed class InMemoryReplayCache : IReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep = DateTimeOffset.UtcNow;

    public bool TryRegister(string assertionId, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrEmpty(assertionId))
        {
            // No usable ID to track; treat as non-replayable rather than blocking the flow.
            return true;
        }

        Sweep();

        return _seen.TryAdd(assertionId, expiresAt);
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextSweep)
        {
            return;
        }

        _nextSweep = now.AddMinutes(5);
        foreach (var entry in _seen)
        {
            if (entry.Value <= now)
            {
                _seen.TryRemove(entry.Key, out _);
            }
        }
    }
}
