using System;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

public interface ISessionEnforcementState
{
    bool TryMarkWarned(Guid userId, string deviceId, DateTimeOffset windowEndUtc, DateTimeOffset nowUtc);

    bool TryMarkFinalized(Guid userId, string deviceId, DateTimeOffset windowEndUtc, DateTimeOffset nowUtc);

    bool TryMarkNotified(Guid userId, string deviceId, DateTimeOffset windowEndUtc, DateTimeOffset nowUtc);

    void PruneOlderThan(DateTimeOffset cutoffUtc);
}
