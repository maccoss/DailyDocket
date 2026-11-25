using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Outbox;

namespace DailyDocket.Core.Notifications;

/// <summary>Which tab a notice opens.</summary>
public enum NoticeTab
{
    Schedule,
    Email,
    Slack,
}

/// <summary>Something that needs Mike: an owed email or Slack conversation, or an outbox item that stopped.</summary>
/// <param name="Key">The thread id, conversation key, or outbox item id (with its status, so Held then Failed is news twice).</param>
/// <param name="Select">What to select on the tab when the notice is clicked: a thread id or conversation key.</param>
public sealed record AttentionItem(NoticeTab Tab, string Key, string Select, string Title, string Detail);

/// <summary>One Windows notification.</summary>
public sealed record Notice(NoticeTab Tab, string Select, string Title, string Text);

/// <summary>When to notify, as pure functions: only what is new since the last look, one notice per kind.</summary>
public static class NoticeRules
{
    /// <summary>
    /// The notices for what is in <paramref name="now"/> but was not in <paramref name="before"/>.
    /// Null <paramref name="before"/> is the first look after starting: nothing is new then, or every
    /// start would announce the whole Email tab.
    /// </summary>
    public static IReadOnlyList<Notice> Diff(IReadOnlySet<string>? before, IReadOnlyList<AttentionItem> now)
    {
        if (before is null)
        {
            return [];
        }

        var notices = new List<Notice>();
        foreach (var group in now.Where(i => !before.Contains(Id(i))).GroupBy(i => (i.Tab, Outbox: IsOutbox(i))).OrderBy(g => g.Key.Outbox ? 0 : 1))
        {
            var items = group.ToList();
            var first = items[0];
            if (items.Count == 1)
            {
                notices.Add(new Notice(first.Tab, first.Select, first.Title, first.Detail));
                continue;
            }

            var what = group.Key.Outbox ? "items in the outbox need you" : group.Key.Tab == NoticeTab.Slack ? "Slack conversations need a reply" : "emails need a reply";
            notices.Add(new Notice(first.Tab, first.Select, $"{items.Count} {what}", $"{first.Title}: {first.Detail}"));
        }

        return notices;
    }

    /// <summary>
    /// The items Mike asked to hear about. Applied to the items being diffed, never to the
    /// baseline: everything listed still counts as seen, so widening the setting later does not
    /// announce the whole backlog at once.
    /// </summary>
    public static IReadOnlyList<AttentionItem> Wanted(IReadOnlyList<AttentionItem> items, NotifyAbout notify) => notify switch
    {
        NotifyAbout.Everything => items,
        NotifyAbout.SendProblems => [.. items.Where(IsOutbox)],
        _ => [],
    };

    /// <summary>The keys of <paramref name="now"/>, to pass as the next look's "before".</summary>
    public static IReadOnlySet<string> Keys(IEnumerable<AttentionItem> now) => now.Select(Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>An outbox item that stopped (Held or Failed), or null for any other.</summary>
    public static AttentionItem? FromOutbox(OutboxItem item)
    {
        if (item.Status is not (OutboxStatus.Held or OutboxStatus.Failed) || OutboxRules.IsHousekeeping(item.Kind))
        {
            return null;
        }

        var tab = item.Kind switch
        {
            OutboxKind.SlackReply => NoticeTab.Slack,
            OutboxKind.CalendarResponse => NoticeTab.Schedule,
            _ => NoticeTab.Email,
        };
        var title = item.Status == OutboxStatus.Held
            ? "A reply was held: the conversation moved on"
            : item.Kind == OutboxKind.CalendarResponse ? "A calendar response was not sent" : "A reply was not sent";
        var detail = item.Status == OutboxStatus.Held
            ? $"\"{Short(item.Subject)}\". Check what arrived, then send, edit or discard."
            : $"\"{Short(item.Subject)}\": {Short(item.Problem ?? "no reason given")}";
        return new AttentionItem(tab, $"outbox:{item.Id}:{item.Status}", item.Target, title, detail);
    }

    private static bool IsOutbox(AttentionItem i) => i.Key.StartsWith("outbox:", StringComparison.Ordinal);

    private static string Id(AttentionItem i) => $"{i.Tab}:{i.Key}";

    private static string Short(string s) => s.Length <= 80 ? s : s[..80] + "...";
}
