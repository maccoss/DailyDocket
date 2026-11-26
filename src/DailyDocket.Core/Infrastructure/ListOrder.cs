namespace DailyDocket.Core.Infrastructure;

/// <summary>How the Email and Slack lists are sorted; each tab keeps its own.</summary>
public enum ListOrder
{
    /// <summary>Who has waited longest at the top: the dashboard artifact's order, and the default.</summary>
    OldestFirst,

    NewestFirst,

    /// <summary>By the person waiting, alphabetically; their newest first.</summary>
    Sender,

    /// <summary>By subject (Email) or channel and conversation (Slack), alphabetically; newest first within one.</summary>
    Topic,
}

/// <summary>Sorting for the Email and Slack lists.</summary>
public static class ListOrdering
{
    /// <summary>
    /// <paramref name="items"/> in <paramref name="order"/>. Pinned items (unsent drafts) stay on
    /// top whatever the order: they are Mike's own half-finished replies. Ties fall to the oldest,
    /// so the list does not shuffle between refreshes.
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> items, ListOrder order, Func<T, DateTimeOffset?> waitingSince, Func<T, string> sender, Func<T, string> topic, Func<T, bool>? pinned = null)
    {
        var when = (Func<T, DateTimeOffset>)(i => waitingSince(i) ?? DateTimeOffset.MaxValue);
        var top = items.OrderBy(i => pinned?.Invoke(i) == true ? 0 : 1);
        var sorted = order switch
        {
            ListOrder.NewestFirst => top.ThenByDescending(when),
            ListOrder.Sender => top.ThenBy(i => sender(i).Trim(), StringComparer.CurrentCultureIgnoreCase).ThenByDescending(when),
            ListOrder.Topic => top.ThenBy(i => TopicKey(topic(i)), StringComparer.CurrentCultureIgnoreCase).ThenByDescending(when),
            _ => top.ThenBy(when),
        };
        return [.. sorted];
    }

    /// <summary>"Re: Fwd: QC report" sorts as "QC report", and "#lab" as "lab".</summary>
    public static string TopicKey(string topic)
    {
        var t = topic.Trim();
        while (true)
        {
            var stripped = t.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? t[3..]
                : t.StartsWith("fw:", StringComparison.OrdinalIgnoreCase) ? t[3..]
                : t.StartsWith("fwd:", StringComparison.OrdinalIgnoreCase) ? t[4..]
                : t.StartsWith('#') ? t[1..]
                : null;
            if (stripped is null)
            {
                return t;
            }

            t = stripped.TrimStart();
        }
    }
}
