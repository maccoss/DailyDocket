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
}
