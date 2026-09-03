using System.Collections.Concurrent;
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
        public Action Abort = () => { };
    }

    private readonly ConcurrentDictionary<string, OwnerEntry> _owners = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, TrackedRequest>> _active = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _killedUsers = new();
    private readonly ILogger<StreamKillRegistry> _logger;

    public StreamKillRegistry(ILogger<StreamKillRegistry> logger)
    {
        _logger = logger;
    }

    // Diagnostic only, deliberately NOT on IStreamKillRegistry: nothing in the plugin's runtime
    // path reads it. It exists so the unbounded-growth guarantee in the plan's global constraints
    // is actually assertable -- an emptied-but-retained _active bucket has no other observable
    // effect, which is exactly why the leak went unnoticed.
    public int ActiveBucketCount => _active.Count;

    public void RecordPlaySessionOwner(string playSessionId, Guid userId, DateTimeOffset nowUtc)
    {
        _owners[playSessionId] = new OwnerEntry { UserId = userId, LastSeenUtc = nowUtc };
    }

    public void TouchPlaySession(string playSessionId, DateTimeOffset nowUtc)
    {
        // Re-stamping on use is what makes the 2-hour prune cutoff mean "2 hours since we last
        // saw this playback" (matching SessionEnforcementState's semantics) rather than "2 hours
        // since playback started" -- the latter silently un-enforced any movie longer than the
        // cutoff, because the owner entry and its abort handle were pruned mid-stream.
        if (_owners.TryGetValue(playSessionId, out var entry))
        {
            entry.LastSeenUtc = nowUtc;
        }
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
        bucket[trackingId] = new TrackedRequest { Abort = abort };
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
        _killedUsers[userId] = nowUtc;

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

    public void ClearUser(Guid userId)
    {
        _killedUsers.TryRemove(userId, out _);
    }

    public bool IsPlaySessionKilled(string playSessionId) =>
        _owners.TryGetValue(playSessionId, out var entry) && _killedUsers.ContainsKey(entry.UserId);

    public void PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        foreach (var kvp in _owners.Where(kvp => kvp.Value.LastSeenUtc < cutoffUtc).ToList())
        {
            // Never evict an entry that still has a live tracked request. A DirectPlay stream can
            // be a single HTTP request lasting the whole movie, so it never re-enters the
            // streaming middleware to be re-stamped by TouchPlaySession -- pruning it would drop
            // both the ownership mapping and the abort handle, silently un-enforcing that stream.
            if (HasLiveRequests(kvp.Key))
            {
                continue;
            }

            _owners.TryRemove(kvp.Key, out _);
            _active.TryRemove(kvp.Key, out _);
        }

        // _active buckets are created for EVERY streaming request carrying a playSessionId, owned
        // or not (auth resolution failed, the client skipped PlaybackInfo, the mapping predates a
        // restart). UntrackActiveRequest empties the inner dictionary but leaves the outer key
        // behind, so without this sweep those keys accumulate forever.
        foreach (var kvp in _active.Where(kvp => kvp.Value.IsEmpty && !_owners.ContainsKey(kvp.Key)).ToList())
        {
            _active.TryRemove(kvp.Key, out _);
        }

        foreach (var kvp in _killedUsers.Where(kvp => kvp.Value < cutoffUtc).ToList())
        {
            _killedUsers.TryRemove(kvp.Key, out _);
        }
    }

    private bool HasLiveRequests(string playSessionId) =>
        _active.TryGetValue(playSessionId, out var bucket) && !bucket.IsEmpty;
}
