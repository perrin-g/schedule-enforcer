using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

public interface IScheduleWindowCalculator
{
    ScheduleWindowResult GetCurrentWindow(IReadOnlyList<AccessSchedule> schedules, DateTimeOffset nowUtc, TimeZoneInfo timeZone);
}

public sealed class ScheduleWindowResult
{
    public bool HasCoveringWindow { get; init; }

    public DateTimeOffset? WindowEndUtc { get; init; }
}
