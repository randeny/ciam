using System.Collections.Concurrent;

namespace OpenIdServer.Saml;

/// <summary>
/// Short-lived, server-side store that holds a processed <see cref="SamlValidationResult"/>
/// between the ACS HTTP-POST and the follow-up GET that renders the claims reflector.
/// Enables a Post-Redirect-Get flow so the result page is refresh-safe and shown on a clean URL.
/// </summary>
public interface ISamlResultStore
{
    /// <summary>Stores a result and returns an opaque id used to retrieve it once.</summary>
    string Store(SamlValidationResult result, TimeSpan ttl);

    /// <summary>Atomically retrieves and removes a stored result, or <c>null</c> if missing/expired.</summary>
    SamlValidationResult? Take(string id);
}

/// <summary>
/// Process-local, thread-safe implementation. Suitable for a single-instance app.
/// For multi-instance deployments back this with a distributed store (e.g. Redis or Table Storage).
/// </summary>
public sealed class InMemorySamlResultStore : ISamlResultStore
{
    private readonly ConcurrentDictionary<string, (SamlValidationResult Result, DateTimeOffset ExpiresAt)> _items =
        new(StringComparer.Ordinal);

    public string Store(SamlValidationResult result, TimeSpan ttl)
    {
        Sweep();
        var id = Guid.NewGuid().ToString("N");
        _items[id] = (result, DateTimeOffset.UtcNow.Add(ttl));
        return id;
    }

    public SamlValidationResult? Take(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (_items.TryRemove(id, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return entry.Result;
        }

        return null;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _items)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _items.TryRemove(entry.Key, out _);
            }
        }
    }
}
