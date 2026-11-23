using System.Text.Json;
using Dapper;
using DailyDocket.Core.Outbox;

namespace DailyDocket.Data;

/// <summary>The outbox, in the database and mirrored to outbox.json.</summary>
/// <remarks>
/// The database is otherwise a cache that can be rebuilt; the outbox and unsent drafts cannot, so
/// every change is also exported to a JSON file beside it, encrypted for Mike's Windows account (design, Local data model).
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
        Restored = RestoreFromExport();
    }

    /// <summary>How many unsent items came back from outbox.json when the database had none.</summary>
    public int Restored { get; }

    /// <summary>
    /// A new or replaced database has an empty outbox while outbox.json still lists Mike's unsent
    /// items (the database could not be read, or was deleted): bring them back. Anything that was
    /// mid-send comes back held, never queued, so it cannot go twice.
    /// </summary>
    private int RestoreFromExport()
    {
        if (_exportPath is null || !File.Exists(_exportPath))
        {
            return 0;
        }

        using (var c = _db.Connect())
        {
            if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox_item") > 0)
            {
                return 0;
            }
        }

        List<OutboxItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<OutboxItem>>(Unprotect(File.ReadAllBytes(_exportPath)), Export);
        }
        catch (JsonException)
        {
            return 0; // An unreadable export is left as it is for Mike to look at.
        }

        foreach (var item in items ?? [])
        {
            Save(item.Status is OutboxStatus.Sending or OutboxStatus.Queued
                ? item with { Status = OutboxStatus.Held, Problem = "Restored after DailyDocket's local database had to be rebuilt. Check the conversation, then send again or discard." }
                : item);
        }

        return items?.Count ?? 0;
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
            File.WriteAllBytes(temp, Protect(JsonSerializer.SerializeToUtf8Bytes(unsent, Export)));
            File.Move(temp, _exportPath, overwrite: true);
        }
    }

    private static readonly byte[] Entropy = "DailyDocket outbox export"u8.ToArray();

    /// <summary>
    /// The export holds Mike's unsent replies and their recipients, so it is encrypted for his
    /// Windows account (DPAPI) rather than left beside the encrypted database in plain text. DPAPI
    /// rather than the database key: the export exists to survive losing the database, and must
    /// not be lost with it. Anyone signed in as Mike can still read it, as with the database key.
    /// </summary>
    internal static byte[] Protect(byte[] json) => OperatingSystem.IsWindows()
        ? System.Security.Cryptography.ProtectedData.Protect(json, Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser)
        : json;

    /// <summary>The export's JSON. What DPAPI cannot open (a plain file from an earlier version) is returned as it is.</summary>
    internal static byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows())
        {
            return data;
        }

        try
        {
            return System.Security.Cryptography.ProtectedData.Unprotect(data, Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return data;
        }
    }
}
