using System.Text;
using System.Text.RegularExpressions;

namespace DailyDocket.Core.Slack;

/// <summary>A thread, or a channel's messages outside any thread.</summary>
/// <param name="Key">channel~threadTs, or channel~main outside a thread.</param>
public sealed record SlackConversation(string Key, string ChannelId, string? ThreadTs, IReadOnlyList<SlackMessage> Messages)
{
    public SlackMessage Last => Messages[^1];
}

/// <summary>The dashboard artifact's Slack rules, as pure functions.</summary>
public static partial class SlackRules
{
    /// <summary>
    /// Slack's markup as a person would read it: &lt;@U1|Pat&gt; is @Pat, &lt;#C1|lab&gt; is #lab,
    /// &lt;https://x|label&gt; is "label (https://x)", and a bare &lt;https://x&gt; is the address.
    /// </summary>
    public static string Readable(string text)
    {
        var t = NamedUser().Replace(text, "@$1");
        t = BareUser().Replace(t, "@$1");
        t = NamedChannel().Replace(t, "#$1");
        t = LabeledLink().Replace(t, m => m.Groups[2].Value == m.Groups[1].Value ? m.Groups[1].Value : $"{m.Groups[2].Value} ({m.Groups[1].Value})");
        t = BareLink().Replace(t, "$1");
        return t.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<@[UWB][A-Z0-9]+\|([^>]+)>")]
    private static partial Regex NamedUser();

    [GeneratedRegex(@"<@([UWB][A-Z0-9]+)>")]
    private static partial Regex BareUser();

    [GeneratedRegex(@"<#C[A-Z0-9]+\|([^>]*)>")]
    private static partial Regex NamedChannel();

    [GeneratedRegex(@"<((?:https?|mailto):[^|>]+)\|([^>]+)>")]
    private static partial Regex LabeledLink();

    [GeneratedRegex(@"<((?:https?|mailto):[^|>]+)>")]
    private static partial Regex BareLink();

    /// <summary>
    /// "~" not "/": the key becomes a file name and a database key, and a slash makes it a path.
    /// In a thread, the thread is the conversation; outside one, the channel is.
    /// </summary>
    public static string ConversationKey(string channelId, string? threadTs) => $"{channelId}~{threadTs ?? "main"}";

    /// <summary>
    /// Groups messages from every search (DMs, mentions, and his own channel messages) into
    /// conversations. His own messages are needed: his reply rarely mentions himself, so without
    /// them a channel mention never clears.
    /// </summary>
    /// <remarks>
    /// A message that starts a thread belongs to that thread, as in Slack: its ts is the thread's
    /// ts, though search reports it with no thread. Left with the channel, a DM answered in a thread
    /// ("Reply" on the message) stayed waiting on him, since the channel's last word was still
    /// theirs (November 2026).
    /// </remarks>
    public static IReadOnlyList<SlackConversation> Group(IEnumerable<SlackMessage> messages)
    {
        var all = messages.ToList();
        var threads = all.Where(m => m.ThreadTs is not null).Select(m => (m.ChannelId, m.ThreadTs!)).ToHashSet();
        string? Thread(SlackMessage m) => m.ThreadTs ?? (threads.Contains((m.ChannelId, m.Ts)) ? m.Ts : null);
        return
        [
            .. all
                .Select(m => m with { ThreadTs = Thread(m) })
                .GroupBy(m => ConversationKey(m.ChannelId, m.ThreadTs))
                .Select(g =>
                {
                    var unique = g.GroupBy(m => m.Ts).Select(x => x.First()).OrderBy(m => m.TsValue).ToList();
                    return new SlackConversation(g.Key, unique[0].ChannelId, unique[0].ThreadTs, unique);
                }),
        ];
    }

    /// <summary>Waiting on him: someone else spoke last. A reaction from him is checked separately.</summary>
    public static bool IsWaiting(SlackConversation conversation, string myUserId) =>
        conversation.Last.FromId != myUserId;

    /// <summary>The names of the others in a conversation, for a DM's title.</summary>
    public static string Others(SlackMessage message, string ownerName)
    {
        var names = message.Participants.Split(',')
            .Select(p => p.Split("(ID:")[0].Trim())
            .Where(p => p.Length > 0 && (ownerName.Length == 0 || !p.Contains(ownerName, StringComparison.OrdinalIgnoreCase)));
        var joined = string.Join(", ", names);
        return joined.Length > 0 ? joined : "direct message";
    }

    /// <summary>
    /// Where a conversation is, as the Slack tab and triage show it: the other person's name for a
    /// DM, "Group DM · " and the others' names for a group DM, the channel's name otherwise.
    /// </summary>
    /// <remarks>
    /// A group DM has no name of its own in Slack's search results ("Group DM"), so the label alone
    /// did not say which one to open; Slack lists it under its members' names.
    /// </remarks>
    public static string Where(SlackMessage last, string ownerName) =>
        last.IsDm ? Others(last, ownerName)
        : last.ChannelLabel.Equals("Group DM", StringComparison.OrdinalIgnoreCase) && last.Participants.Length > 0 ? $"Group DM · {Others(last, ownerName)}"
        : last.ChannelLabel;

    /// <summary>What the triage model reads: the last few messages, each capped.</summary>
    public static string TriageTranscript(SlackConversation conversation, string myUserId, string where)
    {
        var sb = new StringBuilder();
        sb.Append("Conversation: ").AppendLine(where).AppendLine();
        foreach (var m in conversation.Messages.TakeLast(6))
        {
            var who = m.FromId == myUserId ? "Mike" : m.FromName.Length > 0 ? m.FromName : "them";
            var text = m.Text.Length > 500 ? m.Text[..500] : m.Text;
            sb.Append(who).Append(": ").AppendLine(text);
        }

        return sb.ToString();
    }
}
