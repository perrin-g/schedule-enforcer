using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

// All boundary math happens in UTC (per spec: Timezone correctness / independent review item 9)
// -- StartHour/EndHour are local wall-clock hours, so each candidate boundary is built as a
// local DateTime and converted to UTC via TimeZoneInfo, never compared as raw local values.
public class ScheduleWindowCalculator : IScheduleWindowCalculator
{
    public ScheduleWindowResult GetCurrentWindow(IReadOnlyList<AccessSchedule> schedules, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        DateTimeOffset? latestCoveringEndUtc = null;

        foreach (var schedule in schedules)
        {
            foreach (var day in ResolveDays(schedule.DayOfWeek))
            {
                // Only today's and yesterday's occurrence of `day` can possibly cover "now":
                // a same-day window starts and ends within [StartHour, EndHour) today, and a
                // wraparound window (EndHour <= StartHour) that started yesterday can still be
                // covering the early hours of today.
                foreach (var candidateLocalDate in new[] { nowLocal.Date, nowLocal.Date.AddDays(-1) })
                {
                    if (candidateLocalDate.DayOfWeek != day)
                    {
                        continue;
                    }

                    var windowStartUtc = ToUtcSafe(candidateLocalDate.AddHours(schedule.StartHour), timeZone);
                    var windowEndLocal = candidateLocalDate.AddHours(schedule.EndHour);
                    if (schedule.EndHour <= schedule.StartHour)
                    {
                        windowEndLocal = windowEndLocal.AddDays(1);
                    }

                    var windowEndUtc = ToUtcSafe(windowEndLocal, timeZone);

                    if (nowUtc >= windowStartUtc && nowUtc < windowEndUtc)
                    {
                        if (latestCoveringEndUtc is null || windowEndUtc > latestCoveringEndUtc)
                        {
                            latestCoveringEndUtc = windowEndUtc;
                        }
                    }
                }
            }
        }

        return new ScheduleWindowResult
        {
            HasCoveringWindow = latestCoveringEndUtc is not null,
            WindowEndUtc = latestCoveringEndUtc
        };
    }

    // Converts a local wall-clock DateTime to UTC, handling the two DST edge cases that raw
    // TimeZoneInfo.ConvertTimeToUtc doesn't handle gracefully: a spring-forward gap (the local
    // time never occurred) is nudged forward past the gap; a fall-back ambiguity (the local time
    // occurred twice) resolves to the earlier/daylight offset, so a computed cutoff trends
    // slightly earlier rather than later -- consistent with this plugin's fail-safe-toward-an-
    // earlier-stop stance elsewhere in the design.
    private static DateTimeOffset ToUtcSafe(DateTime localUnspecified, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            // Compute the actual UTC instant for each candidate offset and take the earlier one
            // directly -- deliberately not "pick the smaller/larger offset", which inverts depending
            // on the sign convention (UTC = local - offset, so the SMALLER offset yields the LATER
            // UTC instant, not the earlier one). An earlier version picked
            // offsets.OrderBy(o => o).First() intending "earliest UTC instant" and got exactly this
            // backwards -- caught during Task 3's task review.
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            return offsets.Select(o => new DateTimeOffset(local, o).ToUniversalTime()).Min();
        }

        var offset = timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static IEnumerable<DayOfWeek> ResolveDays(DynamicDayOfWeek day) => day switch
    {
        DynamicDayOfWeek.Everyday => Enum.GetValues<DayOfWeek>(),
        DynamicDayOfWeek.Weekday => new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        DynamicDayOfWeek.Weekend => new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
        _ => new[] { (DayOfWeek)(int)day }
    };
}
