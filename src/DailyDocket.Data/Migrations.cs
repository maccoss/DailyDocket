using Dapper;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Data;

/// <summary>The schema, versioned with PRAGMA user_version.</summary>
/// <remarks>
/// Every cache table can be dropped and refilled by a pull, so until the outbox exists (phase 4)
/// a version this code does not know simply rebuilds the cache. From then on the outbox must be
/// carried across, and each version gets a real step.
/// </remarks>
internal static class Migrations
{
    public const int Current = 5;

    /// <summary>
    /// Version 5: when a thread was last read, so threads he may owe are read again on a timer
    /// (Store.ThreadsToRead). Existing rows get null, which reads each of them once more.
    /// </summary>
    private const string ReadTimeStep = "ALTER TABLE email_thread ADD COLUMN read_utc TEXT;";

    /// <summary>
    /// Version 4: the meeting's join link (Zoom, Teams, Meet, Webex). Existing rows get null until
    /// the next pull rewrites them.
    /// </summary>
    private const string JoinLinkStep = "ALTER TABLE calendar_event ADD COLUMN join_link TEXT;";

    /// <summary>
    /// Version 3: the hash of what a meeting brief depends on. Existing rows get null (a brief is
    /// then judged by its start alone) until the next pull rewrites them.
    /// </summary>
    private const string EventHashStep = "ALTER TABLE calendar_event ADD COLUMN content_hash TEXT;";

    /// <summary>
    /// Version 2: the outbox, the one table that is not a cache (Mike's unsent writing). From here
    /// on a version step must carry it across, never drop it.
    /// </summary>
    private const string OutboxSchema = """
        CREATE TABLE IF NOT EXISTS outbox_item (
            id          TEXT PRIMARY KEY,
            kind        TEXT NOT NULL,
            status      TEXT NOT NULL,
            target      TEXT NOT NULL,
            reply_to    TEXT,
            channel     TEXT,
            to_list     TEXT NOT NULL,
            cc_list     TEXT NOT NULL,
            subject     TEXT NOT NULL,
            base        TEXT NOT NULL,
            document    TEXT NOT NULL,
            html        TEXT NOT NULL,
            text        TEXT NOT NULL,
            created     TEXT NOT NULL,
            updated     TEXT NOT NULL,
            send_after  TEXT,
            claimed_at  TEXT,
            machine     TEXT NOT NULL,
            attempts    INTEGER NOT NULL,
            remote_id   TEXT,
            problem     TEXT
        );
        CREATE INDEX IF NOT EXISTS outbox_status ON outbox_item (status);
        CREATE INDEX IF NOT EXISTS outbox_target ON outbox_item (target);
        """;

    private const string Schema = """
        PRAGMA journal_mode = WAL;

        CREATE TABLE calendar_event (
            calendar_key TEXT NOT NULL,
            event_id     TEXT NOT NULL,
            start_utc    TEXT NOT NULL,
            end_utc      TEXT NOT NULL,
            all_day      INTEGER NOT NULL,
            title        TEXT NOT NULL,
            location     TEXT NOT NULL,
            guests       INTEGER NOT NULL,
            my_response  TEXT,
            html_link    TEXT,
            status       TEXT NOT NULL,
            event_type   TEXT,
            updated_utc  TEXT,
            content_hash TEXT,
            join_link    TEXT,
            PRIMARY KEY (calendar_key, event_id)
        );
        CREATE INDEX calendar_event_start ON calendar_event (start_utc);

        -- One row per thread seen in an inbox search. newest_message_id is the message the search
        -- named (not always the newest); read_message_id is the newest message the last get_thread
        -- saw, at read_utc.
        CREATE TABLE email_thread (
            thread_id         TEXT PRIMARY KEY,
            newest_message_id TEXT NOT NULL,
            read_message_id   TEXT,
            message_count     INTEGER NOT NULL,
            subject           TEXT,
            sender            TEXT,
            seen_utc          TEXT NOT NULL,
            category          TEXT,
            trailing          INTEGER,
            replied_earlier   INTEGER,
            waiting_since_utc TEXT,
            waiting_on        TEXT,
            snippet           TEXT,
            unread            INTEGER,
            read_utc          TEXT
        );

        CREATE TABLE email_message (
            message_id TEXT PRIMARY KEY,
            thread_id  TEXT NOT NULL,
            sender     TEXT NOT NULL,
            to_list    TEXT NOT NULL,
            cc_list    TEXT NOT NULL,
            date_utc   TEXT,
            labels     TEXT NOT NULL,
            subject    TEXT,
            snippet    TEXT,
            body       TEXT
        );
        CREATE INDEX email_message_thread ON email_message (thread_id);

        CREATE TABLE mail_draft (
            draft_id  TEXT PRIMARY KEY,
            thread_id TEXT NOT NULL,
            date_utc  TEXT
        );

        CREATE TABLE slack_message (
            channel_id       TEXT NOT NULL,
            ts               TEXT NOT NULL,
            ts_value         REAL NOT NULL,
            thread_ts        TEXT,
            conversation_key TEXT NOT NULL,
            channel_label    TEXT NOT NULL,
            participants     TEXT NOT NULL,
            from_id          TEXT NOT NULL,
            from_name        TEXT NOT NULL,
            permalink        TEXT,
            text             TEXT NOT NULL,
            PRIMARY KEY (channel_id, ts)
        );
        CREATE INDEX slack_message_conversation ON slack_message (conversation_key);

        -- Claude's answer for one item, kept against the newest message it read: a thread is
        -- judged once and never again until someone writes in it.
        CREATE TABLE verdict (
            source      TEXT NOT NULL,
            item_key    TEXT NOT NULL,
            for_message TEXT NOT NULL,
            needs_reply INTEGER NOT NULL,
            why         TEXT NOT NULL,
            judged_utc  TEXT NOT NULL,
            PRIMARY KEY (source, item_key)
        );

        CREATE TABLE raw_capture (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id       TEXT NOT NULL,
            tool_use_id  TEXT NOT NULL UNIQUE,
            tool_name    TEXT NOT NULL,
            tool_input   TEXT NOT NULL,
            response     TEXT NOT NULL,
            captured_utc TEXT NOT NULL,
            parsed_utc   TEXT,
            error        TEXT
        );

        CREATE TABLE watermark (
            source TEXT NOT NULL,
            scope  TEXT NOT NULL,
            value  TEXT NOT NULL,
            PRIMARY KEY (source, scope)
        );

        CREATE TABLE job_run (
            id          TEXT PRIMARY KEY,
            job         TEXT NOT NULL,
            session_id  TEXT,
            started_utc TEXT NOT NULL,
            ended_utc   TEXT,
            outcome     TEXT,
            detail      TEXT,
            cost_usd    REAL
        );
        CREATE INDEX job_run_job ON job_run (job, started_utc);
        """;

    public static void Apply(SqliteConnection c)
    {
        var version = c.ExecuteScalar<long>("PRAGMA user_version;");
        if (version == Current)
        {
            return;
        }

        if (version > Current)
        {
            // A newer DailyDocket wrote this file. Dropping it would lose that version's outbox.
            throw new InvalidOperationException(
                "This computer's DailyDocket database was written by a newer version of DailyDocket. Update DailyDocket, then start it again.");
        }

        using var tx = c.BeginTransaction();
        if (version is 1 or 2 or 3 or 4)
        {
            // The cache stays; each step adds what its version brought.
            if (version == 1)
            {
                c.Execute(OutboxSchema, transaction: tx);
            }

            var columns = c.Query<string>("SELECT name FROM pragma_table_info('calendar_event');", transaction: tx).ToList();
            if (!columns.Contains("content_hash"))
            {
                c.Execute(EventHashStep, transaction: tx);
            }

            if (!columns.Contains("join_link"))
            {
                c.Execute(JoinLinkStep, transaction: tx);
            }

            if (!c.Query<string>("SELECT name FROM pragma_table_info('email_thread');", transaction: tx).Contains("read_utc"))
            {
                c.Execute(ReadTimeStep, transaction: tx);
            }

            c.Execute($"PRAGMA user_version = {Current};", transaction: tx);
            tx.Commit();
            return;
        }

        c.Execute(Schema.Replace("PRAGMA journal_mode = WAL;", "", StringComparison.Ordinal), transaction: tx);
        c.Execute(OutboxSchema, transaction: tx);
        c.Execute($"PRAGMA user_version = {Current};", transaction: tx);
        tx.Commit();
        c.Execute("PRAGMA journal_mode = WAL;");
    }
}
