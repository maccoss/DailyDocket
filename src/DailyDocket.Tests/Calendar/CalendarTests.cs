using DailyDocket.Core.Calendar;
using DailyDocket.Core.Configuration;
using DailyDocket.Tests.Fixtures;

namespace DailyDocket.Tests.Calendar;

public sealed class CalendarTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.FromHours(-8));

    private static IReadOnlyList<CalendarEvent> Events(string key = "work") =>
        CalendarParser.Parse(Fixture.Read("calendar-events.json"), key, Fixture.Pacific).Events;

    [Fact]
    public void Events_are_parsed_with_his_response_and_real_guests_only()
    {
        var (zone, events, _) = CalendarParser.Parse(Fixture.Read("calendar-events.json"), "work", Fixture.Pacific);

        zone.ShouldBe("America/Los_Angeles");
        var lab = events.Single(e => e.EventId == "ev-1");
        lab.MyResponse.ShouldBe("accepted");
        lab.Guests.ShouldBe(2);
        lab.Start.ShouldBe(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(-8)));
    }

    private static string? Join(string eventJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(eventJson);
        return CalendarParser.JoinLink(doc.RootElement);
    }

    [Fact]
    public void The_join_link_comes_from_the_conference_then_the_location_then_the_description()
    {
        Join("""{"conferenceData":{"entryPoints":[{"entryPointType":"phone","uri":"tel:+1-555-0100"},{"entryPointType":"video","uri":"https://meet.google.com/abc-defg-hij"}]},"location":"https://example.zoom.us/j/1"}""")
            .ShouldBe("https://meet.google.com/abc-defg-hij");
        // An empty conferenceData (what Outlook and Zoom invitations carry) falls through to the text.
        Join("""{"conferenceData":{},"location":"Room 101, https://example.zoom.us/j/123456789?pwd=abc.","description":"https://teams.microsoft.com/l/meetup-join/x"}""")
            .ShouldBe("https://example.zoom.us/j/123456789?pwd=abc");
        Join("""{"description":"Join: <a href=\"https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=%7b%7d&amp;btype=a\">Click here</a>"}""")
            .ShouldBe("https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=%7b%7d&btype=a");
    }

    [Fact]
    public void Only_https_video_meeting_hosts_become_a_join_link()
    {
        // The invitation's text is written by whoever sent it: another site must not get a Join button.
        Join("""{"description":"Agenda at https://docs.example.org/agenda and http://example.zoom.us/j/1"}""").ShouldBeNull();
        Join("""{"location":"https://zoom.us.evil.example/j/1"}""").ShouldBeNull();
        Join("""{"location":"Conference room B"}""").ShouldBeNull();
        Join("""{"description":"https://evilzoom.us/j/1 then https://washington.zoom.us/j/2"}""").ShouldBe("https://washington.zoom.us/j/2");
        // Dial-in pages are skipped for the meeting link itself.
        Join("""{"description":"Numbers: https://dialin.teams.microsoft.com/x?id=1 Join: https://teams.microsoft.com/l/meetup-join/y"}""")
            .ShouldBe("https://teams.microsoft.com/l/meetup-join/y");
        Join("""{"description":"Local number: https://example.zoom.us/u/abc Join: https://example.zoom.us/j/9"}""").ShouldBe("https://example.zoom.us/j/9");
    }

    [Fact]
    public void A_join_link_opens_the_meeting_app_where_one_takes_it()
    {
        MeetingLinks.AppLink("https://us02web.zoom.us/j/86118486353?pwd=abc123.1")
            .ShouldBe("zoommtg://us02web.zoom.us/join?action=join&confno=86118486353&pwd=abc123.1");
        MeetingLinks.AppLink("https://example-society.zoom.com/j/891614781")
            .ShouldBe("zoommtg://example-society.zoom.com/join?action=join&confno=891614781");
        MeetingLinks.AppScheme("https://example.zoom.us/j/123456789").ShouldBe("zoommtg");
        MeetingLinks.AppLink("https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=%7b%7d")
            .ShouldBe("msteams:/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=%7b%7d");
        // Not a meeting the app can open by id: the browser opens it.
        MeetingLinks.AppLink("https://example.zoom.us/my/someone").ShouldBeNull();
        MeetingLinks.AppLink("https://meet.google.com/abc-defg-hij").ShouldBeNull();
        MeetingLinks.AppLink("http://example.zoom.us/j/123456789").ShouldBeNull();
    }

    [Fact]
    public void A_join_link_is_shown_short_and_without_its_password()
    {
        MeetingLinks.Short("https://us02web.zoom.us/j/86118486353?pwd=abc123.1").ShouldBe("us02web.zoom.us/j/86118486353");
        MeetingLinks.Short("https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=%7b%7d").ShouldBe("Microsoft Teams meeting");
        MeetingLinks.Short("https://meet.google.com/abc-defg-hij").ShouldBe("meet.google.com/abc-defg-hij");
    }

    [Fact]
    public void A_zoom_com_link_is_a_join_link()
    {
        // Zoom gives some organizations zoom.com addresses rather than zoom.us.
        Join("""{"description":"Join Zoom Meeting\nhttps://example-society.zoom.com/j/123456789?pwd=abc\n\nMeeting ID: 123"}""")
            .ShouldBe("https://example-society.zoom.com/j/123456789?pwd=abc");
    }

    [Fact]
    public void An_all_day_event_starts_at_midnight_in_the_calendars_zone()
    {
        var conference = Events().Single(e => e.EventId == "ev-4");

        conference.AllDay.ShouldBeTrue();
        conference.Start.ShouldBe(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(-8)));
        CalendarRules.IsBackground(conference).ShouldBeTrue();
    }

    [Fact]
    public void Declined_and_cancelled_events_are_not_on_the_schedule()
    {
        var day = CalendarRules.Build(Events(), Fixture.Config(), Now, 7, Fixture.Pacific).First();

        day.Slots.Select(s => s.Event.EventId).ShouldBe(["ev-1", "ev-2"]);
    }

    [Fact]
    public void Overlapping_primary_events_clash_and_an_unanswered_invitation_is_flagged()
    {
        var slots = CalendarRules.Build(Events(), Fixture.Config(), Now, 7, Fixture.Pacific).First().Slots;

        slots.ShouldAllBe(s => s.Clash);
        slots.Single(s => s.Event.EventId == "ev-2").Unanswered.ShouldBeTrue();
    }

    [Fact]
    public void Department_events_never_clash()
    {
        var events = Events().Select(e => e.EventId == "ev-2" ? e with { CalendarKey = "dept" } : e);

        var slots = CalendarRules.Build(events, Fixture.Config(), Now, 7, Fixture.Pacific).First().Slots;

        slots.ShouldAllBe(s => !s.Clash);
    }

    [Fact]
    public void A_background_event_shows_on_every_day_it_covers()
    {
        var days = CalendarRules.Build(Events(), Fixture.Config(), Now, 7, Fixture.Pacific);

        days.Where(d => d.Background.Any(b => b.Event.EventId == "ev-4")).Select(d => d.Date)
            .ShouldBe([new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 25)]);
    }

    [Fact]
    public void A_list_events_call_is_matched_to_its_calendar_by_id()
    {
        var config = Fixture.Config();

        config.CalendarFor(null).Key.ShouldBe("work");
        config.CalendarFor("primary").Key.ShouldBe("work");
        config.CalendarFor("DEPT@example.org").Key.ShouldBe("dept");
        config.CalendarFor("dept@example.org").Primary.ShouldBeFalse();
    }

    [Fact]
    public void A_missing_config_file_gives_the_defaults()
    {
        var config = DocketConfig.Load(Path.Combine(Path.GetTempPath(), "no-such-docket.yaml"));

        config.Calendars.ShouldHaveSingleItem().Key.ShouldBe("work");
        config.Mail.WindowDays.ShouldBe(30);
    }
}
