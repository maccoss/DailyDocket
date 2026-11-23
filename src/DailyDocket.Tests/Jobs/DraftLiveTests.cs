using DailyDocket.Core.Configuration;
using DailyDocket.Core.Drafting;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Mail;
using DailyDocket.Core.Processes;
using DailyDocket.Data;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Tests.Jobs;

/// <summary>
/// Opt-in: drafts a reply to one owed thread in a live test's database (DAILYDOCKET_INSPECT, with
/// DAILYDOCKET_LIVE for docket.yaml) and prints it. Sends nothing: the draft run has no tools.
/// </summary>
public sealed class DraftLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Claude_drafts_a_reply_from_the_cache()
    {
        var folder = Environment.GetEnvironmentVariable("DAILYDOCKET_INSPECT");
        var live = Environment.GetEnvironmentVariable("DAILYDOCKET_LIVE");
        Assert.SkipWhen(string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(live), "Set DAILYDOCKET_INSPECT and DAILYDOCKET_LIVE.");

        var config = DocketConfig.Load(Path.Combine(live!, "docket.yaml"));
        var db = DocketDatabase.Open(Path.Combine(folder!, "docket.db"), "live-test");
        var store = new Store(db);
        var jobs = new JobStore(store, new CaptureIngestor(db, () => config, TimeZoneInfo.Local), () => config);
        // DAILYDOCKET_DRAFT_SUBJECT picks a thread by subject; DAILYDOCKET_GUIDES is a config/replies folder.
        var subject = Environment.GetEnvironmentVariable("DAILYDOCKET_DRAFT_SUBJECT");
        var rows = store.MailRows(DateTimeOffset.UtcNow.AddDays(-30)).Rows;
        var row = rows.FirstOrDefault(r => subject is not null && r.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase)) ?? rows.First(r => r.State == "Owed");
        var conversation = string.Join("\n\n", store.ThreadMessages(row.ThreadId).Select(m => $"--- From: {m.Sender}\n{MailText.SplitQuoted(m.Body).Message}"));
        var guides = Environment.GetEnvironmentVariable("DAILYDOCKET_GUIDES") is { } folder2 ? ReplyGuide.Load(folder2) : [];
        var guide = ReplyGuide.Choose(guides, conversation);
        output.WriteLine($"Guide: {guide?.Title ?? "(none)"}");

        var paths = new AppPaths(Directory.CreateTempSubdirectory("dd-draft-").FullName);
        paths.EnsureCreated();
        var tools = new ToolLocator();
        var runner = new JobRunner(tools, new ProcessRunner(tools), paths, jobs, () => config, "unused.exe");
        var outcome = await runner.RunAsync(DraftPlans.Draft(config, true, conversation, store.RecentFromMe(config, 4), null, "sonnet", guide));

        output.WriteLine($"Thread: {row.Subject} (from {row.WaitingOn}); ok={outcome.Ok} cost={outcome.CostUsd:0.000} {outcome.Detail}");
        output.WriteLine(DraftPlans.HouseStyle(outcome.FinalText ?? ""));
        SqliteConnection.ClearAllPools();
        outcome.Ok.ShouldBeTrue();
        outcome.FinalText.ShouldNotBeNullOrWhiteSpace();
    }
}
