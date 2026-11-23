using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Nightly;
using DailyDocket.Tests.Fixtures;

namespace DailyDocket.Tests.Nightly;

public sealed class NightlyTests
{
    private static readonly TimeSpan Pst = TimeSpan.FromHours(-8);

    private static NightlyConfig Config() => DocketConfig.Parse("""
        nightly:
          machine: Skiddaw
          fallback: [speedygonzales, pillar]
          at: "02:00"
          catchUpAt: "12:30"
          graceHours: 2
        """).Nightly;

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, Pst);

    [Fact]
    public void The_nightly_section_is_read_from_docket_yaml()
    {
        var c = Config();

        c.Machine.ShouldBe("Skiddaw");
        c.Fallback.ShouldBe(["speedygonzales", "pillar"]);
        c.IsDesignated("skiddaw").ShouldBeTrue();
        c.IsDesignated("SKIDDAW").ShouldBeTrue();
        c.FallbackRank("SpeedyGonzales").ShouldBe(0);
        c.FallbackRank("surface").ShouldBe(-1);
        new DocketConfig().Nightly.Machine.ShouldBe("");
    }

    [Fact]
    public void The_designated_machine_always_runs_and_takes_the_job_back()
    {
        var lease = new JobLease("speedygonzales", Local(23, 2), Local(23, 12, 30), Local(23, 2), null);

        LeaseRules.Decide(Config(), lease, "skiddaw", Local(23, 8)).MayRun.ShouldBeTrue();
    }

    [Fact]
    public void A_fallback_waits_until_the_lease_is_two_hours_past_due()
    {
        var lease = new JobLease("skiddaw", Local(22, 12, 31), Local(23, 2), Local(22, 2), Local(22, 12, 31));

        LeaseRules.Decide(Config(), lease, "speedygonzales", Local(23, 3, 59)).MayRun.ShouldBeFalse();
        LeaseRules.Decide(Config(), lease, "speedygonzales", Local(23, 4, 1)).MayRun.ShouldBeTrue();

        // The second fallback waits twice as long, so the two rarely both run.
        LeaseRules.Decide(Config(), lease, "pillar", Local(23, 4, 1)).MayRun.ShouldBeFalse();
        LeaseRules.Decide(Config(), lease, "pillar", Local(23, 6, 1)).MayRun.ShouldBeTrue();
    }

    [Fact]
    public void A_fallback_keeps_the_job_while_it_holds_the_lease_and_stops_when_the_designated_one_returns()
    {
        var held = new JobLease("SpeedyGonzales", Local(23, 4, 5), Local(23, 12, 30), Local(23, 4, 5), null);
        LeaseRules.Decide(Config(), held, "speedygonzales", Local(23, 12, 31)).MayRun.ShouldBeTrue();

        var back = LeaseRules.Renew(held, "skiddaw", Local(23, 12, 35), Local(24, 2), NightlySlot.CatchUp);
        LeaseRules.Decide(Config(), back, "speedygonzales", Local(24, 2, 1)).MayRun.ShouldBeFalse();
    }

    [Fact]
    public void Machines_that_are_not_listed_never_run_the_nightly_jobs()
    {
        LeaseRules.Decide(Config(), null, "surface", Local(23, 2)).MayRun.ShouldBeFalse();
        LeaseRules.Decide(new NightlyConfig(), null, "skiddaw", Local(23, 2)).MayRun.ShouldBeFalse();
    }

    [Fact]
    public void With_no_lease_yet_only_the_first_fallback_starts()
    {
        LeaseRules.Decide(Config(), null, "speedygonzales", Local(23, 2)).MayRun.ShouldBeTrue();
        LeaseRules.Decide(Config(), null, "pillar", Local(23, 2)).MayRun.ShouldBeFalse();
    }

    [Fact]
    public void Renewing_records_only_the_slot_that_ran()
    {
        var before = new JobLease("skiddaw", Local(22, 2), Local(22, 12, 30), Local(22, 2), Local(21, 12, 30));

        var after = LeaseRules.Renew(before, "skiddaw", Local(22, 12, 40), Local(23, 2), NightlySlot.CatchUp);

        after.LastNightly.ShouldBe(Local(22, 2));
        after.LastCatchUp.ShouldBe(Local(22, 12, 40));
        after.Due.ShouldBe(Local(23, 2));
        LeaseRules.Renew(before, "skiddaw", Local(22, 12, 40), Local(23, 2), NightlySlot.None).LastCatchUp.ShouldBe(Local(21, 12, 30));
    }

    [Fact]
    public void The_lease_file_round_trips_and_a_broken_one_reads_as_no_lease()
    {
        var lease = new JobLease("skiddaw", Local(23, 2, 5), Local(23, 12, 30), Local(23, 2, 5), null);

        var text = LeaseFile.Format(lease);

        text.ShouldBe("holder: \"skiddaw\"\nrenewed: 2026-11-23T02:05:00-08:00\ndue: 2026-11-23T12:30:00-08:00\nnightly: 2026-11-23T02:05:00-08:00\n");
        LeaseFile.Parse(text).ShouldBe(lease);
        LeaseFile.Parse("holder: [unclosed").ShouldBeNull();
        LeaseFile.Parse("holder: skiddaw\n").ShouldBeNull();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("0123")]
    [InlineData("lab \"pc\": 2")]
    public void A_machine_name_that_yaml_would_read_as_something_else_round_trips(string machine)
    {
        var lease = new JobLease(machine, Local(23, 2, 5), Local(23, 12, 30), null, null);

        LeaseFile.Parse(LeaseFile.Format(lease)).ShouldNotBeNull().Holder.ShouldBe(machine);
    }

    [Theory]
    [InlineData(23, 1, 0, null, null, NightlySlot.None)]         // before 2 a.m., the 22nd's run done
    [InlineData(23, 2, 1, null, null, NightlySlot.Nightly)]       // just after 2 a.m.
    [InlineData(23, 9, 0, null, null, NightlySlot.Nightly)]       // the machine slept through 2 a.m.
    [InlineData(23, 9, 0, 2, null, NightlySlot.None)]             // done at 2 a.m.
    [InlineData(23, 13, 0, 2, null, NightlySlot.CatchUp)]         // midday catch-up
    [InlineData(23, 13, 0, 2, 12, NightlySlot.None)]              // catch-up done
    [InlineData(23, 13, 30, 13, null, NightlySlot.None)]           // the nightly ran late, after the catch-up slot
    public void The_planner_knows_which_job_is_due(int day, int hour, int minute, int? nightlyHour, int? catchUpHour, NightlySlot expected)
    {
        DateTimeOffset? lastNightly = nightlyHour is { } n ? Local(23, n, 5) : Local(22, 2, 5);
        DateTimeOffset? lastCatchUp = catchUpHour is { } c ? Local(23, c, 35) : Local(22, 12, 35);
        var lease = new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), lastNightly, lastCatchUp);

        NightlyPlanner.Due(Config(), lease, Local(day, hour, minute), Fixture.Pacific).ShouldBe(expected);
    }

    [Fact]
    public void The_next_slot_is_the_catch_up_or_tomorrow_night()
    {
        NightlyPlanner.NextSlot(Config(), Local(23, 2, 5), Fixture.Pacific).ShouldBe(Local(23, 12, 30));
        NightlyPlanner.NextSlot(Config(), Local(23, 13), Fixture.Pacific).ShouldBe(Local(24, 2));
    }

    [Fact]
    public void A_mistyped_time_falls_back_to_the_default()
    {
        NightlyPlanner.Time("2am", new TimeOnly(2, 0)).ShouldBe(new TimeOnly(2, 0));
        NightlyPlanner.Time("3:15", new TimeOnly(2, 0)).ShouldBe(new TimeOnly(3, 15));
    }

    [Fact]
    public async Task The_designated_machine_runs_the_due_job_and_renews_the_lease()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));
        var ran = new List<NightlySlot>();
        var now = Local(23, 2, 3);
        var jobs = Jobs(store, "skiddaw", () => now, ran);

        var tick = await jobs.TickAsync(TestContext.Current.CancellationToken);

        tick.Ran.ShouldBeTrue();
        ran.ShouldBe([NightlySlot.Nightly]);
        store.Refreshed.ShouldBe(1);
        store.Lease!.LastNightly.ShouldBe(now);
        store.Lease.Due.ShouldBe(Local(23, 12, 30));

        // Checked again ten minutes later: nothing more is due.
        now = Local(23, 2, 13);
        (await jobs.TickAsync(TestContext.Current.CancellationToken)).Ran.ShouldBeFalse();
        ran.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_fallback_does_nothing_while_the_designated_machine_is_on_time()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));
        var ran = new List<NightlySlot>();
        var jobs = Jobs(store, "speedygonzales", () => Local(23, 3), ran);

        var tick = await jobs.TickAsync(TestContext.Current.CancellationToken);

        tick.Slot.ShouldBe(NightlySlot.Nightly);
        tick.Ran.ShouldBeFalse();
        ran.ShouldBeEmpty();
        store.Saved.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_run_renews_the_lease_but_is_not_counted_as_done()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));
        var now = Local(23, 2, 3);
        var jobs = new NightlyJobs(store, () => new DocketConfig { Nightly = Config() }, () => now, "skiddaw",
            (_, _) => Task.FromResult<IReadOnlyList<JobOutcome>>([new JobOutcome("prep", false, "signed out", 0, null)])) { IsOnline = () => true };

        await jobs.TickAsync(TestContext.Current.CancellationToken);

        store.Lease!.Renewed.ShouldBe(now);
        store.Lease.LastNightly.ShouldBe(Local(22, 2, 5));

        // Not retried at every check.
        now = Local(23, 2, 13);
        (await jobs.TickAsync(TestContext.Current.CancellationToken)).Reason.ShouldContain("retry");
    }

    [Fact]
    public async Task Offline_nothing_runs_and_the_lease_is_untouched()
    {
        var store = new FakeLease(null);
        var ran = new List<NightlySlot>();
        var jobs = new NightlyJobs(store, () => new DocketConfig { Nightly = Config() }, () => Local(23, 3), "skiddaw",
            (slot, _) => { ran.Add(slot); return Task.FromResult<IReadOnlyList<JobOutcome>>([]); }) { IsOnline = () => false };

        (await jobs.TickAsync(TestContext.Current.CancellationToken)).Reason.ShouldBe("offline");
        ran.ShouldBeEmpty();
        store.Saved.ShouldBe(0);
    }

    private static NightlyJobs Jobs(FakeLease store, string machine, Func<DateTimeOffset> clock, List<NightlySlot> ran) =>
        new(store, () => new DocketConfig { Nightly = Config() }, clock, machine,
            (slot, _) =>
            {
                ran.Add(slot);
                return Task.FromResult<IReadOnlyList<JobOutcome>>([new JobOutcome("prep", true, null, 0, 0.1m)]);
            })
        { IsOnline = () => true };

    private sealed class FakeLease(JobLease? lease) : ILeaseStore
    {
        public JobLease? Lease { get; private set; } = lease;

        public int Refreshed { get; private set; }

        public int Saved { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshed++;
            return Task.CompletedTask;
        }

        public JobLease? Load() => Lease;

        public Task SaveAsync(JobLease lease, CancellationToken cancellationToken)
        {
            Lease = lease;
            Saved++;
            return Task.CompletedTask;
        }
    }
}
