namespace DailyDocket.Core.Todos;

/// <summary>Where a task came from, when it was captured from a message.</summary>
/// <param name="Kind">email or slack.</param>
/// <param name="Id">The Gmail thread id, or the Slack conversation key (channel~threadTs).</param>
public sealed record TodoSource(string Kind, string Id, string? Subject = null);

/// <summary>One task on the list: a file in the data repository's todos folder.</summary>
/// <param name="Rank">
/// Fractional: dropping a task between two others takes the midpoint, so a reorder rewrites one
/// file, and two machines reordering different tasks offline never conflict.
/// </param>
public sealed record TodoItem(
    string Id,
    string Title,
    string Notes,
    DateOnly? Due,
    IReadOnlyList<string> Tags,
    double Rank,
    bool Done,
    DateTimeOffset? DoneAt,
    DateTimeOffset CreatedAt,
    TodoSource? Source,
    DateTimeOffset Updated,
    string UpdatedOn);

/// <summary>
/// A conversation marked handled: hidden until someone writes in it again.
/// </summary>
/// <param name="Kind">email or slack.</param>
/// <param name="Key">The Gmail thread id, or the Slack conversation key.</param>
/// <param name="UpTo">
/// The newest message it covers: for email the time of the message he was waiting on (ISO 8601),
/// for Slack that message's ts. A newer message brings the conversation back.
/// </param>
public sealed record HandledMark(string Kind, string Key, string UpTo, DateTimeOffset At, string On);
