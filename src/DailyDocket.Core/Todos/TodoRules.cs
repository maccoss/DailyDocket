using System.Globalization;
using System.Security.Cryptography;

namespace DailyDocket.Core.Todos;

/// <summary>The dashboard artifact's to-do rules, as pure functions.</summary>
public static class TodoRules
{
    /// <summary>How many colors a tag can take.</summary>
    public const int TagHues = 10;

    /// <summary>
    /// The open list in its dragged order. Unranked tasks (none, once imported) follow, by due date
    /// and then age.
    /// </summary>
    public static IReadOnlyList<TodoItem> OpenInOrder(IEnumerable<TodoItem> items) =>
    [
        .. items.Where(t => !t.Done)
            .OrderBy(t => t.Rank)
            .ThenBy(t => t.Due is null)
            .ThenBy(t => t.Due)
            .ThenBy(t => t.CreatedAt),
    ];

    /// <summary>Done tasks, most recently finished first.</summary>
    public static IReadOnlyList<TodoItem> DoneRecentFirst(IEnumerable<TodoItem> items) =>
        [.. items.Where(t => t.Done).OrderByDescending(t => t.DoneAt ?? t.Updated)];

    /// <summary>A new task goes to the bottom of the open list.</summary>
    public static double BottomRank(IEnumerable<TodoItem> items) =>
        items.Where(t => !t.Done).Select(t => t.Rank).DefaultIfEmpty(0).Max() + 1;

    /// <summary>
    /// The rank for a task dropped between <paramref name="before"/> and <paramref name="after"/>
    /// (either may be missing at the ends): the midpoint, so only the moved task's file changes.
    /// </summary>
    public static double RankBetween(TodoItem? before, TodoItem? after) => (before, after) switch
    {
        (not null, not null) => (before.Rank + after.Rank) / 2,
        (not null, null) => before.Rank + 1,
        (null, not null) => after.Rank - 1,
        _ => 1,
    };

    /// <summary>The artifact's due labels: "3d late", "today", "tomorrow", a weekday, or a date.</summary>
    public static (string Text, bool Late) DueLabel(DateOnly? due, DateOnly today)
    {
        if (due is not { } d)
        {
            return ("no date", false);
        }

        var days = d.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => ($"{-days}d late", true),
            0 => ("today", false),
            1 => ("tomorrow", false),
            < 7 => (d.ToString("ddd", CultureInfo.InvariantCulture), false),
            _ => (d.ToString("MMM d", CultureInfo.InvariantCulture), false),
        };
    }

    /// <summary>Tags typed as "a, b, a" become ["a", "b"], at most eight.</summary>
    public static IReadOnlyList<string> ParseTags(string? text) =>
        [.. (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8)];

    /// <summary>
    /// A tag's color: the artifact's stable hash of the name (djb2 with xor), so a tag has the same
    /// color on every machine without any setup.
    /// </summary>
    public static int TagHue(string tag)
    {
        uint h = 5381;
        foreach (var c in tag.ToLowerInvariant())
        {
            h = unchecked((h * 33) ^ c);
        }

        return (int)(h % TagHues);
    }

    /// <summary>A short random id, safe as a file name: t followed by base36 time and randomness, as the artifact made them.</summary>
    public static string NewId(DateTimeOffset now)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        static string Base36(ulong v)
        {
            var s = "";
            do
            {
                s = digits[(int)(v % 36)] + s;
                v /= 36;
            }
            while (v > 0);
            return s;
        }

        var random = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) % (36 * 36 * 36 * 36);
        return "t" + Base36((ulong)now.ToUnixTimeMilliseconds()) + Base36(random).PadLeft(4, '0');
    }

    /// <summary>Whether a handled mark still covers an email thread whose newest unanswered message is at <paramref name="waitingSince"/>.</summary>
    public static bool CoversEmail(HandledMark mark, DateTimeOffset? waitingSince) =>
        waitingSince is null ||
        (DateTimeOffset.TryParse(mark.UpTo, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var upTo) && waitingSince <= upTo);

    /// <summary>Whether a handled mark still covers a Slack conversation whose last message is <paramref name="lastTs"/>.</summary>
    public static bool CoversSlack(HandledMark mark, string lastTs) =>
        decimal.TryParse(mark.UpTo, NumberStyles.Number, CultureInfo.InvariantCulture, out var upTo) &&
        decimal.TryParse(lastTs, NumberStyles.Number, CultureInfo.InvariantCulture, out var last) &&
        last <= upTo;
}
