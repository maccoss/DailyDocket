using DailyDocket.Core.Configuration;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Processes;
using DailyDocket.Data;
using Microsoft.Data.Sqlite;
using Xunit.Sdk;

namespace DailyDocket.Tests.Jobs;

/// <summary>
/// Opt-in: runs the real pull and triage jobs against the owner's accounts, read-only, into a fresh
/// database in a temporary folder. Set DAILYDOCKET_LIVE to the folder holding docket.yaml (normally
/// %LOCALAPPDATA%\DailyDocket). Needs Claude Code signed in, and the hook built.
/// </summary>
public sealed class LivePullTests(ITestOutputHelper output)
{
    private static readonly string? ConfigFolder = Environment.GetEnvironmentVariable("DAILYDOCKET_LIVE");

    [Fact]
    public async Task The_jobs_fill_every_tab()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConfigFolder), "Set DAILYDOCKET_LIVE to the folder holding docket.yaml.");

        var config = DocketConfig.Load(Path.Combine(ConfigFolder!, "docket.yaml"));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.Schedule.TimeZone);
        var root = Directory.CreateTempSubdirectory("dd-live-").FullName;
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        // The hook is built beside the tests (they reference it), wherever the build output goes.
        var hook = Path.Combine(AppContext.BaseDirectory, "DailyDocket.Hook.exe");
        File.Exists(hook).ShouldBeTrue($"the hook should be built beside the tests at {hook}");

        var db = DocketDatabase.Open(paths.DatabaseFile, "live-test");
        var store = new Store(db);
        var jobs = new JobStore(store, new CaptureIngestor(db, () => config, zone), () => config);
        var tools = new ToolLocator();
        var runner = new JobRunner(tools, new ProcessRunner(tools), paths, jobs, () => config, hook);
        var now = DateTimeOffset.UtcNow;

        var outcomes = await Task.WhenAll(
            runner.RunAsync(JobPlans.CalendarPull(config, now, zone, "haiku")),
            runner.RunAsync(JobPlans.MailPull(config, now, null, "haiku")),
            runner.RunAsync(JobPlans.SlackPull(config, now, zone, null, "haiku")));
        // One conversation per run: a few runs show the per-conversation cost without judging them all.
        var triage = await JobPlans.TriageAllAsync(jobs, plan => runner.RunAsync(plan), "sonnet", 5);
        output.WriteLine($"triage runs={triage.Count} total cost={triage.Sum(o => o.CostUsd ?? 0):0.000} left={jobs.TriageBatch(DateTimeOffset.UtcNow, 1000).Count}");

        foreach (var o in outcomes.Concat(triage))
        {
            output.WriteLine($"{o.Job}: {(o.Ok ? "ok" : "FAILED")} captures={o.Captured} cost={o.CostUsd:0.000} {o.Detail}");
        }

        var schedule = store.Schedule(config, now, zone);
        var (mail, counts) = store.MailRows(jobs.MailSince(now));
        var slack = store.SlackRows(config, jobs.SlackSince(now));
        output.WriteLine($"schedule days={schedule.Count} events={schedule.Sum(d => d.Slots.Count + d.Background.Count)}");
        output.WriteLine($"mail owed={counts.Owed} reading={counts.Reading} quiet={counts.Quiet} drafts={mail.Count(m => m.State == "Draft")} toRead={store.ThreadsToRead(now.AddDays(-30), 1000).Count}");
        output.WriteLine($"slack owed={slack.Count(s => s.State == "Owed")} reading={slack.Count(s => s.State == "Reading")}");
        output.WriteLine($"data folder: {root}");

        SqliteConnection.ClearAllPools();
        outcomes.ShouldAllBe(o => o.Ok);
        schedule.ShouldNotBeEmpty();
    }
}
