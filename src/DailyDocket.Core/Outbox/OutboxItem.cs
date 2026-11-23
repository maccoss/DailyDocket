namespace DailyDocket.Core.Outbox;

/// <summary>Where an item is on its way out (design, Offline compose and the outbox).</summary>
public enum OutboxStatus
{
    /// <summary>Being written; saved on every pause. Never leaves the machine.</summary>
    Draft,

    /// <summary>Mike pressed Send; waits out the undo window.</summary>
    Queued,

    /// <summary>The Send job claimed it.</summary>
    Sending,

    /// <summary>The connector returned an id.</summary>
    Sent,

    /// <summary>The conversation moved on since he wrote; waits for him to confirm, edit or discard.</summary>
    Held,

    /// <summary>The connector or the guard refused; shown with the reason.</summary>
    Failed,
}

/// <summary>What kind of message an item is.</summary>
public enum OutboxKind
{
    /// <summary>A reply in a Gmail thread, to exactly the recipients chosen.</summary>
    EmailReply,

    /// <summary>A Slack message: in a thread when it has one, otherwise in the DM or channel.</summary>
    SlackReply,
}

/// <summary>One thing Mike wrote, on its way out.</summary>
/// <param name="Target">The Gmail thread id, or the Slack conversation key.</param>
/// <param name="ReplyTo">Email: the id of the message replied to (the thread's newest). Slack: the thread's ts, or null outside a thread.</param>
/// <param name="Channel">Slack: the channel or DM id.</param>
/// <param name="Base">
/// The newest message in the conversation when he started writing (email message id, Slack ts).
/// Anything newer when it is about to send puts the item on Held.
/// </param>
/// <param name="Document">The compose box's own document (XAML), so a draft reopens exactly as he left it.</param>
/// <param name="Html">The formatted body (email); empty for Slack.</param>
/// <param name="Text">The plain-text body (email's alternative), or the Markdown message (Slack).</param>
public sealed record OutboxItem(
    string Id,
    OutboxKind Kind,
    OutboxStatus Status,
    string Target,
    string? ReplyTo,
    string? Channel,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string Base,
    string Document,
    string Html,
    string Text,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    DateTimeOffset? SendAfter,
    DateTimeOffset? ClaimedAt,
    string Machine,
    int Attempts,
    string? RemoteId,
    string? Problem);
