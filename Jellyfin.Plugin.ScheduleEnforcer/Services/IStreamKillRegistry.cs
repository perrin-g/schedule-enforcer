namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

public interface IStreamKillRegistry
{
    void RecordPlaySessionOwner(string playSessionId, Guid userId, DateTimeOffset nowUtc);

    // Refreshes an owner entry's LastSeenUtc so a long-running playback is never aged out
    // mid-stream. Called from the streaming middleware on every matched request (Transcode/HLS
    // re-enters per segment); no-op for an unknown playSessionId. DirectPlay, which may never
    // re-enter the middleware, is covered instead by PruneOlderThan never evicting an entry with
    // a live tracked request.
    void TouchPlaySession(string playSessionId, DateTimeOffset nowUtc);

    bool TryGetOwner(string playSessionId, out Guid userId);

    void TrackActiveRequest(string playSessionId, Guid trackingId, Action abort);

    void UntrackActiveRequest(string playSessionId, Guid trackingId);

    void KillUser(Guid userId, DateTimeOffset nowUtc);

    // Clears a standing kill for a user. Called every tick a user is inside a covering
    // AccessSchedule window, so a second window opening within the prune cutoff of the first
    // window's end can never be blocked on sight by the previous window's kill.
    void ClearUser(Guid userId);

    bool IsPlaySessionKilled(string playSessionId);

    void PruneOlderThan(DateTimeOffset cutoffUtc);
}
