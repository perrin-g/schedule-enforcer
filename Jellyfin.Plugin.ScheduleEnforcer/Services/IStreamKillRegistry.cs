using System;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

public interface IStreamKillRegistry
{
    void RecordPlaySessionOwner(string playSessionId, Guid userId, DateTimeOffset nowUtc);

    bool TryGetOwner(string playSessionId, out Guid userId);

    void TrackActiveRequest(string playSessionId, Guid trackingId, Action abort);

    void UntrackActiveRequest(string playSessionId, Guid trackingId);

    void KillUser(Guid userId, DateTimeOffset nowUtc);

    bool IsPlaySessionKilled(string playSessionId);

    void PruneOlderThan(DateTimeOffset cutoffUtc);
}
