using System.Globalization;
using DailyDocket.Core.Configuration;

namespace DailyDocket.Core.Nightly;

/// <summary>Which of the shared jobs a run does.</summary>
public enum NightlySlot
{
    None,

    /// <summary>The deep sweep, then meeting prep for the next two working days.</summary>
    Nightly,

    /// <summary>Meeting prep only, for events added or changed since the nightly run.</summary>
    CatchUp,
}

/// <summary>When the nightly jobs are due, as pure functions of the clock and the lease.</summary>
/// <remarks>
/// Due-ness is read from the lease's last run times, not this machine's, so a fallback's run at
/// 02:00 is not repeated by the designated machine when it wakes at 08:00.
/// </remarks>
public static class NightlyPlanner
{
    private static readonly TimeOnly DefaultNightly = new(2, 0);
    private static readonly TimeOnly DefaultCatchUp = new(12, 30);

    /// <summary>The job due now, or None. A missed nightly run is caught up before a catch-up slot.</summary>
    public static NightlySlot Due(NightlyConfig config, JobLease? lease, DateTimeOffset now, TimeZoneInfo zone)
    {
        var lastNightlySlot = Latest(Time(config.At, DefaultNightly), now, zone);
        var lastCatchUpSlot = Latest(Time(config.CatchUpAt, DefaultCatchUp), now, zone);

        if ((lease?.LastNightly ?? DateTimeOffset.MinValue) < lastNightlySlot)
        {
            return NightlySlot.Nightly;
        }

        // A nightly run after the latest catch-up slot already covered it.
        var lastPrep = Max(lease?.LastNightly, lease?.LastCatchUp);
        return lastPrep < lastCatchUpSlot ? NightlySlot.CatchUp : NightlySlot.None;
    }

    /// <summary>The next scheduled slot after <paramref name="now"/>: when the lease holder is next expected.</summary>
    public static DateTimeOffset NextSlot(NightlyConfig config, DateTimeOffset now, TimeZoneInfo zone)
    {
        var a = Next(Time(config.At, DefaultNightly), now, zone);
        var b = Next(Time(config.CatchUpAt, DefaultCatchUp), now, zone);
        return a < b ? a : b;
    }

    /// <summary>
    /// The most recent scheduled slot, nightly or catch-up, at or before <paramref name="now"/>: the
    /// slot a run now belongs to, for counting failures and claims per slot.
    /// </summary>
    public static DateTimeOffset LatestSlot(NightlyConfig config, DateTimeOffset now, TimeZoneInfo zone)
    {
        var a = Latest(Time(config.At, DefaultNightly), now, zone);
        var b = Latest(Time(config.CatchUpAt, DefaultCatchUp), now, zone);
        return a > b ? a : b;
    }

    /// <summary>Parses "HH:mm"; anything else is the default, so a typo never stops the jobs.</summary>
    internal static TimeOnly Time(string? text, TimeOnly fallback) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : fallback;

    /// <summary>The most recent time of day <paramref name="at"/> at or before now, in the owner's zone.</summary>
    private static DateTimeOffset Latest(TimeOnly at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var slot = At(today, at, zone);
        return slot <= now ? slot : At(today.AddDays(-1), at, zone);
    }

    private static DateTimeOffset Next(TimeOnly at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var slot = At(today, at, zone);
        return slot > now ? slot : At(today.AddDays(1), at, zone);
    }

    private static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset? b) =>
        (a ?? DateTimeOffset.MinValue) > (b ?? DateTimeOffset.MinValue) ? a ?? DateTimeOffset.MinValue : b ?? DateTimeOffset.MinValue;
}
