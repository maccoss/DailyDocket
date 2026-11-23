using System.Text.Json;
using Dapper;
using DailyDocket.Core.Outbox;

namespace DailyDocket.Data;

/// <summary>The outbox, in the database and mirrored to outbox.json.</summary>
/// <remarks>
/// The database is otherwise a cache that can be rebuilt; the outbox and unsent drafts cannot, so
/// every change is also exported to a plain JSON file beside it (design, Local data model).
/// </remarks>
public sealed class OutboxStore : IOutboxStore
{
    private static readonly JsonSerializerOptions Export = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly DocketDatabase _db;
    private readonly string? _exportPath;
    private readonly object _exportLock = new();

    public OutboxStore(DocketDatabase db, string? exportPath)
    {
        _db = db;
        _exportPath = exportPath;
    }

    public void Save(OutboxItem item)
    {
        using (var c = _db.Connect())
        {
            c.Execute(
                """
                INSERT OR REPLACE INTO outbox_item
                    (id, kind, status, target, reply_to, channel, to_list, cc_list, subject, base, document, html, text,
                     created, updated, send_after, claimed_at, machine, attempts, remote_id, problem)
                VALUES (@id, @kind, @status, @target, @reply_to, @channel, @to_list, @cc_list, @subject, @base, @document, @html, @text,
                        @created, @updated, @send_after, @claimed_at, @machine, @attempts, @remote_id, @problem)
                """,
                new
                {
                    id = item.Id,
                    kind = item.Kind.ToString(),
                    status = item.Status.ToString(),
                    target = item.Target,
                    reply_to = item.ReplyTo,
                    channel = item.Channel,
                    to_list = JsonSerializer.Serialize(item.To),
                    cc_list = JsonSerializer.Serialize(item.Cc),
                    subject = item.Subject,
                    @base = item.Base,
                    document = item.Document,
                    html = item.Html,
                    text = item.Text,
                    created = DocketDatabase.Iso(item.Created),
                    updated = DocketDatabase.Iso(item.Updated),
                    send_after = DocketDatabase.Iso(item.SendAfter),
                    claimed_at = DocketDatabase.Iso(item.ClaimedAt),
                    machine = item.Machine,
                    attempts = item.Attempts,
                    remote_id = item.RemoteId,
                    problem = item.Problem,
                });
        }

        WriteExport();
    }

    public OutboxItem? Get(string id)
    {
        using var c = _db.Connect();
        return c.Query("SELECT * FROM outbox_item WHERE id = @id", new { id }).Select(Read).FirstOrDefault();
    }

    public IReadOnlyList<OutboxItem> WithStatus(params OutboxStatus[] statuses)
    {
        using var c = _db.Connect();
        var names = statuses.Select(s => s.ToString()).ToArray();
        return [.. c.Query("SELECT * FROM outbox_item WHERE status IN @names ORDER BY created", new { names }).Select(Read)];
    }

    /// <summary>Everything not yet sent for one conversation: its draft, and anything queued, held or failed.</summary>
    public IReadOnlyList<OutboxItem> ForTarget(string target)
    {
        using var c = _db.Connect();
        return [.. c.Query("SELECT * FROM outbox_item WHERE target = @target AND status <> 'Sent' ORDER BY created", new { target }).Select(Read)];
    }

    public void Delete(string id)
    {
        using (var c = _db.Connect())
        {
            c.Execute("DELETE FROM outbox_item WHERE id = @id", new { id });
        }

        WriteExport();
    }

    private static OutboxItem Read(dynamic r) => new(
        (string)r.id,
        Enum.Parse<OutboxKind>((string)r.kind),
        Enum.Parse<OutboxStatus>((string)r.status),
        (string)r.target,
        (string?)r.reply_to,
        (string?)r.channel,
        JsonSerializer.Deserialize<string[]>((string)r.to_list) ?? [],
        JsonSerializer.Deserialize<string[]>((string)r.cc_list) ?? [],
        (string)r.subject,
        (string)r.@base,
        (string)r.document,
        (string)r.html,
        (string)r.text,
        DocketDatabase.FromIso((string)r.created)!.Value,
        DocketDatabase.FromIso((string)r.updated)!.Value,
        DocketDatabase.FromIso((string?)r.send_after),
        DocketDatabase.FromIso((string?)r.claimed_at),
        (string)r.machine,
        (int)(long)r.attempts,
        (string?)r.remote_id,
        (string?)r.problem);

    /// <summary>Unsent items only: sent ones are in Gmail and Slack, the sources of truth.</summary>
    private void WriteExport()
    {
        if (_exportPath is null)
        {
            return;
        }

        lock (_exportLock)
        {
            var unsent = WithStatus(OutboxStatus.Draft, OutboxStatus.Queued, OutboxStatus.Sending, OutboxStatus.Held, OutboxStatus.Failed);
            var temp = _exportPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(unsent, Export));
            File.Move(temp, _exportPath, overwrite: true);
        }
    }
}
