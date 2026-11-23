using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DailyDocket.Core.Mail;

namespace DailyDocket.Core.Calendar;

/// <summary>One event on one of the owner's calendars.</summary>
/// <param name="ContentHash">
/// A hash of what a meeting brief depends on (start, end, title, description, location and the
/// invited addresses); null for rows cached before it existed. See <see cref="CalendarParser.ContentHash"/>.
/// </param>
/// <param name="JoinLink">The meeting's Zoom, Teams, Meet or Webex link, when it has one. See <see cref="CalendarParser.JoinLink"/>.</param>
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
    DateTimeOffset? Updated,
    string? ContentHash = null,
    string? JoinLink = null);

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
        var emails = new List<string>();
        if (ev.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attendees.EnumerateArray())
            {
                if (Bool(a, "resource"))
                {
                    continue;
                }

                guests++;
                if (GmailParser.Str(a, "email") is { } email)
                {
                    emails.Add(email);
                }

                if (Bool(a, "self"))
                {
                    mine = GmailParser.Str(a, "responseStatus");
                }
            }
        }

        var title = GmailParser.Str(ev, "summary") ?? "(no title)";
        var location = GmailParser.Str(ev, "location") ?? "";
        var endValue = endAt ?? startAt.Value.AddHours(1);
        return new CalendarEvent(
            id,
            calendarKey,
            startAt.Value,
            endValue,
            allDay,
            title,
            location,
            guests,
            mine,
            GmailParser.Str(ev, "htmlLink"),
            GmailParser.Str(ev, "status") ?? "confirmed",
            GmailParser.Str(ev, "eventType"),
            DateTimeOffset.TryParse(GmailParser.Str(ev, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u) ? u : null,
            ContentHash(startAt.Value, endValue, title, GmailParser.Str(ev, "description") ?? "", location, emails),
            JoinLink(ev));
    }

    // Video meeting hosts only: the link becomes a Join button, and the location and description
    // are written by whoever sent the invitation, so an arbitrary site there must not get one.
    // Zoom gives some organizations zoom.com addresses (the JPR check-in's), not only zoom.us.
    private static readonly string[] MeetingHosts = ["zoom.us", "zoom.com", "zoomgov.com", "teams.microsoft.com", "teams.live.com", "meet.google.com", "webex.com"];

    private static readonly Regex Url = new(@"https://[^\s""'<>()\[\]{}|\\^`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The meeting's join link: Google's own conference link first (often empty: invitations from
    /// Outlook and Zoom put the link in the text), then the first Zoom, Teams, Meet or Webex link in
    /// the location, then in the description. Only the link is kept, never the description.
    /// </summary>
    public static string? JoinLink(JsonElement ev)
    {
        if (ev.TryGetProperty("conferenceData", out var conference) && conference.ValueKind == JsonValueKind.Object
            && conference.TryGetProperty("entryPoints", out var points) && points.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in points.EnumerateArray())
            {
                if (GmailParser.Str(p, "entryPointType") == "video" && MeetingLink(GmailParser.Str(p, "uri")) is { } video)
                {
                    return video;
                }
            }
        }

        foreach (var field in new[] { "hangoutLink", "location", "description" })
        {
            var text = GmailParser.Str(ev, field) ?? "";
            foreach (Match m in Url.Matches(text))
            {
                if (MeetingLink(m.Value) is { } link)
                {
                    return link;
                }
            }
        }

        return null;
    }

    /// <summary>The link, cleaned, when it is https on a video meeting host; otherwise null.</summary>
    internal static string? MeetingLink(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        // Descriptions are often HTML (href="...&amp;...") and sentences end in punctuation.
        var text = System.Net.WebUtility.HtmlDecode(candidate).TrimEnd('.', ',', ';', ':', '!', '?', '*', '>');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();

        // Phone pages that sit beside the join link in an invitation: Teams' dial-in numbers and
        // Zoom's "find your local number" (/u/...).
        if (host.StartsWith("dialin.", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/u/", StringComparison.Ordinal))
        {
            return null;
        }

        return MeetingHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)) ? uri.AbsoluteUri : null;
    }

    /// <summary>
    /// What a brief depends on, hashed: start, end, title, description, location and the invited
    /// addresses (sorted, case folded). The event's updated time was used before, but it moves
    /// whenever any attendee answers the invitation, so every RSVP remade the brief.
    /// </summary>
    /// <remarks>Only the hash is kept: the description can be long and is not shown anywhere.</remarks>
    public static string ContentHash(DateTimeOffset start, DateTimeOffset end, string title, string description, string location, IEnumerable<string> attendeeEmails)
    {
        var text = string.Join(
            "\n",
            start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            end.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            title,
            description.Replace("\r\n", "\n", StringComparison.Ordinal),
            location,
            string.Join(",", attendeeEmails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
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
