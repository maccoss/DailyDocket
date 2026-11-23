using System.Globalization;
using System.Text.Json;

namespace DailyDocket.Core.Mail;

/// <summary>A thread as a search lists it: in summary view, its newest message only.</summary>
public sealed record ThreadSummary(
    string ThreadId, int MessageCount, string NewestMessageId, string Sender, IReadOnlyList<string> To, IReadOnlyList<string> Cc, string? Subject);

/// <summary>One message, as <c>get_thread</c> returns it.</summary>
public sealed record MailMessage(
    string Id,
    string ThreadId,
    string Sender,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DateTimeOffset? Date,
    IReadOnlyList<string> Labels,
    string? Subject,
    string? Snippet,
    string? Body);

/// <summary>An unsent draft. Drafts are invisible to thread reads, so they are listed on their own.</summary>
public sealed record MailDraft(string Id, string ThreadId, DateTimeOffset? Date);

/// <summary>Turns Gmail connector results into records.</summary>
/// <remarks>
/// Seen on the connector in November 2026: search results carry ids, senders, recipients and
/// (in the minimal and summary views) the subject, but no date, labels or snippet, whatever the
/// tool's description promises. Only <c>get_thread</c> has dates. An empty result is <c>{}</c>.
/// </remarks>
public static class GmailParser
{
    public static (IReadOnlyList<ThreadSummary> Threads, string? NextPageToken) ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var threads = new List<ThreadSummary>();
        if (root.TryGetProperty("threads", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in list.EnumerateArray())
            {
                var id = Str(t, "id");
                if (id is null)
                {
                    continue;
                }

                // In summary view the one message is the newest; in the other views the subset is
                // the thread's oldest few, so its last entry is the newest the search saw.
                var messages = t.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().ToList()
                    : [];
                var newest = messages.Count > 0 ? messages[^1] : default;
                threads.Add(new ThreadSummary(
                    id,
                    t.TryGetProperty("messageCount", out var c) && c.TryGetInt32(out var count) ? count : messages.Count,
                    newest.ValueKind == JsonValueKind.Object ? Str(newest, "id") ?? id : id,
                    newest.ValueKind == JsonValueKind.Object ? Str(newest, "sender") ?? "" : "",
                    newest.ValueKind == JsonValueKind.Object ? Strings(newest, "toRecipients") : [],
                    newest.ValueKind == JsonValueKind.Object ? Strings(newest, "ccRecipients") : [],
                    newest.ValueKind == JsonValueKind.Object ? Str(newest, "subject") : null));
            }
        }

        return (threads, Str(root, "nextPageToken"));
    }

    public static (string? ThreadId, IReadOnlyList<MailMessage> Messages) ParseThread(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var threadId = Str(root, "id");
        var messages = new List<MailMessage>();
        if (root.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in list.EnumerateArray())
            {
                var id = Str(m, "id");
                if (id is null)
                {
                    continue;
                }

                messages.Add(new MailMessage(
                    id,
                    Str(m, "threadId") ?? threadId ?? "",
                    Named(Str(m, "sender") ?? "", m),
                    Strings(m, "toRecipients"),
                    Strings(m, "ccRecipients"),
                    MessageDate(m),
                    Strings(m, "labelIds"),
                    Str(m, "subject"),
                    Str(m, "snippet"),
                    Str(m, "plaintextBody")));
            }
        }

        messages.Sort((a, b) => Nullable.Compare(a.Date, b.Date));
        return (threadId ?? messages.FirstOrDefault()?.ThreadId, messages);
    }

    public static IReadOnlyList<MailDraft> ParseDrafts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var drafts = new List<MailDraft>();
        if (doc.RootElement.TryGetProperty("drafts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in list.EnumerateArray())
            {
                if (Str(d, "id") is { } id && Str(d, "threadId") is { } threadId)
                {
                    drafts.Add(new MailDraft(id, threadId, ParseDate(Str(d, "date"))));
                }
            }
        }

        return drafts;
    }

    /// <summary>
    /// The sender as "Name &lt;address&gt;". A thread read gives the bare address as sender and the
    /// names separately (addressDisplayNames), so the tab can say who is waiting.
    /// </summary>
    private static string Named(string sender, JsonElement m)
    {
        if (sender.Contains('<', StringComparison.Ordinal) || !m.TryGetProperty("addressDisplayNames", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return sender;
        }

        foreach (var n in names.EnumerateArray())
        {
            if (string.Equals(Str(n, "emailAddress"), sender, StringComparison.OrdinalIgnoreCase) && Str(n, "displayName") is { Length: > 0 } name)
            {
                return $"{name} <{sender}>";
            }
        }

        return sender;
    }

    /// <summary>internalDate (milliseconds since 1970) is exact; the date header is the fallback.</summary>
    private static DateTimeOffset? MessageDate(JsonElement m)
    {
        if (m.TryGetProperty("internalDate", out var internalDate))
        {
            var text = internalDate.ValueKind == JsonValueKind.String ? internalDate.GetString() : internalDate.GetRawText();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }
        }

        return ParseDate(Str(m, "date"));
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];
}
