using System.Globalization;
using System.Text.Json;
using DailyDocket.Core.Mail;

namespace DailyDocket.Core.Calendar;

/// <summary>One event on one of the owner's calendars.</summary>
public sealed record CalendarEvent(
    string EventId,
    string CalendarKey,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string Title,
    string Location,
    int Guests,
    string? MyResponse,
    string? HtmlLink,
    string Status,
    string? EventType,
    DateTimeOffset? Updated);

/// <summary>Turns Calendar <c>list_events</c> results into records.</summary>
public static class CalendarParser
{
    public static (string? TimeZone, IReadOnlyList<CalendarEvent> Events, string? NextPageToken) Parse(
        string json, string calendarKey, TimeZoneInfo zone)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var events = new List<CalendarEvent>();
        var list = root.TryGetProperty("events", out var e) ? e : root.TryGetProperty("items", out var it) ? it : default;
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in list.EnumerateArray())
            {
                if (Parse(ev, calendarKey, zone) is { } parsed)
                {
                    events.Add(parsed);
                }
            }
        }

        events.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (GmailParser.Str(root, "timeZone"), events, GmailParser.Str(root, "nextPageToken"));
    }

    private static CalendarEvent? Parse(JsonElement ev, string calendarKey, TimeZoneInfo zone)
    {
        var id = GmailParser.Str(ev, "id");
        if (id is null || !ev.TryGetProperty("start", out var start))
        {
            return null;
        }

        var allDay = GmailParser.Str(start, "dateTime") is null && GmailParser.Str(start, "date") is not null;
        var startAt = When(start, zone);
        if (startAt is null)
        {
            return null;
        }

        var endAt = ev.TryGetProperty("end", out var end) ? When(end, zone) : null;

        string? mine = null;
        var guests = 0;
        if (ev.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attendees.EnumerateArray())
            {
                if (Bool(a, "resource"))
                {
                    continue;
                }

                guests++;
                if (Bool(a, "self"))
                {
                    mine = GmailParser.Str(a, "responseStatus");
                }
            }
        }

        return new CalendarEvent(
            id,
            calendarKey,
            startAt.Value,
            endAt ?? startAt.Value.AddHours(1),
            allDay,
            GmailParser.Str(ev, "summary") ?? "(no title)",
            GmailParser.Str(ev, "location") ?? "",
            guests,
            mine,
            GmailParser.Str(ev, "htmlLink"),
            GmailParser.Str(ev, "status") ?? "confirmed",
            GmailParser.Str(ev, "eventType"),
            DateTimeOffset.TryParse(GmailParser.Str(ev, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u) ? u : null);
    }

    /// <summary>A timed event has dateTime; an all-day event has a date, which is midnight in the calendar's zone.</summary>
    private static DateTimeOffset? When(JsonElement e, TimeZoneInfo zone)
    {
        if (GmailParser.Str(e, "dateTime") is { } dt &&
            DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return at;
        }

        if (GmailParser.Str(e, "date") is { } d &&
            DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var local = day.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        return null;
    }

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
