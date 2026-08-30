using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using Xunit;

namespace Jellyfin.Plugin.ScheduleEnforcer.Tests.Services;

public class ScheduleWindowCalculatorTests
{
    private static readonly TimeZoneInfo Auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
    private readonly ScheduleWindowCalculator _calculator = new();

    // AccessSchedule has no parameterless constructor -- confirmed via reflection against the
    // installed Jellyfin.Database.Implementations 10.11.11 package: the real signature is
    // AccessSchedule(DynamicDayOfWeek dayOfWeek, double startHour, double endHour, Guid userId).
    // The userId argument is irrelevant to GetCurrentWindow's pure logic, so a fresh Guid is fine.
    private static AccessSchedule Schedule(DynamicDayOfWeek day, double start, double end) =>
        new(day, start, end, Guid.NewGuid());

    [Fact]
    public void GetCurrentWindow_WithinSaturdayEveningWindow_ReturnsCoveringWindowEndingAt22()
    {
        // Saturday 2026-08-29 21:00 NZST (UTC+12) -- inside the real 19:00-22:00 window.
        var nowUtc = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
        var schedules = new List<AccessSchedule>
        {
            Schedule(DynamicDayOfWeek.Saturday, 13.0, 17.0),
            Schedule(DynamicDayOfWeek.Saturday, 19.0, 22.0),
        };

        var result = _calculator.GetCurrentWindow(schedules, nowUtc, Auckland);

        Assert.True(result.HasCoveringWindow);
        // 22:00 NZST 2026-08-29 == 10:00 UTC 2026-08-29
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero), result.WindowEndUtc);
    }

    [Fact]
    public void GetCurrentWindow_InGapBetweenTwoSaturdayWindows_ReturnsNoCoveringWindow()
    {
        // Saturday 18:00 NZST -- between the 13-17 and 19-22 windows.
        var nowUtc = new DateTimeOffset(2026, 8, 29, 6, 0, 0, TimeSpan.Zero);
        var schedules = new List<AccessSchedule>
        {
            Schedule(DynamicDayOfWeek.Saturday, 13.0, 17.0),
            Schedule(DynamicDayOfWeek.Saturday, 19.0, 22.0),
        };

        var result = _calculator.GetCurrentWindow(schedules, nowUtc, Auckland);

        Assert.False(result.HasCoveringWindow);
        Assert.Null(result.WindowEndUtc);
    }

    [Fact]
    public void GetCurrentWindow_WeekdayEnumMatchesMondayThroughFriday()
    {
        // Wednesday 2026-09-02 14:00 NZST -- inside a Weekday 13-17 rule.
        var nowUtc = new DateTimeOffset(2026, 9, 2, 2, 0, 0, TimeSpan.Zero);
        var schedules = new List<AccessSchedule> { Schedule(DynamicDayOfWeek.Weekday, 13.0, 17.0) };

        var result = _calculator.GetCurrentWindow(schedules, nowUtc, Auckland);

        Assert.True(result.HasCoveringWindow);
    }

    [Fact]
    public void GetCurrentWindow_WeekdayEnumExcludesSaturday()
    {
        var nowUtc = new DateTimeOffset(2026, 8, 29, 2, 0, 0, TimeSpan.Zero); // Saturday 14:00 NZST
        var schedules = new List<AccessSchedule> { Schedule(DynamicDayOfWeek.Weekday, 13.0, 17.0) };

        var result = _calculator.GetCurrentWindow(schedules, nowUtc, Auckland);

        Assert.False(result.HasCoveringWindow);
    }

    [Fact]
    public void GetCurrentWindow_MidnightWraparound_CoversPastMidnightPortion()
    {
        // Friday 19:00-01:00 wraparound window; check Saturday 00:30 NZST, which is the
        // carried-over portion from Friday's rule.
        var friday1900Utc = new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero); // Friday 19:00 NZST -- inside
        var saturday0030Utc = new DateTimeOffset(2026, 8, 28, 12, 30, 0, TimeSpan.Zero); // Saturday 00:30 NZST -- carried over
        var schedules = new List<AccessSchedule> { Schedule(DynamicDayOfWeek.Friday, 19.0, 1.0) };

        var duringEvening = _calculator.GetCurrentWindow(schedules, friday1900Utc, Auckland);
        var pastMidnight = _calculator.GetCurrentWindow(schedules, saturday0030Utc, Auckland);

        Assert.True(duringEvening.HasCoveringWindow);
        Assert.True(pastMidnight.HasCoveringWindow);
        // Window end is Saturday 01:00 NZST == Friday 13:00 UTC (UTC+12 offset).
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 13, 0, 0, TimeSpan.Zero), pastMidnight.WindowEndUtc);
    }

    [Fact]
    public void GetCurrentWindow_AfterWraparoundWindowEnds_ReturnsNoCoveringWindow()
    {
        var saturday0130Utc = new DateTimeOffset(2026, 8, 28, 13, 30, 0, TimeSpan.Zero); // Saturday 01:30 NZST -- after 01:00 end
        var schedules = new List<AccessSchedule> { Schedule(DynamicDayOfWeek.Friday, 19.0, 1.0) };

        var result = _calculator.GetCurrentWindow(schedules, saturday0130Utc, Auckland);

        Assert.False(result.HasCoveringWindow);
    }

    [Fact]
    public void GetCurrentWindow_EmptySchedules_ReturnsNoCoveringWindow()
    {
        var result = _calculator.GetCurrentWindow(new List<AccessSchedule>(), DateTimeOffset.UtcNow, Auckland);

        Assert.False(result.HasCoveringWindow);
    }

    [Fact]
    public void GetCurrentWindow_DstSpringForwardGap_DoesNotThrow()
    {
        // NZ DST begins 2026-09-27 02:00 -> 03:00 (local 02:00-03:00 does not exist that day).
        // A schedule starting at 02:30 local on that date must not throw; it should resolve to
        // a valid instant rather than crash the enforcement loop.
        var nowUtc = new DateTimeOffset(2026, 9, 27, 13, 0, 0, TimeSpan.Zero);
        var schedules = new List<AccessSchedule> { Schedule(DynamicDayOfWeek.Sunday, 2.5, 4.0) };

        var exception = Record.Exception(() => _calculator.GetCurrentWindow(schedules, nowUtc, Auckland));

        Assert.Null(exception);
    }

    [Fact]
    public void GetCurrentWindow_DstFallBackAmbiguity_ResolvesToEarlierUtcInstant()
    {
        // Search forward for NZ's fall-back ambiguous hour rather than hardcoding the calendar
        // date, to avoid repeating the exact "manually computed the wrong instant" mistake this
        // test exists to catch.
        var probe = new DateTime(2026, 1, 1);
        while (!Auckland.IsAmbiguousTime(probe))
        {
            probe = probe.AddMinutes(30);
        }

        var offsets = Auckland.GetAmbiguousTimeOffsets(probe);
        var expectedEarliestUtc = offsets.Select(o => new DateTimeOffset(probe, o).ToUniversalTime()).Min();

        // Build a schedule whose window END lands exactly on the ambiguous local instant, then
        // assert GetCurrentWindow resolves it to expectedEarliestUtc, not the later instant.
        var dayOfWeek = (DynamicDayOfWeek)(int)probe.DayOfWeek;
        var endHour = probe.Hour + probe.Minute / 60.0;
        var schedules = new List<AccessSchedule> { Schedule(dayOfWeek, endHour - 1.0, endHour) };
        var nowUtc = expectedEarliestUtc.AddMinutes(-30); // inside the window, shortly before its end

        var result = _calculator.GetCurrentWindow(schedules, nowUtc, Auckland);

        Assert.True(result.HasCoveringWindow);
        Assert.Equal(expectedEarliestUtc, result.WindowEndUtc);
    }
}
