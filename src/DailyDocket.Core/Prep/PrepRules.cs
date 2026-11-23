using DailyDocket.Core.Calendar;
using DailyDocket.Core.Configuration;

namespace DailyDocket.Core.Prep;

/// <summary>An event that gets a brief: the event, the calendar to read it from, and its local date (the brief's folder).</summary>
public sealed record PrepTarget(CalendarEvent Event, string? CalendarId, DateOnly Date);

/// <summary>Which events meeting prep covers, and when a brief must be remade, as pure functions.</summary>
public static class PrepRules
{
    /// <summary>Event types that are not meetings.</summary>
    private static readonly HashSet<string> NotMeetings = new(StringComparer.OrdinalIgnoreCase) { "outOfOffice", "focusTime", "workingLocation", "birthday" };

    /// <summary>
    /// The first <paramref name="count"/> working days (Monday to Friday) starting today: run at 2 a.m.
    /// on a Friday, that is Friday and Monday, so a Monday morning meeting is ready Friday night.
    /// </summary>
    public static IReadOnlyList<DateOnly> WorkingDays(DateTimeOffset now, TimeZoneInfo zone, int count)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = new List<DateOnly>();
        while (days.Count < Math.Max(0, count))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(day);
            }

            day = day.AddDays(1);
        }

        return days;
    }

    /// <summary>Why an event is not prepped, or null when it is.</summary>
    public static string? Skip(CalendarEvent e, DocketConfig config)
    {
        var calendar = config.Calendars.FirstOrDefault(c => c.Key == e.CalendarKey) ?? new CalendarConfig { Key = e.CalendarKey, Primary = true };
        if (!CalendarRules.IsShown(e))
        {
            return "declined or cancelled";
        }

        if (CalendarRules.IsBackground(e))
        {
            return "all day";
        }

        if (e.EventType is { } type && NotMeetings.Contains(type))
        {
            return "not a meeting";
        }

        if (config.Prep.SkipCalendars.Any(k => string.Equals(k, e.CalendarKey, StringComparison.OrdinalIgnoreCase)))
        {
            return "calendar not prepped";
        }

        if (!calendar.Primary && !string.Equals(e.MyResponse, "accepted", StringComparison.OrdinalIgnoreCase))
        {
            return "secondary calendar, not accepted";
        }

        // Guests counts every invited person, the owner included: fewer than two is a block of his own time.
        return e.Guests < 2 ? "no one else invited" : null;
    }

    /// <summary>The events in the next working days, not yet started, that get a brief, soonest first.</summary>
    public static IReadOnlyList<PrepTarget> Select(IEnumerable<CalendarEvent> events, DocketConfig config, DateTimeOffset now, TimeZoneInfo zone)
    {
        var days = WorkingDays(now, zone, config.Prep.WorkingDays).ToHashSet();
        return
        [
            .. events
                .Where(e => e.Start > now)
                .Select(e => (Event: e, Date: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Start, zone).DateTime)))
                .Where(x => days.Contains(x.Date) && Skip(x.Event, config) is null)
                .GroupBy(x => x.Event.EventId)
                .Select(g => g.First()) // the same meeting on two calendars is prepped once
                .OrderBy(x => x.Event.Start)
                .Select(x => new PrepTarget(x.Event, config.Calendars.FirstOrDefault(c => c.Key == x.Event.CalendarKey)?.Id, x.Date)),
        ];
    }

    /// <summary>
    /// A brief is remade when there is none, the event's start moved, or what the brief depends on
    /// changed (the event's <see cref="CalendarEvent.ContentHash"/>: start, end, title, description,
    /// location, who is invited).
    /// </summary>
    /// <remarks>
    /// Not the event's updated time: Calendar moves it whenever anyone answers the invitation, which
    /// made every RSVP remake the brief and pay for a run. A brief or cached event from before the
    /// hash existed has none to compare; it is judged by its start alone rather than remade.
    /// </remarks>
    public static bool IsStale(MeetingBrief? brief, CalendarEvent e) =>
        brief is null
        || !SameSecond(brief.Start, e.Start)
        || (brief.EventHash is { } written && e.ContentHash is { } current && !string.Equals(written, current, StringComparison.Ordinal));

    /// <summary>The file keeps whole seconds; Calendar's updated times carry milliseconds.</summary>
    private static bool SameSecond(DateTimeOffset a, DateTimeOffset b) =>
        a.ToUnixTimeSeconds() == b.ToUnixTimeSeconds();
}
