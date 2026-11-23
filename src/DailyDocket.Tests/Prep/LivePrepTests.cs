using DailyDocket.Core.Configuration;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Prep;
using DailyDocket.Core.Processes;
using DailyDocket.Data;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Tests.Prep;

/// <summary>
/// Opt-in: preps ONE real upcoming meeting, read-only, into a temporary folder (never the data
/// repository, nothing pushed). Set DAILYDOCKET_LIVE_PREP=1 and DAILYDOCKET_LIVE to the folder
/// holding docket.yaml; DAILYDOCKET_PREP_TITLE picks the meeting by title. Needs Claude Code
/// signed in, and the hook built.
/// </summary>
public sealed class LivePrepTests(ITestOutputHelper output)
{
    [Fact]
    public async Task One_meeting_gets_a_brief()
    {
        var live = Environment.GetEnvironmentVariable("DAILYDOCKET_LIVE");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DAILYDOCKET_LIVE_PREP") != "1" || string.IsNullOrEmpty(live), "Set DAILYDOCKET_LIVE_PREP=1 and DAILYDOCKET_LIVE.");

        var config = DocketConfig.Load(Path.Combine(live!, "docket.yaml"));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.Schedule.TimeZone);
        var root = Directory.CreateTempSubdirectory("dd-liveprep-").FullName;
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var repo = Path.Combine(root, "repo");
        var hook = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DailyDocket.Hook", "bin", "Debug", "net10.0", "DailyDocket.Hook.exe"));

        var db = DocketDatabase.Open(paths.DatabaseFile, "live-test");
        var store = new Store(db);
        var jobs = new JobStore(store, new CaptureIngestor(db, () => config, zone), () => config);
        var tools = new ToolLocator();
        var runner = new JobRunner(tools, new ProcessRunner(tools), paths, jobs, () => config, hook);
        var now = DateTimeOffset.UtcNow;

        var calendar = await runner.RunAsync(JobPlans.CalendarPull(config, now, zone, "haiku"), TestContext.Current.CancellationToken);
        output.WriteLine($"calendar: {(calendar.Ok ? "ok" : calendar.Detail)} captures={calendar.Captured} cost={calendar.CostUsd:0.000}");

        var title = Environment.GetEnvironmentVariable("DAILYDOCKET_PREP_TITLE");
        var targets = PrepRules.Select(store.Events(now, now.AddDays(6)), config, now, zone);
        output.WriteLine($"targets in the next {config.Prep.WorkingDays} working days: {targets.Count}");
        var target = targets.FirstOrDefault(t => title is not null && t.Event.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) ?? targets.First();

        var prep = new MeetingPrep(runner.RunAsync, () => repo, () => config, () => "sonnet", store.Events, () => [], _ => { }, AppInfo.MachineName);
        var outcome = await prep.PrepNowAsync(target.Event.EventId, now, TestContext.Current.CancellationToken);

        output.WriteLine($"prep: {(outcome!.Ok ? "ok" : outcome.Detail)} cost={outcome.CostUsd:0.000}");
        output.WriteLine($"brief: {BriefFile.PathFor(repo, target.Date, target.Event.EventId)}");
        output.WriteLine($"data folder: {root}");

        // The test runner hides output from a passing test; the summary is kept beside the data too.
        File.WriteAllText(Path.Combine(root, "live-prep.txt"), $"calendar cost={calendar.CostUsd:0.000}\nprep ok={outcome.Ok} cost={outcome.CostUsd:0.000} {outcome.Detail}\n");
        SqliteConnection.ClearAllPools();
        outcome.Ok.ShouldBeTrue();
        BriefFile.Load(repo, target.Date, target.Event.EventId).ShouldNotBeNull();
    }

    /// <summary>Opt-in: lists the Google Drive tools the connector offers in headless mode (ToolSearch only; nothing is read).</summary>
    [Fact]
    public async Task Drive_tool_names()
    {
        var live = Environment.GetEnvironmentVariable("DAILYDOCKET_LIVE");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DAILYDOCKET_LIVE_PREP") != "1" || string.IsNullOrEmpty(live), "Set DAILYDOCKET_LIVE_PREP=1 and DAILYDOCKET_LIVE.");

        var config = DocketConfig.Load(Path.Combine(live!, "docket.yaml"));
        var root = Directory.CreateTempSubdirectory("dd-probe-").FullName;
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var db = DocketDatabase.Open(paths.DatabaseFile, "live-test");
        var jobs = new JobStore(new Store(db), new CaptureIngestor(db, () => config, TimeZoneInfo.Local), () => config);
        var tools = new ToolLocator();
        var runner = new JobRunner(tools, new ProcessRunner(tools), paths, jobs, () => config, "unused-no-capture");

        var probe = await runner.RunAsync(new JobPlan(
            "probe", "haiku",
            "Call ToolSearch once with the query \"google drive file search read\" and max_results 10. Then reply with only the full names of the tools it returned, one per line.",
            ["ToolSearch"], ConnectorTools.ReaderDisallowed, false, 4, new Dictionary<string, string>()), TestContext.Current.CancellationToken);

        File.WriteAllText(Path.Combine(root, "drive-tools.txt"), $"{(probe.Ok ? "ok" : probe.Detail)} cost={probe.CostUsd:0.000}\n{probe.FinalText}\n");
        output.WriteLine($"probe written to {root}");
        SqliteConnection.ClearAllPools();
        probe.Ok.ShouldBeTrue();
    }

    /// <summary>Opt-in: prints each run's job, outcome and cost from a live test's data folder (DAILYDOCKET_INSPECT).</summary>
    [Fact]
    public void Print_run_costs()
    {
        var folder = Environment.GetEnvironmentVariable("DAILYDOCKET_INSPECT");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "Set DAILYDOCKET_INSPECT to a live test's data folder.");

        var db = DocketDatabase.Open(Path.Combine(folder!, "docket.db"), "live-test");
        using (var c = db.Connect())
        {
            var lines = Dapper.SqlMapper.Query<(string Job, string? Outcome, double? Cost, string Started, string? Ended)>(c, "SELECT job, outcome, cost_usd, started_utc, ended_utc FROM job_run ORDER BY started_utc")
                .Select(r => $"{r.Job} {r.Outcome} ${r.Cost:0.000} {r.Started} -> {r.Ended}");
            File.WriteAllLines(Path.Combine(folder!, "run-costs.txt"), lines);
        }

        SqliteConnection.ClearAllPools();
    }
}