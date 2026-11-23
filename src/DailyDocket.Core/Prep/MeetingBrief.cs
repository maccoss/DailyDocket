using System.Text.RegularExpressions;
using DailyDocket.Core.Drafting;

namespace DailyDocket.Core.Prep;

/// <summary>Someone at the meeting, as the brief describes them.</summary>
public sealed record BriefAttendee(string Name, string Org, string Role, bool Organizer);

/// <summary>
/// One meeting's brief: <c>briefs/&lt;date&gt;/&lt;eventId&gt;.md</c> in the data repository.
/// </summary>
/// <remarks>
/// Briefs are the one place mail-derived text enters git, so every field is a short summary Claude
/// wrote for Mike, capped in length, never a copied message (the repository is private for this reason).
/// </remarks>
/// <param name="EventUpdated">The event's updated time when the brief was written: a later one makes the brief stale.</param>
/// <param name="Sources">Gmail thread ids and links the brief drew on.</param>
public sealed record MeetingBrief(
    string EventId,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset? EventUpdated,
    IReadOnlyList<BriefAttendee> Attendees,
    IReadOnlyList<string> Agenda,
    IReadOnlyList<string> Context,
    IReadOnlyList<string> Owes,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<string> Sources,
    DateTimeOffset Written,
    string WrittenOn,
    string Model);

/// <summary>The caps and cleaning every brief passes, whatever the model wrote.</summary>
public static partial class BriefRules
{
    public const int MaxItems = 8;
    public const int MaxItemLength = 240;
    public const int MaxAttendees = 30;
    public const int MaxNameLength = 80;
    public const int MaxRoleLength = 120;
    public const int MaxSources = 12;

    /// <summary>
    /// One line of brief text: whitespace collapsed (a line break would end the bullet), the house
    /// style applied, and cut at <paramref name="max"/> characters on a word.
    /// </summary>
    public static string Line(string? text, int max = MaxItemLength)
    {
        var s = DraftPlans.HouseStyle(Whitespace().Replace(text ?? "", " ")).Trim();
        if (s.Length <= max)
        {
            return s;
        }

        var cut = s.LastIndexOf(' ', max - 1);
        return (cut > max / 2 ? s[..cut] : s[..(max - 1)]).TrimEnd(',', ';', ' ') + "…";
    }

    /// <summary>Items cleaned, blanks dropped, at most <see cref="MaxItems"/>.</summary>
    public static IReadOnlyList<string> Items(IEnumerable<string?>? items) =>
        [.. (items ?? []).Select(i => Line(i).TrimStart('-', '*', ' ')).Where(i => i.Length > 0).Take(MaxItems)];

    /// <summary>
    /// A name or organization without the characters the file's attendee line uses as separators,
    /// so a brief always reads back as it was written.
    /// </summary>
    public static string Name(string? text, bool allowColon = false)
    {
        var s = Line(text, MaxNameLength);
        s = s.Replace("(", "", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal)
             .Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);
        return allowColon ? s.Trim() : s.Replace(":", "", StringComparison.Ordinal).Trim();
    }

    /// <summary>Only Gmail thread ids and web links are kept as sources: nothing that could be a copied message.</summary>
    public static IReadOnlyList<string> Sources(IEnumerable<string?>? sources) =>
    [
        .. (sources ?? [])
            .Select(s => (s ?? "").Trim())
            .Where(s => SourceId().IsMatch(s) || (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == "https" && s.Length <= 300 && !s.Any(char.IsWhiteSpace)))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSources),
    ];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{6,64}$")]
    private static partial Regex SourceId();
}
