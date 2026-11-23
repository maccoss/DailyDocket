namespace DailyDocket.Core.Todos;

/// <summary>Every Handled mark, as the tabs and triage consult them.</summary>
public sealed record HandledSet(IReadOnlyDictionary<string, HandledMark> Email, IReadOnlyDictionary<string, HandledMark> Slack)
{
    public static readonly HandledSet Empty = new(new Dictionary<string, HandledMark>(), new Dictionary<string, HandledMark>());

    /// <summary>Handled, and nobody has written since: hidden from the tab and from triage.</summary>
    public bool CoversEmail(string threadId, DateTimeOffset? waitingSince) =>
        Email.TryGetValue(threadId, out var mark) && TodoRules.CoversEmail(mark, waitingSince);

    public bool CoversSlack(string key, string lastTs) =>
        Slack.TryGetValue(key, out var mark) && TodoRules.CoversSlack(mark, lastTs);

    /// <summary>
    /// A Slack conversation is handled by a mark on itself, or, for a thread, by one on its channel
    /// outside threads that reaches past the thread's newest message.
    /// </summary>
    /// <remarks>
    /// A thread's first message used to be grouped with channel~main, where its Handled mark was
    /// made; it now belongs to the thread (SlackRules.Group), which has its own key. Without this, a
    /// conversation he had handled came back, and was paid for in triage again (code review,
    /// November 2026).
    /// </remarks>
    public bool CoversSlack(DailyDocket.Core.Slack.SlackConversation conversation) =>
        CoversSlack(conversation.Key, conversation.Last.Ts)
        || (conversation.ThreadTs is not null && CoversSlack(DailyDocket.Core.Slack.SlackRules.ConversationKey(conversation.ChannelId, null), conversation.Last.Ts));
}
