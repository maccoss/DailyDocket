using DailyDocket.Core.Calendar;
using DailyDocket.Core.Claude;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Prep;
using DailyDocket.Tests.Fixtures;

namespace DailyDocket.Tests.Prep;

public sealed class PrepTests
{
    private static readonly TimeSpan Pst = TimeSpan.FromHours(-8);

    // Friday, November 27, 2026, 2 a.m. Pacific: the nightly run.
    private static readonly DateTimeOffset FridayNight = new(2026, 11, 27, 2, 0, 0, Pst);

    private static DocketConfig Config() => DocketConfig.Parse("""
        owner:
          name: Owner Person
          emails: [owner@example.org]
          slackUserId: U0000000001
        calendars:
          - key: work
            primary: true
          - key: home
            id: home@example.org
            primary: true
          - key: dept
            id: dept@example.org
            primary: false
        prep:
          workingDays: 2
          skipCalendars: [home]
        """);

    private static CalendarEvent Event(string id, DateTimeOffset start, string calendar = "work", string? response = "accepted", int guests = 3,
        bool allDay = false, string? type = null, DateTimeOffset? updated = null, string status = "confirmed") =>
        new(id, calendar, start, start.AddHours(1), allDay, $"Meeting {id}", "", guests, response, null, status, type, updated ?? new DateTimeOffset(2026, 11, 20, 18, 0, 0, 123, TimeSpan.Zero));

    private static DateTimeOffset At(int day, int hour) => new(2026, 11, day, hour, 0, 0, Pst);

    [Fact]
    public void Two_working_days_from_friday_are_friday_and_monday()
    {
        PrepRules.WorkingDays(FridayNight, Fixture.Pacific, 2).ShouldBe([new DateOnly(2026, 11, 27), new DateOnly(2026, 11, 30)]);
        PrepRules.WorkingDays(new DateTimeOffset(2026, 11, 28, 2, 0, 0, Pst), Fixture.Pacific, 2).ShouldBe([new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 1)]);
    }

    [Fact]
    public void Only_meetings_mike_will_attend_in_the_next_two_working_days_are_prepped()
    {
        CalendarEvent[] events =
        [
            Event("keep-fri", At(27, 10)),
            Event("keep-mon", At(30, 9)),
            Event("saturday", At(28, 10)),
            Event("tuesday", new DateTimeOffset(2026, 12, 1, 10, 0, 0, Pst)),
            Event("declined", At(27, 11), response: "declined"),
            Event("cancelled", At(27, 11), status: "cancelled"),
            Event("allday", At(27, 0), allDay: true),
            Event("home", At(27, 12), calendar: "home"),
            Event("dept-unanswered", At(27, 13), calendar: "dept", response: "needsAction"),
            Event("dept-accepted", At(27, 14), calendar: "dept", response: "accepted"),
            Event("alone", At(27, 15), guests: 1),
            Event("ooo", At(27, 16), type: "outOfOffice"),
            Event("past", At(27, 1)),
        ];

        var targets = PrepRules.Select(events, Config(), FridayNight, Fixture.Pacific);

        targets.Select(t => t.Event.EventId).ShouldBe(["keep-fri", "dept-accepted", "keep-mon"]);
        targets[1].CalendarId.ShouldBe("dept@example.org");
        targets[2].Date.ShouldBe(new DateOnly(2026, 11, 30));
    }

    [Fact]
    public void Each_skip_says_why()
    {
        PrepRules.Skip(Event("x", At(27, 10), response: "declined"), Config()).ShouldBe("declined or cancelled");
        PrepRules.Skip(Event("x", At(27, 10), calendar: "home"), Config()).ShouldBe("calendar not prepped");
        PrepRules.Skip(Event("x", At(27, 10), calendar: "dept", response: "tentative"), Config()).ShouldBe("secondary calendar, not accepted");
        PrepRules.Skip(Event("x", At(27, 10), guests: 0), Config()).ShouldBe("no one else invited");
        PrepRules.Skip(Event("x", At(27, 10)), Config()).ShouldBeNull();
    }

    [Fact]
    public void A_brief_is_remade_only_when_the_event_changed()
    {
        var e = Event("e1", At(27, 10)) with { ContentHash = "aaaa" };
        var brief = Brief("e1") with { Start = e.Start, EventUpdated = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero), EventHash = "aaaa" };

        PrepRules.IsStale(null, e).ShouldBeTrue();
        PrepRules.IsStale(brief, e).ShouldBeFalse();
        PrepRules.IsStale(brief, e with { ContentHash = "bbbb" }).ShouldBeTrue();
        PrepRules.IsStale(brief, e with { Start = e.Start.AddHours(1) }).ShouldBeTrue();

        // Someone answering the invitation moves the updated time and nothing else.
        PrepRules.IsStale(brief, e with { Updated = e.Updated!.Value.AddMinutes(5) }).ShouldBeFalse();
    }

    [Fact]
    public void A_brief_or_event_from_before_the_hash_is_judged_by_its_start_alone()
    {
        var e = Event("e1", At(27, 10)) with { ContentHash = "aaaa" };
        var old = Brief("e1") with { Start = e.Start, EventUpdated = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), EventHash = null };

        PrepRules.IsStale(old, e).ShouldBeFalse();
        PrepRules.IsStale(old, e with { Start = e.Start.AddMinutes(30) }).ShouldBeTrue();
        PrepRules.IsStale(old with { EventHash = "aaaa" }, e with { ContentHash = null }).ShouldBeFalse();
    }

    [Fact]
    public void The_event_hash_ignores_rsvps_but_not_what_the_brief_depends_on()
    {
        static string Parse(string attendees, string description = "Budget", string summary = "Renewal", string end = "2026-11-27T11:00:00-08:00") =>
            CalendarParser.Parse(
                $$"""{"events":[{"id":"e1","summary":"{{summary}}","description":"{{description}}","updated":"2026-11-20T18:00:00Z","start":{"dateTime":"2026-11-27T10:00:00-08:00"},"end":{"dateTime":"{{end}}"},"attendees":[{{attendees}}]}]}""",
                "work",
                Fixture.Pacific).Events.Single().ContentHash.ShouldNotBeNull();

        var invited = Parse("""{"email":"owner@example.org","self":true,"responseStatus":"needsAction"},{"email":"Pat@Example.org","responseStatus":"needsAction"}""");
        var answered = Parse("""{"email":"pat@example.org","responseStatus":"accepted"},{"email":"owner@example.org","self":true,"responseStatus":"accepted"}""");

        answered.ShouldBe(invited);
        Parse("""{"email":"owner@example.org"},{"email":"pat@example.org"},{"email":"lee@example.org"}""").ShouldNotBe(invited);
        Parse("""{"email":"owner@example.org"},{"email":"pat@example.org"}""", description: "Budget and hiring").ShouldNotBe(invited);
        Parse("""{"email":"owner@example.org"},{"email":"pat@example.org"}""", summary: "Renewal, moved").ShouldNotBe(invited);
        Parse("""{"email":"owner@example.org"},{"email":"pat@example.org"}""", end: "2026-11-27T11:30:00-08:00").ShouldNotBe(invited);
    }

    [Fact]
    public void A_brief_file_round_trips()
    {
        var brief = Brief("abc123_20261127T180000Z");

        var text = BriefFile.Format(brief);
        var back = BriefFile.Parse(text)!;

        back.EventId.ShouldBe(brief.EventId);
        back.Title.ShouldBe(brief.Title);
        back.Start.ShouldBe(brief.Start);
        back.EventUpdated.ShouldBe(brief.EventUpdated);
        back.EventHash.ShouldBe(brief.EventHash);
        back.Attendees.ShouldBe(brief.Attendees);
        back.Agenda.ShouldBe(brief.Agenda);
        back.Context.ShouldBe(brief.Context);
        back.Owes.ShouldBe(brief.Owes);
        back.Gaps.ShouldBeEmpty();
        back.Sources.ShouldBe(brief.Sources);
        back.Written.ShouldBe(brief.Written);
        back.WrittenOn.ShouldBe("skiddaw");
        back.Model.ShouldBe("sonnet");
        text.ShouldContain("## Who\n- Pat Lee (Acme Labs): program officer [organizer]\n- Sam Roe\n");
        text.ShouldNotContain("## Gaps");
    }

    [Fact]
    public void Names_lose_the_separators_and_items_are_one_capped_line_without_em_dashes()
    {
        var brief = Brief("e1") with
        {
            Attendees = [new BriefAttendee("Lee (Chair): Dept [x]", "Org (West)", "line one\nline two [organizer]", false)],
            Agenda = ["First\nsecond \u2014 third", "", new string('a', 400)],
            Context = [.. Enumerable.Range(1, 12).Select(i => $"item {i}")],
            Sources = ["19a2c0f3e8b1", "https://docs.google.com/document/d/1", "Dear Mike, here is the whole message", "http://insecure.example.org"],
        };

        var back = BriefFile.Parse(BriefFile.Format(brief))!;

        back.Attendees.ShouldBe([new BriefAttendee("Lee Chair Dept x", "Org West", "line one line two organizer", false)]);
        back.Agenda[0].ShouldBe("First second, third");
        back.Agenda.Count.ShouldBe(2);
        back.Agenda[1].Length.ShouldBeLessThanOrEqualTo(BriefRules.MaxItemLength);
        back.Context.Count.ShouldBe(BriefRules.MaxItems);
        back.Sources.ShouldBe(["19a2c0f3e8b1", "https://docs.google.com/document/d/1"]);
    }

    [Fact]
    public void A_file_that_is_not_a_brief_is_skipped()
    {
        BriefFile.Parse("just notes").ShouldBeNull();
        BriefFile.Parse("---\ntitle: x\n---\n").ShouldBeNull();
        BriefFile.Parse("---\neventId: [broken\n---\n").ShouldBeNull();
    }

    [Fact]
    public void Briefs_are_filed_by_date_and_an_event_id_cannot_be_a_path()
    {
        var root = Directory.CreateTempSubdirectory("dd-brief-").FullName;
        try
        {
            BriefFile.Save(root, new DateOnly(2026, 11, 27), Brief("e1"));

            File.Exists(Path.Combine(root, "briefs", "2026-11-27", "e1.md")).ShouldBeTrue();
            BriefFile.LoadDays(root, [new DateOnly(2026, 11, 27), new DateOnly(2026, 11, 30)]).Keys.ShouldBe(["e1"]);
            Should.Throw<ArgumentException>(() => BriefFile.PathFor(root, new DateOnly(2026, 11, 27), "..\\x"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_brief_saves_only_the_meeting_the_run_was_given()
    {
        var run = new FakeRun(Target("e1"));
        var tools = new AppTools { Run = run };

        tools.WriteBrief("someone-elses", [new BriefAttendeeArg("Pat")], ["Plan"], [], [], []).ShouldStartWith("Not saved: someone-elses is not the meeting");
        run.Saved.ShouldBeEmpty();

        tools.WriteBrief("e1", [new BriefAttendeeArg("Pat", "Acme", "PI", true)], ["Plan the renewal"], ["Thread about the budget"], [], [], ["t1"]).ShouldBe("saved");
        run.Saved.Single().Attendees.ShouldBe([new BriefAttendee("Pat", "Acme", "PI", true)]);
        run.Saved.Single().Start.ShouldBe(run.PrepTargets[0].Event.Start);
    }

    [Fact]
    public void Write_brief_names_what_is_missing()
    {
        var run = new FakeRun(Target("e1"));
        var tools = new AppTools { Run = run };

        var result = tools.WriteBrief("e1", [new BriefAttendeeArg("  ")], ["  "], [], [], []);

        result.ShouldContain("attendees must list who is coming");
        result.ShouldContain("agenda needs at least one item");
        run.Saved.ShouldBeEmpty();
    }

    [Fact]
    public void Write_brief_is_refused_in_runs_that_are_not_prep()
    {
        var tools = new AppTools { Run = new NoPrepRun() };

        tools.WriteBrief("e1", [new BriefAttendeeArg("Pat")], ["Plan"], [], [], []).ShouldStartWith("Not saved");
    }

    [Fact]
    public void The_prep_run_reads_only_and_can_write_nothing_but_its_brief()
    {
        var plan = JobPlans.PrepRun(Target("e1"), Config(), Fixture.Pacific, ["To do: Send the budget"], "sonnet", (_, _) => "");

        plan.AllowedTools.ShouldNotContain(t => ConnectorTools.Writes.Contains(t));
        ConnectorTools.Writes.ShouldAllBe(w => plan.DisallowedTools.Contains(w));
        plan.AllowedTools.ShouldContain(ConnectorTools.GetEvent);
        plan.AllowedTools.ShouldContain(AppTools.WriteBriefToolName);
        plan.AllowedTools.ShouldNotContain(t => t.StartsWith(ConnectorTools.Slack, StringComparison.Ordinal));
        plan.Capture.ShouldBeFalse();
        plan.Prep!.Targets.Single().Event.EventId.ShouldBe("e1");
        plan.Prompt.ShouldContain("\"eventId\":\"e1\"");
        plan.Prompt.ShouldContain("To do: Send the budget");
    }

    [Fact]
    public async Task Prep_writes_each_due_brief_once_and_skips_unchanged_ones()
    {
        var root = Directory.CreateTempSubdirectory("dd-prep-").FullName;
        try
        {
            var events = new List<CalendarEvent> { Event("e1", At(27, 10)) with { ContentHash = "h1" }, Event("e2", At(30, 9)) with { ContentHash = "h2" } };
            var changed = new List<string>();
            var runs = 0;
            var prep = new MeetingPrep(
                (plan, _) =>
                {
                    runs++;
                    var target = plan.Prep!.Targets[0];
                    plan.Prep.Save(target, Brief(target.Event.EventId) with { Start = target.Event.Start, EventUpdated = target.Event.Updated, EventHash = target.Event.ContentHash });
                    return Task.FromResult(new JobOutcome("prep", true, null, 0, 0.2m));
                },
                () => root, Config, () => "sonnet", (_, _) => events, () => [], changed.Add, "skiddaw");

            var first = await prep.RunDueAsync(FridayNight, TestContext.Current.CancellationToken);
            var second = await prep.RunDueAsync(FridayNight, TestContext.Current.CancellationToken);

            first.Count.ShouldBe(2);
            second.ShouldBeEmpty();
            runs.ShouldBe(2);
            changed.Count.ShouldBe(2);
            BriefFile.Load(root, new DateOnly(2026, 11, 30), "e2")!.WrittenOn.ShouldBe("skiddaw");

            // An RSVP alone changes nothing; a new description remakes the brief.
            events[1] = events[1] with { Updated = events[1].Updated!.Value.AddHours(1) };
            events[0] = events[0] with { ContentHash = "h1-changed" };
            prep.Due(FridayNight).Select(t => t.Event.EventId).ShouldBe(["e1"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_run_that_never_saved_a_brief_counts_as_failed()
    {
        var root = Directory.CreateTempSubdirectory("dd-prep-").FullName;
        try
        {
            var prep = new MeetingPrep(
                (_, _) => Task.FromResult(new JobOutcome("prep", true, null, 0, 0.1m)),
                () => root, Config, () => "sonnet", (_, _) => [Event("e1", At(27, 10))], () => [], _ => { }, "skiddaw");

            var outcome = (await prep.RunDueAsync(FridayNight, TestContext.Current.CancellationToken)).Single();

            outcome.Ok.ShouldBeFalse();
            outcome.Detail!.ShouldContain("without writing a brief");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static PrepTarget Target(string id) => new(Event(id, At(27, 10)), null, new DateOnly(2026, 11, 27));

    private static MeetingBrief Brief(string id) => new(
        id,
        "Renewal planning",
        At(27, 10),
        new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero),
        [new BriefAttendee("Pat Lee", "Acme Labs", "program officer", true), new BriefAttendee("Sam Roe", "", "", false)],
        ["Decide the renewal's aims"],
        ["Pat asked for a draft budget on Nov 18"],
        ["The draft budget, promised for Monday"],
        [],
        ["19a2c0f3e8b1"],
        new DateTimeOffset(2026, 11, 27, 2, 4, 0, Pst),
        "skiddaw",
        "sonnet",
        "0123abcd4567ef89");

    private sealed class FakeRun(PrepTarget target) : IRunContext
    {
        public List<MeetingBrief> Saved { get; } = [];

        public IReadOnlyDictionary<string, string> TriageInstructions => new Dictionary<string, string>();

        public IReadOnlyList<PrepTarget> PrepTargets => [target];

        public IReadOnlyList<string> ThreadsToRead() => [];

        public IReadOnlyList<TriageItem> TriageBatch() => [];

        public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why)
        {
        }

        public string SaveBrief(PrepTarget t, MeetingBrief brief)
        {
            Saved.Add(brief);
            return "saved";
        }
    }

    private sealed class NoPrepRun : IRunContext
    {
        public IReadOnlyDictionary<string, string> TriageInstructions => new Dictionary<string, string>();

        public IReadOnlyList<string> ThreadsToRead() => [];

        public IReadOnlyList<TriageItem> TriageBatch() => [];

        public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why)
        {
        }
    }
}
