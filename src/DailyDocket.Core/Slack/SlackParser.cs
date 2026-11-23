using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DailyDocket.Core.Slack;

/// <summary>One Slack message as a search or a thread read reports it.</summary>
/// <param name="Ts">The message timestamp exactly as Slack printed it (it is also the message id).</param>
/// <param name="ThreadTs">The parent's timestamp when the message is in a thread; null otherwise.</param>
public sealed record SlackMessage(
    string ChannelId,
    string ChannelLabel,
    string Participants,
    string FromId,
    string FromName,
    string Ts,
    string? ThreadTs,
    string? Permalink,
    string Text)
{
    public decimal TsValue => decimal.TryParse(Ts, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public DateTimeOffset When => DateTimeOffset.FromUnixTimeMilliseconds((long)(TsValue * 1000));

    public bool IsDm => ChannelLabel.StartsWith("DM", StringComparison.OrdinalIgnoreCase) || ChannelId.StartsWith('D');
}

/// <summary>Reads Slack's results.</summary>
/// <remarks>
/// The search tool answers with a JSON envelope, <c>{results, pagination_info}</c>, around a
/// labeled text report (seen in November 2026), so this reads the labels, as the dashboard
/// artifact did. Anything it cannot parse is skipped rather than guessed at.
/// </remarks>
public static partial class SlackParser
{
    public static (IReadOnlyList<SlackMessage> Messages, string? Cursor) ParseSearch(string responseText)
    {
        var (report, pagination) = Unwrap(responseText);
        var messages = new List<SlackMessage>();
        foreach (var block in ResultHeader().Split(report).Skip(1))
        {
            var channelId = Grab(block, ChannelId());
            var ts = Grab(block, MessageTs());
            if (channelId.Length == 0 || ts.Length == 0)
            {
                continue;
            }

            var fromId = Grab(block, FromId());
            var permalink = Grab(block, Permalink());

            // A channel is not a conversation: Slack puts the parent id in the permalink, and
            // without it a reply in an unrelated thread looks like an answer to this question. It
            // is empty for a message not in a thread, which is the normal case for a DM.
            var threadTs = Grab(permalink, ThreadTsParam());

            var hit = new SlackMessage(
                channelId,
                Grab(block, ChannelLabel()).Trim(),
                Grab(block, Participants()),
                fromId,
                SenderName(block, fromId),
                ts,
                threadTs.Length == 0 ? null : threadTs,
                permalink.Length == 0 ? null : permalink,
                Body(block));
            messages.Add(hit);
            messages.AddRange(Context(block, hit));
        }

        var cursor = Grab(pagination, Cursor());
        return (messages, cursor.Length == 0 ? null : cursor);
    }

    /// <summary>
    /// True when <paramref name="userId"/> appears among a message's reactions. The reactions tool's
    /// exact shape is not pinned down yet, and the id appearing anywhere in its answer is enough.
    /// </summary>
    public static bool ReactedBy(string reactionsResponse, string userId) =>
        userId.Length > 0 && reactionsResponse.Contains(userId, StringComparison.Ordinal);

    /// <summary>The text report and the pagination prose from the search envelope.</summary>
    internal static (string Report, string Pagination) Unwrap(string responseText)
    {
        var trimmed = responseText.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                return (
                    root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "",
                    root.TryGetProperty("pagination_info", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "");
            }
            catch (JsonException)
            {
                // Not the envelope; read it as the report itself.
            }
        }

        return (responseText, responseText);
    }

    /// <summary>
    /// The messages around a result (include_context). After a thread's parent they are its
    /// replies, and after a DM message they are the DM's next messages, so they belong to the same
    /// conversation. This is what shows that he answered without reading each thread: seen in
    /// November 2026, a mention's context held his reply.
    /// </summary>
    private static IEnumerable<SlackMessage> Context(string block, SlackMessage hit)
    {
        var section = ContextHeader().Match(block);
        if (!section.Success)
        {
            yield break;
        }

        var body = block[(section.Index + section.Length)..];
        var end = body.IndexOf("\n---", StringComparison.Ordinal);
        if (end >= 0)
        {
            body = body[..end];
        }

        foreach (var entry in ContextEntry().Split(body).Skip(1))
        {
            var fromLine = "From: " + entry;
            var ts = Grab(entry, MessageTs());
            if (ts.Length == 0)
            {
                continue;
            }

            var fromId = Grab(fromLine, FromId());
            var afterTs = MessageTs().Match(entry);
            var text = string.Join('\n', entry[(afterTs.Index + afterTs.Length)..].Split('\n').Select(l => l.Trim())).Trim();

            // A reply in a thread carries the thread's parent; when the result was the parent
            // itself (thread_ts equal to its own ts) the thread is the same.
            yield return hit with
            {
                FromId = fromId,
                FromName = SenderName(fromLine, fromId),
                Ts = ts,
                Permalink = null,
                Text = text,
            };
        }
    }

    /// <summary>The message text: after "Text:", up to the next result or its context section.</summary>
    private static string Body(string block)
    {
        var start = TextLabel().Match(block);
        if (!start.Success)
        {
            return "";
        }

        var body = block[(start.Index + start.Length)..];
        var end = BodyEnd().Match(body);
        return (end.Success ? body[..end.Index] : body).Trim();
    }

    /// <summary>Apps and some accounts carry no display name: fall back to the address, then the id.</summary>
    private static string SenderName(string block, string fromId)
    {
        var name = Grab(block, FromName()).Trim();
        if (name.Length > 0)
        {
            return name;
        }

        var address = Grab(block, FromAddress());
        if (address.Length > 0)
        {
            return address.Split('@')[0];
        }

        return fromId.StartsWith('B') ? $"app {fromId}" : fromId;
    }

    private static string Grab(string text, Regex regex) => regex.Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : "";

    [GeneratedRegex(@"###\s+Result\s+\d+\s+of\s+\d+")]
    private static partial Regex ResultHeader();

    [GeneratedRegex(@"Channel:[^\n]*\(ID:\s*([A-Z0-9]+)\)")]
    private static partial Regex ChannelId();

    [GeneratedRegex(@"Channel:\s*([^\n(]*)")]
    private static partial Regex ChannelLabel();

    [GeneratedRegex(@"Participants:\s*([^\n]+)")]
    private static partial Regex Participants();

    [GeneratedRegex(@"From:[^\n]*\(ID:\s*([A-Z0-9]+)\)")]
    private static partial Regex FromId();

    [GeneratedRegex(@"From:[ \t]*([^<(\n]+)")]
    private static partial Regex FromName();

    [GeneratedRegex(@"From:[^\n]*<([^>]+)>")]
    private static partial Regex FromAddress();

    [GeneratedRegex(@"Message_ts:\s*([\d.]+)")]
    private static partial Regex MessageTs();

    [GeneratedRegex(@"Permalink:\s*\[[^\]]*\]\(([^)]+)\)")]
    private static partial Regex Permalink();

    [GeneratedRegex(@"thread_ts=([\d.]+)")]
    private static partial Regex ThreadTsParam();

    [GeneratedRegex(@"\nText:[ \t]*\n?")]
    private static partial Regex TextLabel();

    [GeneratedRegex(@"\n(---|Context (after|before):)")]
    private static partial Regex BodyEnd();

    [GeneratedRegex(@"cursor[^`]*`([^`]+)`")]
    private static partial Regex Cursor();

    [GeneratedRegex(@"\nContext after:[ \t]*\n")]
    private static partial Regex ContextHeader();

    [GeneratedRegex(@"(?m)^-\s+From:\s*")]
    private static partial Regex ContextEntry();
}
