using System.Globalization;
using DailyDocket.Core.Todos;

namespace DailyDocket.Core.Prep;

/// <summary>What a prep run is told about Mike's open loops, so a brief can say what he owes.</summary>
/// <remarks>
/// Titles, subjects and Claude's one-line reasons only: the run reads the threads themselves if it
/// needs more, and nothing here is new mail text.
/// </remarks>
public static class PrepContext
{
    public static IReadOnlyList<string> OpenItems(IEnumerable<TodoItem> todos, IEnumerable<(string From, string Subject, string? Why)> owed)
    {
        var items = new List<string>();
        foreach (var t in TodoRules.OpenInOrder(todos).Take(25))
        {
            var due = t.Due is { } d ? $" (due {d.ToString("MMM d", CultureInfo.InvariantCulture)})" : "";
            var source = t.Source?.Subject is { Length: > 0 } subject ? $" [from \"{subject}\"]" : "";
            items.Add($"To do: {t.Title}{due}{source}");
        }

        foreach (var (from, subject, why) in owed.Take(25))
        {
            items.Add($"Reply owed to {from}: \"{subject}\"" + (why is { Length: > 0 } ? $" ({why})" : ""));
        }

        return items;
    }
}
