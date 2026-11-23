using System.Text.Json;
using Dapper;
using DailyDocket.Core.Calendar;
using DailyDocket.Core.Capture;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Mail;
using DailyDocket.Core.Slack;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Data;

/// <summary>Turns a run's captures into cache rows.</summary>
/// <remarks>
/// Each capture is stored raw first (kept 14 days, so a parser fix can be replayed over real data
/// without pulling again) and parsed in the same transaction. A capture that fails to parse keeps
/// its error and leaves the cache as it was, so one odd result never blanks a column.
/// </remarks>
public sealed class CaptureIngestor
{
    /// <summary>
    /// Messages are read in the app, offline, so the whole text is kept; this ceiling only stops a
    /// pathological message (a pasted log) from bloating the cache.
    /// </summary>
    public const int BodyLimit = 200_000;

    private readonly DocketDatabase _db;
    private readonly Func<DocketConfig> _config;
    private readonly TimeZoneInfo _zone;

    public CaptureIngestor(DocketDatabase db, Func<DocketConfig> config, TimeZoneInfo zone)
    {
        _db = db;
        _config = config;
        _zone = zone;
    }

    /// <summary>Ingests every capture in <paramref name="folder"/> not seen before. Returns how many were new.</summary>
    public int IngestFolder(string runId, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            CapturedCall call;
            try
            {
                call = CapturedCall.Parse(File.ReadAllText(file), new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                continue; // A file still being written, or not a capture at all; skipped.
            }

            if (Ingest(runId, call))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Stores and applies one capture. False when it was already ingested.</summary>
    public bool Ingest(string runId, CapturedCall call)
    {
        using var c = _db.Connect();
        using var tx = c.BeginTransaction();
        var inserted = c.Execute(
            """
            INSERT OR IGNORE INTO raw_capture (run_id, tool_use_id, tool_name, tool_input, response, captured_utc)
            VALUES (@runId, @id, @tool, @input, @response, @at)
            """,
            new
            {
                runId,
                id = call.ToolUseId,
                tool = call.ToolName,
                input = call.Input.ValueKind == JsonValueKind.Undefined ? "{}" : call.Input.GetRawText(),
                response = call.ResponseText,
                at = DocketDatabase.Iso(call.CapturedAt),
            },
            tx);
        if (inserted == 0)
        {
            return false;
        }

        string? error = null;
        try
        {
            Apply(c, tx, call);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            error = e.Message;
        }

        c.Execute(
            "UPDATE raw_capture SET parsed_utc = @now, error = @error WHERE tool_use_id = @id",
            new { now = DocketDatabase.Iso(DateTimeOffset.UtcNow), error, id = call.ToolUseId },
            tx);
        tx.Commit();
        return true;
    }

    /// <summary>Re-parses every stored capture, oldest first: replays a parser fix over real data.</summary>
    public int Replay()
    {
        using var c = _db.Connect();
        var rows = c.Query<(string RunId, string ToolUseId, string ToolName, string Input, string Response, string At)>(
            "SELECT run_id, tool_use_id, tool_name, tool_input, response, captured_utc FROM raw_capture ORDER BY id").ToList();
        foreach (var r in rows)
        {
            using var tx = c.BeginTransaction();
            using var input = JsonDocument.Parse(r.Input);
            Apply(c, tx, new CapturedCall(r.ToolName, input.RootElement.Clone(), r.Response, r.ToolUseId, null, DocketDatabase.FromIso(r.At)!.Value));
            tx.Commit();
        }

        return rows.Count;
    }

    private void Apply(SqliteConnection c, SqliteTransaction tx, CapturedCall call)
    {
        var config = _config();
        switch ((call.Server, call.Tool))
        {
            case ("Gmail", "search_threads"):
                ApplySearch(c, tx, call, config);
                break;
            case ("Gmail", "get_thread"):
                ApplyThread(c, tx, call, config);
                break;
            case ("Gmail", "list_drafts"):
                ApplyDrafts(c, tx, call, config);
                break;
            case ("Google_Calendar", "list_events"):
                ApplyEvents(c, tx, call, config);
                break;
            case ("Slack", "slack_search_public_and_private"):
                ApplySlack(c, tx, call);
                break;
        }
    }

    private static void ApplySearch(SqliteConnection c, SqliteTransaction tx, CapturedCall call, DocketConfig config)
    {
        // Only inbox sweeps define what is on the Email tab. Other searches (sent mail, a drafting
        // run's lookups) say nothing about what is waiting.
        if (call.InputString("query") is not { } query || !query.Contains("in:inbox", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var (threads, _) = GmailParser.ParseSearch(call.ResponseText);
        foreach (var t in threads)
        {
            c.Execute(
                """
                INSERT INTO email_thread (thread_id, newest_message_id, message_count, subject, sender, seen_utc)
                VALUES (@ThreadId, @NewestMessageId, @MessageCount, @Subject, @Sender, @seen)
                ON CONFLICT (thread_id) DO UPDATE SET
                    newest_message_id = excluded.newest_message_id,
                    message_count = excluded.message_count,
                    subject = COALESCE(excluded.subject, email_thread.subject),
                    sender = excluded.sender,
                    seen_utc = excluded.seen_utc
                """,
                new { t.ThreadId, t.NewestMessageId, t.MessageCount, t.Subject, t.Sender, seen = DocketDatabase.Iso(call.CapturedAt) },
                tx);
        }
    }

    private static void ApplyThread(SqliteConnection c, SqliteTransaction tx, CapturedCall call, DocketConfig config)
    {
        var (threadId, messages) = GmailParser.ParseThread(call.ResponseText);
        threadId ??= call.InputString("threadId");
        if (threadId is null || messages.Count == 0)
        {
            return;
        }

        // A metadata-only read has no bodies; keep the ones a plain-text read stored earlier.
        var withBodies = messages.Any(m => m.Body is not null);
        if (withBodies)
        {
            c.Execute("DELETE FROM email_message WHERE thread_id = @threadId", new { threadId }, tx);
        }

        foreach (var m in messages)
        {
            c.Execute(
                """
                INSERT INTO email_message (message_id, thread_id, sender, to_list, cc_list, date_utc, labels, subject, snippet, body)
                VALUES (@Id, @threadId, @Sender, @to, @cc, @date, @labels, @Subject, @Snippet, @body)
                ON CONFLICT (message_id) DO UPDATE SET
                    sender = excluded.sender, to_list = excluded.to_list, cc_list = excluded.cc_list,
                    date_utc = excluded.date_utc, labels = excluded.labels,
                    subject = COALESCE(excluded.subject, email_message.subject),
                    snippet = COALESCE(excluded.snippet, email_message.snippet),
                    body = COALESCE(excluded.body, email_message.body)
                """,
                new
                {
                    m.Id,
                    threadId,
                    m.Sender,
                    to = JsonSerializer.Serialize(m.To),
                    cc = JsonSerializer.Serialize(m.Cc),
                    date = DocketDatabase.Iso(m.Date),
                    labels = JsonSerializer.Serialize(m.Labels),
                    m.Subject,
                    m.Snippet,
                    body = m.Body is null ? null : m.Body.Length <= BodyLimit ? m.Body : m.Body[..BodyLimit],
                },
                tx);
        }

        var newest = messages[^1];
        c.Execute(
            """
            INSERT INTO email_thread (thread_id, newest_message_id, read_message_id, message_count, subject, sender, seen_utc)
            VALUES (@threadId, @id, @id, @count, @subject, @sender, @seen)
            ON CONFLICT (thread_id) DO UPDATE SET read_message_id = @id, message_count = @count
            """,
            new { threadId, id = newest.Id, count = messages.Count, subject = newest.Subject, sender = newest.Sender, seen = DocketDatabase.Iso(call.CapturedAt) },
            tx);
        Reclassify(c, tx, threadId, config);
    }

    private static void ApplyDrafts(SqliteConnection c, SqliteTransaction tx, CapturedCall call, DocketConfig config)
    {
        var drafts = GmailParser.ParseDrafts(call.ResponseText);
        var touched = c.Query<string>("SELECT DISTINCT thread_id FROM mail_draft", transaction: tx).ToHashSet();

        // The first page is the whole answer in practice (50 drafts); replacing is right only then.
        if (call.InputString("pageToken") is null)
        {
            c.Execute("DELETE FROM mail_draft", transaction: tx);
        }

        foreach (var d in drafts)
        {
            c.Execute(
                "INSERT OR REPLACE INTO mail_draft (draft_id, thread_id, date_utc) VALUES (@Id, @ThreadId, @date)",
                new { d.Id, d.ThreadId, date = DocketDatabase.Iso(d.Date) },
                tx);
            touched.Add(d.ThreadId);
        }

        foreach (var threadId in touched)
        {
            Reclassify(c, tx, threadId, config);
        }
    }

    /// <summary>Recomputes a thread's rule result from its stored messages and the drafts list.</summary>
    internal static void Reclassify(SqliteConnection c, SqliteTransaction tx, string threadId, DocketConfig config)
    {
        var messages = Store.LoadMessages(c, tx, threadId);
        if (messages.Count == 0)
        {
            return;
        }

        var hasDraft = c.ExecuteScalar<long>("SELECT COUNT(*) FROM mail_draft WHERE thread_id = @threadId", new { threadId }, tx) > 0;
        var r = MailRules.Classify(messages, hasDraft, config);
        c.Execute(
            """
            UPDATE email_thread SET category = @category, trailing = @Trailing, replied_earlier = @RepliedEarlier,
                waiting_since_utc = @since, waiting_on = @WaitingOn, snippet = @Snippet, unread = @Unread,
                subject = COALESCE(@Subject, subject)
            WHERE thread_id = @threadId
            """,
            new { category = r.Category.ToString(), r.Trailing, r.RepliedEarlier, since = DocketDatabase.Iso(r.WaitingSince), r.WaitingOn, r.Snippet, r.Unread, r.Subject, threadId },
            tx);
    }

    private void ApplyEvents(SqliteConnection c, SqliteTransaction tx, CapturedCall call, DocketConfig config)
    {
        var calendar = config.CalendarFor(call.InputString("calendarId"));
        var (_, events, next) = CalendarParser.Parse(call.ResponseText, calendar.Key, _zone);

        // A complete answer for a window replaces that window: an event moved or deleted elsewhere
        // must leave the schedule. Only a single-page answer is known to be complete.
        if (next is null && call.InputString("pageToken") is null &&
            DateTimeOffset.TryParse(call.InputString("startTime"), out var from) &&
            DateTimeOffset.TryParse(call.InputString("endTime"), out var to))
        {
            c.Execute(
                "DELETE FROM calendar_event WHERE calendar_key = @key AND start_utc >= @from AND start_utc < @to",
                new { key = calendar.Key, from = DocketDatabase.Iso(from), to = DocketDatabase.Iso(to) },
                tx);
        }

        foreach (var e in events)
        {
            c.Execute(
                """
                INSERT OR REPLACE INTO calendar_event
                    (calendar_key, event_id, start_utc, end_utc, all_day, title, location, guests, my_response, html_link, status, event_type, updated_utc, content_hash, join_link)
                VALUES (@CalendarKey, @EventId, @start, @end, @AllDay, @Title, @Location, @Guests, @MyResponse, @HtmlLink, @Status, @EventType, @updated, @ContentHash, @JoinLink)
                """,
                new
                {
                    e.CalendarKey,
                    e.EventId,
                    start = DocketDatabase.Iso(e.Start),
                    end = DocketDatabase.Iso(e.End),
                    e.AllDay,
                    e.Title,
                    e.Location,
                    e.Guests,
                    e.MyResponse,
                    e.HtmlLink,
                    e.Status,
                    e.EventType,
                    updated = DocketDatabase.Iso(e.Updated),
                    e.ContentHash,
                    e.JoinLink,
                },
                tx);
        }
    }

    private static void ApplySlack(SqliteConnection c, SqliteTransaction tx, CapturedCall call)
    {
        var (messages, _) = SlackParser.ParseSearch(call.ResponseText);
        foreach (var m in messages)
        {
            c.Execute(
                """
                INSERT INTO slack_message (channel_id, ts, ts_value, thread_ts, conversation_key, channel_label, participants, from_id, from_name, permalink, text)
                VALUES (@channel_id, @ts, @ts_value, @thread_ts, @conversation_key, @channel_label, @participants, @from_id, @from_name, @permalink, @text)
                ON CONFLICT (channel_id, ts) DO UPDATE SET
                    thread_ts = COALESCE(excluded.thread_ts, slack_message.thread_ts),
                    conversation_key = CASE WHEN excluded.thread_ts IS NOT NULL THEN excluded.conversation_key ELSE slack_message.conversation_key END,
                    channel_label = CASE WHEN excluded.channel_label <> '' THEN excluded.channel_label ELSE slack_message.channel_label END,
                    participants = CASE WHEN excluded.participants <> '' THEN excluded.participants ELSE slack_message.participants END,
                    from_name = CASE WHEN excluded.from_name <> '' THEN excluded.from_name ELSE slack_message.from_name END,
                    permalink = COALESCE(excluded.permalink, slack_message.permalink),
                    text = CASE WHEN excluded.text <> '' THEN excluded.text ELSE slack_message.text END
                """,
                new
                {
                    channel_id = m.ChannelId,
                    ts = m.Ts,
                    ts_value = (double)m.TsValue,
                    thread_ts = m.ThreadTs,
                    conversation_key = SlackRules.ConversationKey(m.ChannelId, m.ThreadTs),
                    channel_label = m.ChannelLabel,
                    participants = m.Participants,
                    from_id = m.FromId,
                    from_name = m.FromName,
                    permalink = m.Permalink,
                    text = m.Text,
                },
                tx);
        }
    }
}
