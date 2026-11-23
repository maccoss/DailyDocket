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
