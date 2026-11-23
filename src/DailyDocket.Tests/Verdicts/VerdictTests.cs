using System.Text.Json;
using DailyDocket.Core.Capture;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Verdicts;
using DailyDocket.Data;
using DailyDocket.Tests.Fixtures;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Tests.Verdicts;

public sealed class VerdictTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-verdict-").FullName;
    private readonly DocketDatabase _db;
    private readonly Store _store;
    private readonly CaptureIngestor _ingestor;
    private readonly List<string> _changes = [];
    private int _calls;

    public VerdictTests()
    {
        _db = DocketDatabase.Open(Path.Combine(_dir, "docket.db"), "test-key-1");
        _store = new Store(_db);
        _ingestor = new CaptureIngestor(_db, Fixture.Config, Fixture.Pacific);
    }

    private string Repo => Path.Combine(_dir, "repo");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static SharedVerdict Mail(string forMessage, DateTimeOffset? at, bool needsReply = true, string why = "Asks for data") =>
        new("mail", "thread-a", forMessage, at, needsReply, why, Now, "skiddaw");

    private static SharedVerdict Slack(string ts, bool needsReply = true) =>
        new("slack", "C123~1732312345.123400", ts, null, needsReply, "Asks a question", Now, "pillar");

    [Fact]
    public void A_verdict_file_round_trips_and_keeps_a_slack_ts_exactly()
    {
        var mail = Mail("19a2c0f3e8b1", new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(-8)));
        var slack = Slack("1732312399.100200");

        VerdictFile.Parse("mail", "thread-a", VerdictFile.Format(mail)).ShouldBe(mail);
        VerdictFile.Parse("slack", slack.Key, VerdictFile.Format(slack)).ShouldBe(slack);
        VerdictFile.Format(slack).ShouldContain("forMessage: \"1732312399.100200\"");
    }

    [Fact]
    public void The_reason_is_capped_at_70_characters_on_one_line()
    {
        var v = Mail("m1", null, why: "Line one\n" + new string('x', 100));

        var back = VerdictFile.Parse("mail", "thread-a", VerdictFile.Format(v))!;

        back.Why.Length.ShouldBe(70);
        back.Why.ShouldStartWith("Line one x");
    }

    [Fact]
    public void A_broken_file_is_skipped()
    {
        VerdictFile.Parse("mail", "t", "forMessage: [x").ShouldBeNull();
        VerdictFile.Parse("mail", "t", "why: no message\n").ShouldBeNull();
    }

    [Fact]
    public void A_verdict_for_an_older_message_never_overwrites_a_newer_one()
    {
        var older = Mail("m1", Now.AddDays(-2), needsReply: true);
        var newer = Mail("m2", Now.AddDays(-1), needsReply: false);

        VerdictRules.ShouldReplace(null, older).ShouldBeTrue();
        VerdictRules.ShouldReplace(older, newer).ShouldBeTrue();
        VerdictRules.ShouldReplace(newer, older).ShouldBeFalse();
    }

    [Fact]
    public void Slack_verdicts_compare_by_ts()
    {
        VerdictRules.ShouldReplace(Slack("1732312345.123400"), Slack("1732312399.000100")).ShouldBeTrue();
        VerdictRules.ShouldReplace(Slack("1732312399.000100"), Slack("1732312345.123400")).ShouldBeFalse();
    }

    [Fact]
    public void The_first_judgment_of_a_message_stands_and_a_known_time_beats_an_unknown_one()
    {
        VerdictRules.ShouldReplace(Mail("m1", Now, needsReply: true), Mail("m1", Now, needsReply: false)).ShouldBeFalse();
        VerdictRules.ShouldReplace(Mail("m1", null), Mail("m2", Now)).ShouldBeTrue();
        VerdictRules.ShouldReplace(Mail("m1", Now), Mail("m2", null)).ShouldBeFalse();
    }

    [Fact]
    public void Merging_into_the_repository_keeps_the_newer_file()
    {
        VerdictFile.Merge(Repo, Mail("m2", Now.AddDays(-1))).ShouldBeTrue();
        VerdictFile.Merge(Repo, Mail("m1", Now.AddDays(-2))).ShouldBeFalse();

        VerdictFile.Load(Repo, "mail", "thread-a")!.ForMessage.ShouldBe("m2");
        File.Exists(Path.Combine(Repo, "verdicts", "email", "thread-a.yaml")).ShouldBeTrue();
    }

    [Fact]
    public void A_verdict_judged_here_is_shared_with_its_message_time()
    {
        Ingest();
        var sync = new VerdictSync(_store, () => Repo, _changes.Add, "skiddaw");
        var jobs = new JobStore(_store, _ingestor, Fixture.Config) { VerdictRecorded = (s, k, f, n, w) => sync.Export(s, k, f, n, w) };

        jobs.RecordVerdict("mail", "thread-a", "msg-a3", true, "Asks which normalization was used");

        var shared = VerdictFile.Load(Repo, "mail", "thread-a")!;
        shared.ForMessage.ShouldBe("msg-a3");
        shared.MessageAt.ShouldNotBeNull();
        shared.MessageAt.ShouldBe(_store.MailMessageTime("msg-a3"));
        shared.Machine.ShouldBe("skiddaw");
        _changes.ShouldHaveSingleItem();
    }

    [Fact]
    public void Another_machines_verdict_is_taken_in_so_the_thread_is_not_judged_again()
    {
        Ingest();
        _store.MailToTriage(Fixture.Config(), Now.AddDays(-30), 10).ShouldHaveSingleItem();
        VerdictFile.Merge(Repo, Mail("msg-a3", new DateTimeOffset(2026, 11, 20, 17, 0, 0, TimeSpan.Zero), needsReply: true, why: "Asks which normalization"));

        new VerdictSync(_store, () => Repo, _changes.Add, "pillar").Import().ShouldBe(1);

        _store.MailToTriage(Fixture.Config(), Now.AddDays(-30), 10).ShouldBeEmpty();
        _store.MailRows(Now.AddDays(-30)).Rows.ShouldHaveSingleItem().Why.ShouldBe("Asks which normalization");
    }

    [Fact]
    public void A_shared_verdict_for_an_older_message_does_not_replace_the_caches_newer_one()
    {
        Ingest();
        _store.RecordVerdict("mail", "thread-a", "msg-a3", needsReply: false, "A thank-you");
        VerdictFile.Merge(Repo, Mail("msg-a1", new DateTimeOffset(2026, 11, 18, 17, 0, 0, TimeSpan.Zero), needsReply: true));

        new VerdictSync(_store, () => Repo, _changes.Add, "pillar").Import().ShouldBe(0);

        _store.CachedVerdicts("mail").ShouldHaveSingleItem().ForMessage.ShouldBe("msg-a3");
    }

    [Fact]
    public void Old_threads_the_deep_sweep_saw_are_on_the_email_tab_and_judged()
    {
        Ingest();
        var waiting = _store.MailRows(Now.AddDays(-30)).Rows.Single().WaitingSince!.Value;
        var after = Now.AddDays(1); // the live sweep's window has moved past this thread

        _store.MailRows(after).Rows.ShouldBeEmpty();
        _store.MailRows(after, deep: new DeepWindow(Now.AddMinutes(-1), waiting.AddHours(1))).Rows.ShouldHaveSingleItem();
        _store.MailToTriage(Fixture.Config(), after, 10, deep: new DeepWindow(Now.AddMinutes(-1), waiting.AddHours(1))).ShouldHaveSingleItem();

        // Younger than the deep sweep's reach (still inside the live window): left to the live sweep's rule.
        _store.MailRows(after, deep: new DeepWindow(Now.AddMinutes(-1), waiting.AddHours(-1))).Rows.ShouldBeEmpty();
    }

    [Fact]
    public void The_deep_sweep_reads_older_inbox_mail_only_and_records_its_start()
    {
        var plan = JobPlans.DeepSweepRun(Fixture.Config(), Now, Fixture.Pacific, "haiku");

        // One search per day from 31 to 90 days back. The test's Now is set in this file.
        var days = System.Text.RegularExpressions.Regex.Matches(plan.Prompt, "after:").Count;
        days.ShouldBe(60);
        plan.Prompt.ShouldContain("THREAD_VIEW_SUMMARY");
        plan.AllowedTools.ShouldNotContain(t => ConnectorTools.Writes.Contains(t));
        ConnectorTools.Writes.ShouldAllBe(w => plan.DisallowedTools.Contains(w));
        plan.Capture.ShouldBeTrue();
        plan.Watermarks.ShouldContainKey("mail/deep");
    }

    [Fact]
    public void The_job_store_knows_the_deep_window_once_a_deep_sweep_ran()
    {
        var jobs = new JobStore(_store, _ingestor, Fixture.Config);
        jobs.Deep(Now).ShouldBeNull();

        _store.SetWatermark("mail", "deep", Now.AddHours(-10).ToString("O"));

        jobs.Deep(Now).ShouldBe(new DeepWindow(Now.AddHours(-10).AddMinutes(-1), Now.AddDays(-30)));
    }

    private void Ingest()
    {
        _ingestor.Ingest("run-1", Call("mcp__claude_ai_Gmail__search_threads", new { query = "in:inbox newer_than:30d" }, Fixture.Read("gmail-search-summary.json"))).ShouldBeTrue();
        _ingestor.Ingest("run-1", Call("mcp__claude_ai_Gmail__get_thread", new { threadId = "thread-a" }, Fixture.Read("gmail-thread.json"))).ShouldBeTrue();
    }

    private CapturedCall Call(string tool, object input, string response) =>
        new(tool, JsonSerializer.SerializeToElement(input), response, $"toolu_{++_calls}", "s", Now);
}
