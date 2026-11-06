using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Scoping;

public sealed record ObservedRequest(string KeyName, string Method, string Template, long Count, long Denied, DateTimeOffset LastSeen);

// In-memory list of what each API key calls (key name, method, path template), the raw material for
// allowlists. Cleared on restart; never holds tokens or query strings.
public sealed class ObservedRequests
{
    internal const int Capacity = 2000;

    private readonly ConcurrentDictionary<(string Key, string Method, string Template), Entry> _entries = new();
    private readonly TimeProvider _timeProvider;

    public ObservedRequests(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    // Returns true the first time a (key, method, template) combination is seen.
    public bool Record(string keyName, string method, string template, bool denied)
    {
        var id = (keyName, method.ToUpperInvariant(), template);
        var isNew = false;
        if (!_entries.TryGetValue(id, out var entry))
        {
            if (_entries.Count >= Capacity)
            {
                return false;
            }

            entry = _entries.GetOrAdd(id, _ =>
            {
                isNew = true;
                return new Entry();
            });
        }

        Interlocked.Increment(ref entry.Count);
        if (denied)
        {
            Interlocked.Increment(ref entry.Denied);
        }

        Interlocked.Exchange(ref entry.LastSeenTicks, _timeProvider.GetUtcNow().UtcTicks);
        return isNew;
    }

    public IReadOnlyList<ObservedRequest> Snapshot()
        => _entries
            .Select(e => new ObservedRequest(e.Key.Key, e.Key.Method, e.Key.Template, Interlocked.Read(ref e.Value.Count), Interlocked.Read(ref e.Value.Denied), new DateTimeOffset(e.Value.LastSeenTicks, TimeSpan.Zero)))
            .OrderBy(e => e.KeyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Template, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();

    private sealed class Entry
    {
        public long Count;
        public long Denied;
        public long LastSeenTicks;
    }
}
