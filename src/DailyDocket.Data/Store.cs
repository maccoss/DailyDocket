using System.Text.Json;
using Dapper;
using DailyDocket.Core.Calendar;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Mail;
using DailyDocket.Core.Slack;
using DailyDocket.Core.Todos;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Data;

/// <summary>A thread on the Email tab.</summary>
/// <param name="State">Owed (Claude says he owes a reply), Draft (an unsent draft), or Reading (not judged yet).</param>
public sealed record MailRow(
    string ThreadId, string State, string WaitingOn, string Subject, string Snippet, DateTimeOffset? WaitingSince, string? Why, bool Unread, bool RepliedEarlier);

/// <summary>A conversation on the Slack tab.</summary>
public sealed record SlackRow(string Key, string State, string From, string Where, bool IsDm, string Text, DateTimeOffset When, string? Why, string? Permalink, string LastTs = "");

/// <summary>The nightly deep sweep's reach: threads it saw since <paramref name="SeenSince"/> whose wait began before <paramref name="OlderThan"/>.</summary>
public readonly record struct DeepWindow(DateTimeOffset SeenSince, DateTimeOffset OlderThan);

/// <summary>How many items the tabs hide, and why, so the rules stay checkable.</summary>
public sealed record MailCounts(int Owed, int Reading, int Quiet, int Handled = 0);

/// <summary>Reads and writes the cache for the jobs and the tabs.</summary>
public sealed class Store
{
    private readonly DocketDatabase _db;

    public Store(DocketDatabase db) => _db = db;

    public DocketDatabase Database => _db;

    // ---- calendar ----------------------------------------------------------------

    public IReadOnlyList<CalendarEvent> Events(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = _db.Connect();
        return
        [
            .. c.Query("SELECT * FROM calendar_event WHERE end_utc > @from AND start_utc < @to ORDER BY start_utc",
                    new { from = DocketDatabase.Iso(from), to = DocketDatabase.Iso(to) })
                .Select(r => new CalendarEvent(
                    (string)r.event_id,
                    (string)r.calendar_key,
                    DocketDatabase.FromIso((string)r.start_utc)!.Value,
                    DocketDatabase.FromIso((string)r.end_utc)!.Value,
                    (long)r.all_day != 0,
                    (string)r.title,
                    (string)r.location,
                    (int)(long)r.guests,
                    (string?)r.my_response,
                    (string?)r.html_link,
                    (string)r.status,
                    (string?)r.event_type,
                    DocketDatabase.FromIso((string?)r.updated_utc),
                    (string?)r.content_hash,
                    (string?)r.join_link)),
        ];
    }

    public IReadOnlyList<ScheduleDay> Schedule(DocketConfig config, DateTimeOffset now, TimeZoneInfo zone)
    {
        var days = config.Schedule.Days;
        return CalendarRules.Build(Events(now.AddDays(-1), now.AddDays(days + 2)), config, now, days, zone);
    }

    // ---- mail ---------------------------------------------------------------------

    /// <summary>Threads a pull must read: new, or with a newer message than the last read saw.</summary>
    public IReadOnlyList<string> ThreadsToRead(DateTimeOffset seenSince, int max)
    {
        using var c = _db.Connect();
        return
        [
            .. c.Query<string>(
                """
                SELECT thread_id FROM email_thread
                WHERE seen_utc >= @since AND (read_message_id IS NULL OR read_message_id <> newest_message_id)
                ORDER BY seen_utc DESC LIMIT @max
                """,
                new { since = DocketDatabase.Iso(seenSince), max }),
        ];
    }

    /// <summary>The Email tab: unsent drafts first, then what he owes, oldest wait first.</summary>
    /// <param name="deep">
    /// The nightly deep sweep's window: threads it saw (since its start) whose wait began before the
    /// live window (older than) are shown too, so old unanswered threads surface. Null: none.
    /// </param>
    public (IReadOnlyList<MailRow> Rows, MailCounts Counts) MailRows(DateTimeOffset seenSince, HandledSet? handled = null, DeepWindow? deep = null)
    {
        using var c = _db.Connect();
        var rows = c.Query(
            $"""
            SELECT t.*, v.needs_reply, v.why, v.for_message
            FROM email_thread t LEFT JOIN verdict v ON v.source = 'mail' AND v.item_key = t.thread_id
            WHERE {SeenClause} AND t.category IS NOT NULL
            """,
            SeenArgs(seenSince, deep)).ToList();

        var result = new List<MailRow>();
        int owed = 0, reading = 0, quiet = 0, handledCount = 0;
        foreach (var r in rows)
        {
            // Marked handled and nobody has written since: off the tab until someone does.
            if (handled?.CoversEmail((string)r.thread_id, DocketDatabase.FromIso((string?)r.waiting_since_utc)) == true)
            {
                handledCount++;
                continue;
            }

            var category = (string)r.category;
            string? state = null;
            string? why = null;
            if (category == nameof(MailCategory.Draft))
            {
                state = "Draft";
            }
            else if (category == nameof(MailCategory.Open))
            {
                var current = r.for_message is string f && f == (string?)r.read_message_id;
                if (!current)
                {
                    state = "Reading";
                    reading++;
                }
                else if ((long)r.needs_reply != 0)
                {
                    state = "Owed";
                    why = (string?)r.why;
                    owed++;
                }
                else
                {
                    quiet++;
                }
            }
            else
            {
                quiet++;
            }

            if (state is not null)
            {
                result.Add(new MailRow(
                    (string)r.thread_id,
                    state,
                    (string?)r.waiting_on ?? "",
                    (string?)r.subject ?? "(no subject)",
                    (string?)r.snippet ?? "",
                    DocketDatabase.FromIso((string?)r.waiting_since_utc),
                    why,
                    r.unread is long u && u != 0,
                    r.replied_earlier is long re && re != 0));
            }
        }

        var ordered = result
            .OrderBy(r => r.State == "Draft" ? 0 : r.State == "Owed" ? 1 : 2)
            .ThenBy(r => r.WaitingSince ?? DateTimeOffset.MaxValue)
            .ToList();
        return (ordered, new MailCounts(owed, reading, quiet, handledCount));
    }

    /// <summary>Open threads whose newest message Claude has not judged, newest first.</summary>
    public IReadOnlyList<TriageItem> MailToTriage(DocketConfig config, DateTimeOffset seenSince, int max, HandledSet? handled = null, DeepWindow? deep = null)
    {
        using var c = _db.Connect();
        var ids = c.Query<(string ThreadId, string ReadId, string? Since)>(
            $"""
            SELECT t.thread_id, t.read_message_id, t.waiting_since_utc FROM email_thread t
            LEFT JOIN verdict v ON v.source = 'mail' AND v.item_key = t.thread_id
            WHERE t.category = 'Open' AND {SeenClause} AND t.read_message_id IS NOT NULL
              AND (v.for_message IS NULL OR v.for_message <> t.read_message_id)
            ORDER BY t.waiting_since_utc DESC
            """,
            SeenArgs(seenSince, deep))
            .Where(i => handled?.CoversEmail(i.ThreadId, DocketDatabase.FromIso(i.Since)) != true) // never pay to judge what he handled
            .Take(max)
            .ToList();

        return [.. ids.Select(i => new TriageItem("mail", i.ThreadId, i.ReadId, MailRules.TriageTranscript(LoadMessages(c, null, i.ThreadId), config)))];
    }

    /// <summary>Seen by the live sweeps, or by the deep sweep and older than the live window.</summary>
    private const string SeenClause =
        "(t.seen_utc >= @since OR (@deepSince IS NOT NULL AND t.seen_utc >= @deepSince AND t.waiting_since_utc < @deepOlderThan))";

    private static object SeenArgs(DateTimeOffset since, DeepWindow? deep) => new
    {
        since = DocketDatabase.Iso(since),
        deepSince = deep is { } d ? DocketDatabase.Iso(d.SeenSince) : null,
        deepOlderThan = deep is { } o ? DocketDatabase.Iso(o.OlderThan) : null,
    };

    /// <summary>Mike's own recent emails, for a draft's voice: the new text of each, without quoted history.</summary>
    public IReadOnlyList<string> RecentFromMe(DocketConfig config, int max)
    {
        using var c = _db.Connect();
        return
        [
            .. c.Query<(string Sender, string? Body)>("SELECT sender, body FROM email_message WHERE body IS NOT NULL ORDER BY date_utc DESC LIMIT 300")
                .Where(m => config.Owner.IsMe(m.Sender))
                .Select(m => MailText.SplitQuoted(m.Body).Message)
                .Where(t => t.Length is > 40 and < 1500)
                .Take(max),
        ];
    }

    /// <summary>A thread's messages, oldest first, for reading in the app.</summary>
    public IReadOnlyList<MailMessage> ThreadMessages(string threadId)
    {
        using var c = _db.Connect();
        return LoadMessages(c, null, threadId);
    }

    internal static IReadOnlyList<MailMessage> LoadMessages(SqliteConnection c, SqliteTransaction? tx, string threadId) =>
    [
        .. c.Query("SELECT * FROM email_message WHERE thread_id = @threadId ORDER BY date_utc", new { threadId }, tx)
            .Select(r => new MailMessage(
                (string)r.message_id,
                (string)r.thread_id,
                (string)r.sender,
                JsonSerializer.Deserialize<string[]>((string)r.to_list) ?? [],
                JsonSerializer.Deserialize<string[]>((string)r.cc_list) ?? [],
                DocketDatabase.FromIso((string?)r.date_utc),
                JsonSerializer.Deserialize<string[]>((string)r.labels) ?? [],
                (string?)r.subject,
                (string?)r.snippet,
                (string?)r.body)),
    ];

    // ---- slack --------------------------------------------------------------------

    public IReadOnlyList<SlackConversation> SlackConversations(DateTimeOffset since)
    {
        using var c = _db.Connect();
        var messages = c.Query(
                "SELECT * FROM slack_message WHERE conversation_key IN (SELECT conversation_key FROM slack_message WHERE ts_value >= @since)",
                new { since = (double)since.ToUnixTimeSeconds() })
            .Select(r => new SlackMessage(
                (string)r.channel_id,
                (string)r.channel_label,
                (string)r.participants,
                (string)r.from_id,
                (string)r.from_name,
                (string)r.ts,
                (string?)r.thread_ts,
                (string?)r.permalink,
                (string)r.text));
        return SlackRules.Group(messages);
    }

    /// <summary>One conversation's messages, oldest first, for reading in the app.</summary>
    public SlackConversation? Conversation(string key)
    {
        using var c = _db.Connect();
        var messages = c.Query("SELECT * FROM slack_message WHERE conversation_key = @key", new { key })
            .Select(r => new SlackMessage(
                (string)r.channel_id,
                (string)r.channel_label,
                (string)r.participants,
                (string)r.from_id,
                (string)r.from_name,
                (string)r.ts,
                (string?)r.thread_ts,
                (string?)r.permalink,
                (string)r.text));
        return SlackRules.Group(messages).FirstOrDefault();
    }

    /// <summary>The Slack tab: conversations waiting on him that Claude says need a reply, oldest first.</summary>
    public IReadOnlyList<SlackRow> SlackRows(DocketConfig config, DateTimeOffset since, HandledSet? handled = null)
    {
        var me = config.Owner.SlackUserId;
        var verdicts = Verdicts("slack");
        var rows = new List<SlackRow>();
        foreach (var conv in SlackConversations(since).Where(c => SlackRules.IsWaiting(c, me) && c.Last.When >= since && handled?.CoversSlack(c.Key, c.Last.Ts) != true))
        {
            var last = conv.Last;
            string state;
            string? why = null;
            if (verdicts.TryGetValue(conv.Key, out var v) && v.ForMessage == last.Ts)
            {
                if (!v.NeedsReply)
                {
                    continue;
                }

                state = "Owed";
                why = v.Why;
            }
            else
            {
                state = "Reading";
            }

            var where = last.IsDm ? SlackRules.Others(last, config.Owner.Name) : last.ChannelLabel;
            var link = conv.Messages.LastOrDefault(m => m.Permalink is not null)?.Permalink;
            rows.Add(new SlackRow(conv.Key, state, last.FromName, where, last.IsDm, last.Text, last.When, why, link, last.Ts));
        }

        return [.. rows.OrderBy(r => r.State == "Owed" ? 0 : 1).ThenBy(r => r.When)];
    }

    public IReadOnlyList<TriageItem> SlackToTriage(DocketConfig config, DateTimeOffset since, int max, HandledSet? handled = null)
    {
        var me = config.Owner.SlackUserId;
        var verdicts = Verdicts("slack");
        return
        [
            .. SlackConversations(since)
                .Where(c => SlackRules.IsWaiting(c, me) && c.Last.When >= since && handled?.CoversSlack(c.Key, c.Last.Ts) != true)
                .Where(c => !(verdicts.TryGetValue(c.Key, out var v) && v.ForMessage == c.Last.Ts))
                .OrderByDescending(c => c.Last.TsValue)
                .Take(max)
                .Select(c => new TriageItem(
                    "slack",
                    c.Key,
                    c.Last.Ts,
                    SlackRules.TriageTranscript(c, me, c.Last.IsDm ? SlackRules.Others(c.Last, config.Owner.Name) : c.Last.ChannelLabel))),
        ];
    }

    // ---- verdicts, watermarks, runs ---------------------------------------------

    public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why)
    {
        using var c = _db.Connect();
        c.Execute(
            """
            INSERT OR REPLACE INTO verdict (source, item_key, for_message, needs_reply, why, judged_utc)
            VALUES (@source, @key, @forMessage, @needsReply, @why, @now)
            """,
            new { source, key, forMessage, needsReply, why = why.Length > 120 ? why[..120] : why, now = DocketDatabase.Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>The cache's verdicts for a source, as the data repository shares them, with each message's time where known.</summary>
    public IReadOnlyList<Core.Verdicts.SharedVerdict> CachedVerdicts(string source)
    {
        using var c = _db.Connect();
        return
        [
            .. c.Query<(string Key, string ForMessage, long NeedsReply, string Why, string Judged, string? MessageAt)>(
                    """
                    SELECT v.item_key, v.for_message, v.needs_reply, v.why, v.judged_utc, m.date_utc
                    FROM verdict v LEFT JOIN email_message m ON v.source = 'mail' AND m.message_id = v.for_message
                    WHERE v.source = @source
                    """, new { source })
                .Select(v => new Core.Verdicts.SharedVerdict(source, v.Key, v.ForMessage, DocketDatabase.FromIso(v.MessageAt), v.NeedsReply != 0, v.Why,
                    DocketDatabase.FromIso(v.Judged) ?? DateTimeOffset.UnixEpoch, "")),
        ];
    }

    /// <summary>When a mail message was sent, if the cache has it (a Slack ts carries its own time).</summary>
    public DateTimeOffset? MailMessageTime(string messageId)
    {
        using var c = _db.Connect();
        return DocketDatabase.FromIso(c.ExecuteScalar<string?>("SELECT date_utc FROM email_message WHERE message_id = @messageId", new { messageId }));
    }

    /// <summary>Takes in a verdict another machine judged, keeping its judged time.</summary>
    public void ImportVerdict(Core.Verdicts.SharedVerdict v)
    {
        using var c = _db.Connect();
        c.Execute(
            """
            INSERT OR REPLACE INTO verdict (source, item_key, for_message, needs_reply, why, judged_utc)
            VALUES (@source, @key, @forMessage, @needsReply, @why, @at)
            """,
            new { source = v.Source, key = v.Key, forMessage = v.ForMessage, needsReply = v.NeedsReply, why = v.Why, at = DocketDatabase.Iso(v.At) });
    }

    private Dictionary<string, (string ForMessage, bool NeedsReply, string Why)> Verdicts(string source)
    {
        using var c = _db.Connect();
        return c.Query<(string Key, string ForMessage, long NeedsReply, string Why)>(
                "SELECT item_key, for_message, needs_reply, why FROM verdict WHERE source = @source", new { source })
            .ToDictionary(v => v.Key, v => (v.ForMessage, v.NeedsReply != 0, v.Why));
    }

    public string? Watermark(string source, string scope)
    {
        using var c = _db.Connect();
        return c.ExecuteScalar<string?>("SELECT value FROM watermark WHERE source = @source AND scope = @scope", new { source, scope });
    }

    public void SetWatermark(string source, string scope, string value)
    {
        using var c = _db.Connect();
        c.Execute("INSERT OR REPLACE INTO watermark (source, scope, value) VALUES (@source, @scope, @value)", new { source, scope, value });
    }

    public void StartRun(string id, string job, DateTimeOffset at)
    {
        using var c = _db.Connect();
        c.Execute("INSERT INTO job_run (id, job, started_utc) VALUES (@id, @job, @at)", new { id, job, at = DocketDatabase.Iso(at) });
    }

    public void FinishRun(string id, string? sessionId, string outcome, string? detail, double? costUsd)
    {
        using var c = _db.Connect();
        c.Execute(
            "UPDATE job_run SET session_id = @sessionId, ended_utc = @now, outcome = @outcome, detail = @detail, cost_usd = @costUsd WHERE id = @id",
            new { id, sessionId, now = DocketDatabase.Iso(DateTimeOffset.UtcNow), outcome, detail, costUsd });
    }

    /// <summary>When <paramref name="job"/> last finished well, for the status bar.</summary>
    public DateTimeOffset? LastSuccess(string job)
    {
        using var c = _db.Connect();
        return DocketDatabase.FromIso(c.ExecuteScalar<string?>(
            "SELECT MAX(ended_utc) FROM job_run WHERE job = @job AND outcome = 'ok'", new { job }));
    }

    /// <summary>Raw captures are kept 14 days: long enough to replay a parser fix, no longer.</summary>
    public int PurgeCaptures(DateTimeOffset now)
    {
        using var c = _db.Connect();
        return c.Execute("DELETE FROM raw_capture WHERE captured_utc < @cutoff", new { cutoff = DocketDatabase.Iso(now.AddDays(-14)) });
    }
}
