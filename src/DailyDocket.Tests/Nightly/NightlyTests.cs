using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Nightly;
using DailyDocket.Core.Prep;
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
    public void The_latest_slot_is_the_nightly_or_the_catch_up_whichever_came_last()
    {
        NightlyPlanner.LatestSlot(Config(), Local(23, 9), Fixture.Pacific).ShouldBe(Local(23, 2));
        NightlyPlanner.LatestSlot(Config(), Local(23, 13), Fixture.Pacific).ShouldBe(Local(23, 12, 30));
        NightlyPlanner.LatestSlot(Config(), Local(23, 1), Fixture.Pacific).ShouldBe(Local(22, 12, 30));
    }

    [Fact]
    public void A_live_claim_by_another_machine_stops_everyone_until_it_is_abandoned()
    {
        var claimed = new JobLease("speedygonzales", Local(23, 4, 5), Local(23, 2), Local(22, 2, 5), null, Running: Local(23, 4, 5));

        LeaseRules.Decide(Config(), claimed, "skiddaw", Local(23, 5)).MayRun.ShouldBeFalse();
        LeaseRules.Decide(Config(), claimed, "pillar", Local(23, 7)).MayRun.ShouldBeFalse();
        LeaseRules.Decide(Config(), claimed, "speedygonzales", Local(23, 5)).MayRun.ShouldBeTrue();

        // Hours later the run is taken to have died with its machine.
        LeaseRules.Decide(Config(), claimed, "skiddaw", Local(23, 4, 5) + NightlyJobs.AbandonAfter).MayRun.ShouldBeTrue();
    }

    [Fact]
    public void A_fallback_yields_while_the_designated_machine_is_on_duty_for_the_slot()
    {
        // The designated machine failed the 2 a.m. run once and is retrying: its due time has
        // passed, but it renewed after the slot, so the fallback leaves it alone.
        var lease = new JobLease("skiddaw", Local(23, 3, 10), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35), Failures: 1, FailedSlot: Local(23, 2));

        LeaseRules.Decide(Config(), lease, "speedygonzales", Local(23, 6), Local(23, 2)).MayRun.ShouldBeFalse();
    }

    [Fact]
    public void A_fallback_takes_over_after_two_failed_slots()
    {
        var lease = new JobLease("skiddaw", Local(23, 12, 40), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35), Failures: 2, FailedSlot: Local(23, 12, 30));

        LeaseRules.Decide(Config(), lease, "speedygonzales", Local(23, 12, 50), Local(23, 12, 30)).MayRun.ShouldBeTrue();
        LeaseRules.Decide(Config(), lease, "pillar", Local(23, 12, 50), Local(23, 12, 30)).MayRun.ShouldBeFalse();
        LeaseRules.Decide(Config(), lease, "pillar", Local(23, 14, 50), Local(23, 12, 30)).MayRun.ShouldBeTrue();
    }

    [Fact]
    public void Failures_count_once_per_slot_and_reset_on_success_or_a_new_holder()
    {
        var start = new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35));

        var once = LeaseRules.Fail(LeaseRules.Claim(start, "skiddaw", Local(23, 2, 3)), "skiddaw", Local(23, 2, 30), Local(23, 2));
        once.Failures.ShouldBe(1);
        once.Due.ShouldBe(Local(23, 2)); // not moved: a failing holder falls behind
        once.Running.ShouldBeNull();
        LeaseRules.Fail(once, "skiddaw", Local(23, 3, 30), Local(23, 2)).Failures.ShouldBe(1);
        var twice = LeaseRules.Fail(once, "skiddaw", Local(23, 12, 40), Local(23, 12, 30));
        twice.Failures.ShouldBe(2);

        LeaseRules.Renew(twice, "skiddaw", Local(23, 13), Local(24, 2), NightlySlot.Nightly).Failures.ShouldBe(0);
        LeaseRules.Claim(twice, "speedygonzales", Local(23, 13)).Failures.ShouldBe(0);
    }

    [Fact]
    public void The_lease_file_keeps_the_claim_and_the_failures()
    {
        var lease = new JobLease("skiddaw", Local(23, 3, 5), Local(23, 2), Local(22, 2, 5), null, Local(23, 3, 5), 1, Local(23, 2));

        var text = LeaseFile.Format(lease);

        text.ShouldContain("running: 2026-11-23T03:05:00-08:00\nfailures: 1\nfailedSlot: 2026-11-23T02:00:00-08:00\n");
        LeaseFile.Parse(text).ShouldBe(lease);
    }

    [Fact]
    public async Task The_designated_machine_claims_runs_and_renews_the_lease()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));
        var ran = new List<NightlySlot>();
        var now = Local(23, 2, 3);
        store.OnSave = lease =>
        {
            // The claim reaches the other machines before any work starts.
            if (store.Saved == 1)
            {
                lease.Running.ShouldBe(now);
                ran.ShouldBeEmpty();
            }

            return lease;
        };
        var jobs = Jobs(store, "skiddaw", () => now, ran);

        var tick = await jobs.TickAsync(TestContext.Current.CancellationToken);

        tick.Ran.ShouldBeTrue();
        tick.Problem.ShouldBeNull();
        ran.ShouldBe([NightlySlot.Nightly]);
        store.Refreshed.ShouldBe(1);
        store.Saved.ShouldBe(2);
        store.Lease!.LastNightly.ShouldBe(now);
        store.Lease.Due.ShouldBe(Local(23, 12, 30));
        store.Lease.Running.ShouldBeNull();

        // Checked again ten minutes later: nothing more is due.
        now = Local(23, 2, 13);
        (await jobs.TickAsync(TestContext.Current.CancellationToken)).Ran.ShouldBeFalse();
        ran.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_machine_that_lost_the_race_for_the_claim_backs_off()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));

        // The other machine pushed its claim first: the sync keeps the remote's copy.
        store.OnSave = _ => new JobLease("speedygonzales", Local(23, 2, 2), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35), Running: Local(23, 2, 2));
        var ran = new List<NightlySlot>();
        var jobs = Jobs(store, "skiddaw", () => Local(23, 2, 3), ran);

        var tick = await jobs.TickAsync(TestContext.Current.CancellationToken);

        tick.Ran.ShouldBeFalse();
        tick.Reason.ShouldContain("speedygonzales claimed this run first");
        ran.ShouldBeEmpty();
        store.Saved.ShouldBe(1);
    }

    [Fact]
    public async Task A_claim_that_could_not_be_sent_is_not_trusted()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35))) { Sends = false };
        var ran = new List<NightlySlot>();

        var tick = await Jobs(store, "skiddaw", () => Local(23, 2, 3), ran).TickAsync(TestContext.Current.CancellationToken);

        tick.Ran.ShouldBeFalse();
        ran.ShouldBeEmpty();
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
    public async Task A_failed_run_is_not_done_does_not_move_the_due_time_and_says_why()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));
        var now = Local(23, 2, 3);
        NightlyTick? seen = null;
        var jobs = new NightlyJobs(store, () => new DocketConfig { Nightly = Config() }, () => now, "skiddaw",
            (_, _) => Task.FromResult(new NightlyWork([new JobOutcome("deep", false, "Claude Code is not signed in. Run claude auth login --claudeai.", 0, null)], [])))
        { IsOnline = () => true };
        jobs.Finished += t => seen = t;

        await jobs.TickAsync(TestContext.Current.CancellationToken);

        store.Lease!.Renewed.ShouldBe(now);
        store.Lease.Due.ShouldBe(Local(23, 2));
        store.Lease.LastNightly.ShouldBe(Local(22, 2, 5));
        store.Lease.Failures.ShouldBe(1);
        seen.ShouldNotBeNull().Problem.ShouldNotBeNull().ShouldContain("claude auth login");

        // Not retried at every check.
        now = Local(23, 2, 13);
        (await jobs.TickAsync(TestContext.Current.CancellationToken)).Reason.ShouldContain("retry");
    }

    [Fact]
    public async Task One_failed_brief_does_not_rerun_the_sweep_and_is_retried_alone_a_few_times()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));
        var now = Local(23, 2, 3);
        var requests = new List<NightlyRequest>();
        var jobs = new NightlyJobs(store, () => new DocketConfig { Nightly = Config() }, () => now, "skiddaw",
            (request, _) =>
            {
                requests.Add(request);
                IReadOnlyList<JobOutcome> sweep = request.OnlyEvents is null ? [new JobOutcome("deep", true, null, 3, 0.1m)] : [];
                IReadOnlyList<PrepResult> briefs = request.OnlyEvents is { } only
                    ? [.. only.Select(id => Brief(id, false))]
                    : [Brief("e1", true), Brief("e2", false)];
                return Task.FromResult(new NightlyWork(sweep, briefs));
            })
        { IsOnline = () => true };

        var first = await jobs.TickAsync(TestContext.Current.CancellationToken);

        first.Problem.ShouldNotBeNull().ShouldContain("\"Meeting e2\"");
        store.Lease!.LastNightly.ShouldBe(now); // the sweep succeeded: the slot is done
        store.Lease.Failures.ShouldBe(0);
        jobs.PendingBriefRetries.ShouldBe(["e2"]);

        // Not before the hour is up.
        now = Local(23, 2, 33);
        (await jobs.TickAsync(TestContext.Current.CancellationToken)).Ran.ShouldBeFalse();

        // Then only e2, and at most MaxBriefAttempts tries in all.
        for (var hour = 3; hour <= 6; hour++)
        {
            now = Local(23, hour, 10);
            await jobs.TickAsync(TestContext.Current.CancellationToken);
        }

        requests.Count.ShouldBe(NightlyJobs.MaxBriefAttempts);
        requests.Skip(1).ShouldAllBe(r => r.OnlyEvents!.Single() == "e2");
        jobs.PendingBriefRetries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_mid_run_withdraws_the_claim_on_disk_without_syncing()
    {
        var store = new FakeLease(new JobLease("skiddaw", Local(22, 12, 35), Local(23, 2), Local(22, 2, 5), Local(22, 12, 35)));
        using var closing = new CancellationTokenSource();
        var jobs = new NightlyJobs(store, () => new DocketConfig { Nightly = Config() }, () => Local(23, 2, 3), "skiddaw",
            async (_, ct) =>
            {
                await closing.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return new NightlyWork([], []);
            })
        { IsOnline = () => true };

        await Should.ThrowAsync<OperationCanceledException>(() => jobs.TickAsync(closing.Token));

        store.Saved.ShouldBe(1); // the claim only
        store.SavedLocally.ShouldBe(1);
        store.Lease!.Running.ShouldBeNull();
        store.Lease.LastNightly.ShouldBe(Local(22, 2, 5));
    }

    [Fact]
    public async Task Offline_nothing_runs_and_the_lease_is_untouched()
    {
        var store = new FakeLease(null);
        var ran = new List<NightlySlot>();
        var jobs = new NightlyJobs(store, () => new DocketConfig { Nightly = Config() }, () => Local(23, 3), "skiddaw",
            (request, _) => { ran.Add(request.Slot); return Task.FromResult(new NightlyWork([], [])); }) { IsOnline = () => false };

        (await jobs.TickAsync(TestContext.Current.CancellationToken)).Reason.ShouldBe("offline");
        ran.ShouldBeEmpty();
        store.Saved.ShouldBe(0);
    }

    [Fact]
    public void A_catch_up_is_done_when_any_brief_was_written_or_none_was_needed()
    {
        NightlyJobs.Succeeded(NightlySlot.CatchUp, new NightlyWork([], [])).ShouldBeTrue();
        NightlyJobs.Succeeded(NightlySlot.CatchUp, new NightlyWork([], [Brief("e1", false), Brief("e2", true)])).ShouldBeTrue();
        NightlyJobs.Succeeded(NightlySlot.CatchUp, new NightlyWork([], [Brief("e1", false)])).ShouldBeFalse();
        NightlyJobs.Succeeded(NightlySlot.Nightly, new NightlyWork([new JobOutcome("deep", false, "x", 0, null)], [Brief("e1", true)])).ShouldBeFalse();
    }

    private static PrepResult Brief(string id, bool ok) =>
        new(id, $"Meeting {id}", new JobOutcome("prep", ok, ok ? null : "The prep run was refused by the Calendar connector.", 0, 0.2m));

    private static NightlyJobs Jobs(FakeLease store, string machine, Func<DateTimeOffset> clock, List<NightlySlot> ran) =>
        new(store, () => new DocketConfig { Nightly = Config() }, clock, machine,
            (request, _) =>
            {
                ran.Add(request.Slot);
                return Task.FromResult(new NightlyWork(
                    request.Slot == NightlySlot.Nightly ? [new JobOutcome("deep", true, null, 0, 0.1m)] : [],
                    [Brief("e1", true)]));
            })
        { IsOnline = () => true };

    private sealed class FakeLease(JobLease? lease) : ILeaseStore
    {
        public JobLease? Lease { get; private set; } = lease;

        public int Refreshed { get; private set; }

        public int Saved { get; private set; }

        public int SavedLocally { get; private set; }

        /// <summary>Whether a save reaches the remote.</summary>
        public bool Sends { get; init; } = true;

        /// <summary>What the sync leaves after a save (another machine's claim, when it won the race).</summary>
        public Func<JobLease, JobLease>? OnSave { get; set; }

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshed++;
            return Task.CompletedTask;
        }

        public JobLease? Load() => Lease;

        public Task<bool> SaveAsync(JobLease lease, CancellationToken cancellationToken)
        {
            Saved++;
            Lease = OnSave?.Invoke(lease) ?? lease;
            return Task.FromResult(Sends);
        }

        public void SaveLocally(JobLease lease)
        {
            SavedLocally++;
            Lease = lease;
        }
    }
}
