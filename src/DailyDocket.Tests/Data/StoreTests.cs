using System.Text.Json;
using System.Text.Json.Nodes;
using DailyDocket.Core.Capture;
using DailyDocket.Data;
using DailyDocket.Tests.Fixtures;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Tests.Data;

public sealed class StoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-store-").FullName;
    private readonly DocketDatabase _db;
    private readonly Store _store;
    private readonly CaptureIngestor _ingestor;
    private int _calls;

    public StoreTests()
    {
        _db = DocketDatabase.Open(Path.Combine(_dir, "docket.db"), "test-key-1");
        _store = new Store(_db);
        _ingestor = new CaptureIngestor(_db, Fixture.Config, Fixture.Pacific);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private CapturedCall Call(string tool, object input, string response) =>
        new(tool, JsonSerializer.SerializeToElement(input), response, $"toolu_{++_calls}", "s", Now);

    private void Ingest(string tool, object input, string response) =>
        _ingestor.Ingest("run-1", Call(tool, input, response)).ShouldBeTrue();

    private const string Search = "mcp__claude_ai_Gmail__search_threads";
    private const string GetThread = "mcp__claude_ai_Gmail__get_thread";
    private const string Drafts = "mcp__claude_ai_Gmail__list_drafts";

    [Fact]
    public void The_database_is_encrypted()
    {
        _db.CipherVersion().ShouldNotBeNullOrEmpty();

        // Without the key the file is unreadable.
        var plain = new SqliteConnection($"Data Source={_db.Path};Pooling=False");
        plain.Open();
        using var cmd = plain.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        Should.Throw<SqliteException>(() => cmd.ExecuteScalar());
        plain.Dispose();
    }

    [Fact]
    public void A_database_the_key_cannot_open_is_rebuilt_empty()
    {
        Ingest(Search, new { query = "in:inbox newer_than:30d" }, Fixture.Read("gmail-search-summary.json"));
        SqliteConnection.ClearAllPools();

        var reopened = new Store(DocketDatabase.Open(_db.Path, "another-key"));

        reopened.ThreadsToRead(Now.AddDays(-30), 10).ShouldBeEmpty();
    }

    [Fact]
    public void A_searched_thread_is_read_once_until_a_newer_message_arrives()
    {
        Ingest(Search, new { query = "in:inbox newer_than:30d", view = "THREAD_VIEW_SUMMARY" }, Fixture.Read("gmail-search-summary.json"));
        _store.ThreadsToRead(Now.AddDays(-30), 10).ShouldBe(["thread-a", "thread-b"], ignoreOrder: true);

        Ingest(GetThread, new { threadId = "thread-a" }, Fixture.Read("gmail-thread.json"));
        _store.ThreadsToRead(Now.AddDays(-30), 10).ShouldBe(["thread-b"]);

        var newer = Fixture.Read("gmail-search-summary.json").Replace("\"msg-a3\"", "\"msg-a4\"", StringComparison.Ordinal);
        Ingest(Search, new { query = "in:inbox newer_than:30d" }, newer);
        _store.ThreadsToRead(Now.AddDays(-30), 10).ShouldContain("thread-a");
    }

    [Fact]
    public void Only_inbox_searches_put_threads_on_the_email_tab()
    {
        Ingest(Search, new { query = "in:sent newer_than:7d" }, Fixture.Read("gmail-search-summary.json"));

        _store.ThreadsToRead(Now.AddDays(-30), 10).ShouldBeEmpty();
    }

    [Fact]
    public void An_open_thread_is_reading_until_judged_then_owed_with_the_reason()
    {
        Ingest(Search, new { query = "in:inbox newer_than:30d" }, Fixture.Read("gmail-search-summary.json"));
        Ingest(GetThread, new { threadId = "thread-a" }, Fixture.Read("gmail-thread.json"));

        var (rows, counts) = _store.MailRows(Now.AddDays(-30));
        rows.ShouldHaveSingleItem().State.ShouldBe("Reading");
        counts.Reading.ShouldBe(1);

        var item = _store.MailToTriage(Fixture.Config(), Now.AddDays(-30), 10).ShouldHaveSingleItem();
        item.ForMessage.ShouldBe("msg-a3");
        item.Transcript.ShouldContain("which normalization");

        _store.RecordVerdict("mail", item.Key, item.ForMessage, needsReply: true, "Asks which normalization was used");

        var owed = _store.MailRows(Now.AddDays(-30)).Rows.ShouldHaveSingleItem();
        owed.State.ShouldBe("Owed");
        owed.Why.ShouldBe("Asks which normalization was used");
        owed.WaitingOn.ShouldBe("Pat Example");
        _store.MailToTriage(Fixture.Config(), Now.AddDays(-30), 10).ShouldBeEmpty();
    }

    [Fact]
    public void A_thread_judged_as_needing_nothing_leaves_the_tab()
    {
        Ingest(Search, new { query = "in:inbox newer_than:30d" }, Fixture.Read("gmail-search-summary.json"));
        Ingest(GetThread, new { threadId = "thread-a" }, Fixture.Read("gmail-thread.json"));
        _store.RecordVerdict("mail", "thread-a", "msg-a3", needsReply: false, "A thank-you");

        var (rows, counts) = _store.MailRows(Now.AddDays(-30));

        rows.ShouldBeEmpty();
        counts.Quiet.ShouldBe(1);
    }

    [Fact]
    public void An_unsent_draft_puts_the_thread_first_and_its_removal_takes_it_off()
    {
        var lastWord = """{"id":"thread-c","messages":[{"id":"c1","threadId":"thread-c","sender":"pat@example.org","toRecipients":["owner@example.org"],"internalDate":"1795280400000","labelIds":["INBOX"],"subject":"Question","plaintextBody":"?"},{"id":"c2","threadId":"thread-c","sender":"owner@example.org","toRecipients":["pat@example.org"],"internalDate":"1795366800000","labelIds":["SENT"],"plaintextBody":"Answer"}]}""";
        Ingest(Search, new { query = "in:inbox" }, """{"threads":[{"id":"thread-c","messageCount":2,"messages":[{"id":"c2","sender":"owner@example.org","threadId":"thread-c"}]}]}""");
        Ingest(GetThread, new { threadId = "thread-c" }, lastWord);
        _store.MailRows(Now.AddDays(-30)).Rows.ShouldBeEmpty();

        Ingest(Drafts, new { pageSize = 50 }, """{"drafts":[{"id":"d1","threadId":"thread-c","date":"2026-11-20T10:00:00Z"}]}""");
        _store.MailRows(Now.AddDays(-30)).Rows.ShouldHaveSingleItem().State.ShouldBe("Draft");

        Ingest(Drafts, new { pageSize = 50 }, "{}");
        _store.MailRows(Now.AddDays(-30)).Rows.ShouldBeEmpty();
    }

    [Fact]
    public void The_same_capture_is_ingested_once()
    {
        var call = Call(Search, new { query = "in:inbox" }, Fixture.Read("gmail-search-summary.json"));

        _ingestor.Ingest("run-1", call).ShouldBeTrue();
        _ingestor.Ingest("run-1", call).ShouldBeFalse();
    }

    [Fact]
    public void A_capture_that_fails_to_parse_keeps_its_error_and_changes_nothing()
    {
        Ingest(GetThread, new { threadId = "x" }, "not json");

        using var c = _db.Connect();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT error FROM raw_capture";
        ((string)cmd.ExecuteScalar()!).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_complete_calendar_answer_replaces_its_window()
    {
        const string tool = "mcp__claude_ai_Google_Calendar__list_events";
        var window = new { startTime = "2026-11-23T00:00:00-08:00", endTime = "2026-11-30T00:00:00-08:00" };
        Ingest(tool, window, Fixture.Read("calendar-events.json"));
        _store.Events(Now, Now.AddDays(10)).Count.ShouldBe(5);

        var fewer = JsonNode.Parse(Fixture.Read("calendar-events.json"))!;
        fewer["events"]!.AsArray().RemoveAt(0);
        Ingest(tool, window, fewer.ToJsonString());

        _store.Events(Now, Now.AddDays(10)).Select(e => e.EventId).ShouldNotContain("ev-1");
    }

    [Fact]
    public void A_secondary_calendar_is_stored_under_its_own_key()
    {
        Ingest("mcp__claude_ai_Google_Calendar__list_events", new { calendarId = "dept@example.org" }, Fixture.Read("calendar-events.json"));

        _store.Events(Now, Now.AddDays(10)).ShouldAllBe(e => e.CalendarKey == "dept");
    }

    [Fact]
    public void Slack_conversations_waiting_on_him_are_read_then_shown_when_owed()
    {
        // What CapturedCall.Parse makes of Slack's content blocks: the envelope's text.
        var envelope = new JsonObject { ["results"] = Fixture.Read("slack-search.txt"), ["pagination_info"] = "" }.ToJsonString();
        Ingest("mcp__claude_ai_Slack__slack_search_public_and_private", new { filters = "after:2026-11-01" }, envelope);
        var since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var rows = _store.SlackRows(Fixture.Config(), since);
        rows.Select(r => r.Key).ShouldBe(["C0000000006~main", "D0000000001~main", "C0000000003~1795880000.000050"], ignoreOrder: true);
        rows.ShouldAllBe(r => r.State == "Reading");

        var items = _store.SlackToTriage(Fixture.Config(), since, 10);
        var dm = items.Single(i => i.Key == "D0000000001~main");
        dm.Transcript.ShouldContain("Mike: Thanks, got it.");
        _store.RecordVerdict("slack", dm.Key, dm.ForMessage, true, "Asks for the QC report");
        _store.RecordVerdict("slack", "C0000000006~main", "1795708800.000500", false, "A bot notice");

        var after = _store.SlackRows(Fixture.Config(), since);
        after.Single(r => r.Key == "D0000000001~main").State.ShouldBe("Owed");
        after.Single(r => r.Key == "D0000000001~main").Where.ShouldBe("Pat Example");
        after.Select(r => r.Key).ShouldNotContain("C0000000006~main");
    }

    [Fact]
    public void Runs_are_recorded_for_the_status_bar()
    {
        _store.StartRun("r1", "mail", Now);
        _store.FinishRun("r1", "s1", "ok", null, 0.01);

        _store.LastSuccess("mail").ShouldNotBeNull();
        _store.LastSuccess("slack").ShouldBeNull();
    }
}
