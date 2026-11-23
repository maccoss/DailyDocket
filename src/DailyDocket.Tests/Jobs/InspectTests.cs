using DailyDocket.Core.Configuration;
using DailyDocket.Data;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Tests.Jobs;

/// <summary>
/// Opt-in: prints what each tab would show from a live test's database, for checking the rules by
/// eye. Set DAILYDOCKET_INSPECT to that run's data folder. Prints names, subjects and Claude's
/// reasons only, never bodies.
/// </summary>
public sealed class InspectTests(ITestOutputHelper output)
{
    [Fact]
    public void Print_the_tabs()
    {
        var folder = Environment.GetEnvironmentVariable("DAILYDOCKET_INSPECT");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "Set DAILYDOCKET_INSPECT to a live test's data folder.");

        var config = DocketConfig.Load(Path.Combine(Environment.GetEnvironmentVariable("DAILYDOCKET_LIVE") ?? folder!, "docket.yaml"));
        var db = DocketDatabase.Open(Path.Combine(folder!, "docket.db"), "live-test");
        var store = new Store(db);
        var now = DateTimeOffset.UtcNow;

        foreach (var r in store.MailRows(now.AddDays(-30)).Rows)
        {
            output.WriteLine($"MAIL {r.State,-7} {r.WaitingSince:MM-dd} {r.WaitingOn} | {r.Subject} | {r.Why}");
        }

        foreach (var r in store.SlackRows(config, now.AddDays(-21)))
        {
            output.WriteLine($"SLACK {r.State,-7} {r.When:MM-dd} {r.From} in {r.Where} | {r.Why}");
        }

        SqliteConnection.ClearAllPools();
    }
}
