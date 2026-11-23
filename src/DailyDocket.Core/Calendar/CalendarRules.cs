using DailyDocket.Core.Configuration;

namespace DailyDocket.Core.Calendar;

/// <summary>One event placed on a day of the schedule.</summary>
/// <param name="Background">All day, eight hours or longer, or out of office: scenery, not a slot.</param>
/// <param name="Clash">Overlaps another primary event that day.</param>
public sealed record ScheduledEvent(CalendarEvent Event, CalendarConfig Calendar, bool Background, bool Clash, bool Unanswered);

public sealed record ScheduleDay(DateOnly Date, IReadOnlyList<ScheduledEvent> Background, IReadOnlyList<ScheduledEvent> Slots);

/// <summary>The dashboard artifact's schedule rules, as pure functions.</summary>
public static class CalendarRules
{
    /// <summary>An event this long is scenery, not a slot.</summary>
    public static readonly TimeSpan BackgroundLength = TimeSpan.FromHours(8);

    /// <summary>Cancelled events and ones he declined are not on his schedule.</summary>
    public static bool IsShown(CalendarEvent e) =>
        !string.Equals(e.Status, "cancelled", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(e.MyResponse, "declined", StringComparison.OrdinalIgnoreCase);

    public static bool IsBackground(CalendarEvent e) =>
        e.AllDay || e.End - e.Start >= BackgroundLength ||
        string.Equals(e.EventType, "outOfOffice", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the days from <paramref name="today"/> for <paramref name="days"/> days. Background events
    /// show on every day they cover; a slot shows on its first day. Clashes are checked among
    /// primary calendars only: department events never clash.
    /// </summary>
    public static IReadOnlyList<ScheduleDay> Build(
        IEnumerable<CalendarEvent> events, DocketConfig config, DateTimeOffset now, int days, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var shown = events.Where(IsShown).Where(e => e.End > now).ToList();
        var result = new List<ScheduleDay>();
        for (var d = 0; d <= days; d++)
        {
            var date = today.AddDays(d);
            var from = Midnight(date, zone);
            var to = Midnight(date.AddDays(1), zone);

            var background = shown.Where(IsBackground).Where(e => e.Start < to && e.End > from)
                .Select(e => new ScheduledEvent(e, CalendarByKey(config, e.CalendarKey), true, false, false))
                .ToList();

            var slots = shown.Where(e => !IsBackground(e))
                .Where(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Start, zone).DateTime) == date)
                .OrderBy(e => e.Start)
                .ToList();
            var placed = slots.Select(e =>
            {
                var cal = CalendarByKey(config, e.CalendarKey);
                var clash = cal.Primary && slots.Any(o => !ReferenceEquals(o, e) && CalendarByKey(config, o.CalendarKey).Primary && o.Start < e.End && e.Start < o.End);
                var unanswered = string.Equals(e.MyResponse, "needsAction", StringComparison.OrdinalIgnoreCase);
                return new ScheduledEvent(e, cal, false, clash, unanswered);
            }).ToList();

            if (background.Count > 0 || placed.Count > 0)
            {
                result.Add(new ScheduleDay(date, background, placed));
            }
        }

        return result;
    }

    private static CalendarConfig CalendarByKey(DocketConfig config, string key) =>
        config.Calendars.FirstOrDefault(c => c.Key == key) ?? new CalendarConfig { Key = key, Primary = true };

    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
