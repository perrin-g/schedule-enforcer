using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

// Keyed by (UserId, DeviceId, WindowEndUtc) -- NOT SessionId -- because Jellyfin mints a new
// SessionId on every reconnect, which would otherwise silently reset the one-shot warn/finalize
// flags on every disconnect/reconnect (spec: State keying). Cleanup is primarily age-based
// (PruneOlderThan, called every tick in Task 6) rather than tied to a SessionEnded event, because
// SessionEnded can race with a concurrent tick re-adding the same key on a different thread.
public class SessionEnforcementState : ISessionEnforcementState
{
    private sealed class Entry
    {
        public readonly object Lock = new();
        public bool Warned;
        public bool Finalized;
        public bool Notified;
        public DateTimeOffset LastSeenUtc;
    }

    private readonly ConcurrentDictionary<(Guid UserId, string DeviceId, DateTimeOffset WindowEndUtc), Entry> _entries = new();

    public bool TryMarkWarned(Guid userId, string deviceId, DateTimeOffset windowEndUtc, DateTimeOffset nowUtc) =>
        TryMark(userId, deviceId, windowEndUtc, nowUtc, e => e.Warned, (e, v) => e.Warned = v);

    public bool TryMarkFinalized(Guid userId, string deviceId, DateTimeOffset windowEndUtc, DateTimeOffset nowUtc) =>
        TryMark(userId, deviceId, windowEndUtc, nowUtc, e => e.Finalized, (e, v) => e.Finalized = v);

    public bool TryMarkNotified(Guid userId, string deviceId, DateTimeOffset windowEndUtc, DateTimeOffset nowUtc) =>
        TryMark(userId, deviceId, windowEndUtc, nowUtc, e => e.Notified, (e, v) => e.Notified = v);

    public void PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        foreach (var kvp in _entries.ToList())
        {
            lock (kvp.Value.Lock)
            {
                if (kvp.Value.LastSeenUtc < cutoffUtc)
                {
                    _entries.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    private bool TryMark(
        Guid userId,
        string deviceId,
        DateTimeOffset windowEndUtc,
        DateTimeOffset nowUtc,
        Func<Entry, bool> getFlag,
        Action<Entry, bool> setFlag)
    {
        var key = (userId, deviceId, windowEndUtc);
        var entry = _entries.GetOrAdd(key, _ => new Entry());

        lock (entry.Lock)
        {
            entry.LastSeenUtc = nowUtc;
            if (getFlag(entry))
            {
                return false;
            }

            setFlag(entry, true);
            return true;
        }
    }
}
