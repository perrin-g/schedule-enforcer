using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

// Two independent maps, both keyed by PlaySessionId (never SessionId -- Jellyfin mints a new
// SessionId on every reconnect, but a PlaySessionId is scoped to one playback attempt and a
// fresh one is minted by the client's next PlaybackInfo call, matching how
// SessionEnforcementState avoids keying on SessionId for the same reason).
public class StreamKillRegistry : IStreamKillRegistry
{
    private sealed class OwnerEntry
    {
        public Guid UserId;
        public DateTimeOffset LastSeenUtc;
    }

    private sealed class TrackedRequest
    {
        public Guid TrackingId;
        public Action Abort = () => { };
    }

    private readonly ConcurrentDictionary<string, OwnerEntry> _owners = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, TrackedRequest>> _active = new();
    private readonly ConcurrentDictionary<Guid, bool> _killedUsers = new();
    private readonly ILogger<StreamKillRegistry> _logger;

    public StreamKillRegistry(ILogger<StreamKillRegistry> logger)
    {
        _logger = logger;
    }

    public void RecordPlaySessionOwner(string playSessionId, Guid userId, DateTimeOffset nowUtc)
    {
        _owners[playSessionId] = new OwnerEntry { UserId = userId, LastSeenUtc = nowUtc };
    }

    public bool TryGetOwner(string playSessionId, out Guid userId)
    {
        if (_owners.TryGetValue(playSessionId, out var entry))
        {
            userId = entry.UserId;
            return true;
        }

        userId = Guid.Empty;
        return false;
    }

    public void TrackActiveRequest(string playSessionId, Guid trackingId, Action abort)
    {
        var bucket = _active.GetOrAdd(playSessionId, _ => new ConcurrentDictionary<Guid, TrackedRequest>());
        bucket[trackingId] = new TrackedRequest { TrackingId = trackingId, Abort = abort };
    }

    public void UntrackActiveRequest(string playSessionId, Guid trackingId)
    {
        if (_active.TryGetValue(playSessionId, out var bucket))
        {
            bucket.TryRemove(trackingId, out _);
        }
    }

    public void KillUser(Guid userId, DateTimeOffset nowUtc)
    {
        _killedUsers[userId] = true;

        // Only PlaySessionIds owned by this user, not a full scan of every active request --
        // owners can outnumber active requests once idle sessions accumulate before pruning.
        foreach (var playSessionId in _owners.Where(kvp => kvp.Value.UserId == userId).Select(kvp => kvp.Key).ToList())
        {
            if (!_active.TryGetValue(playSessionId, out var bucket))
            {
                continue;
            }

            foreach (var tracked in bucket.Values.ToList())
            {
                try
                {
                    tracked.Abort();
                }
                catch (Exception ex)
                {
                    // Fail-open, same principle as ScheduleEnforcerTask.ExecuteAsync's
                    // per-session catch: one bad abort must not block the others.
                    _logger.LogWarning(ex, "StreamKillRegistry: abort failed for one tracked request");
                }
            }
        }
    }

    public bool IsPlaySessionKilled(string playSessionId) =>
        _owners.TryGetValue(playSessionId, out var entry) && _killedUsers.ContainsKey(entry.UserId);

    public void PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        foreach (var kvp in _owners.Where(kvp => kvp.Value.LastSeenUtc < cutoffUtc).ToList())
        {
            _owners.TryRemove(kvp.Key, out _);
            _active.TryRemove(kvp.Key, out _);
        }
    }
}
