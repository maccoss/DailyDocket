using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DailyDocket.Core.Todos;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DailyDocket.Core.Prep;

/// <summary>
/// Reads and writes briefs: YAML front matter for what the app needs (the event, its updated time,
/// who wrote it and from what), then Markdown sections a person can read on GitHub.
/// </summary>
/// <remarks>
/// The sections are the stored form, not a rendering of something stored elsewhere, so the file is
/// both what Mike reads on his phone and what the app reads back. Each item is one bullet line,
/// which is why <see cref="BriefRules"/> collapses line breaks and keeps separators out of names.
/// </remarks>
public static partial class BriefFile
{
    private const string Who = "Who";
    private const string Agenda = "Agenda";
    private const string Context = "Context";
    private const string Owes = "You owe";
    private const string Gaps = "Gaps";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions Quote = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Folder(string repositoryRoot) => Path.Combine(repositoryRoot, "briefs");

    /// <summary>briefs/&lt;local date&gt;/&lt;eventId&gt;.md; the event id is checked, since it becomes a path.</summary>
    public static string PathFor(string repositoryRoot, DateOnly date, string eventId) =>
        Path.Combine(Folder(repositoryRoot), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), $"{DataRepository.SafeName(eventId)}.md");

    public static MeetingBrief? Load(string repositoryRoot, DateOnly date, string eventId)
    {
        var path = PathFor(repositoryRoot, date, eventId);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>Every brief for the given days, by event id.</summary>
    public static IReadOnlyDictionary<string, MeetingBrief> LoadDays(string repositoryRoot, IEnumerable<DateOnly> days)
    {
        var result = new Dictionary<string, MeetingBrief>(StringComparer.Ordinal);
        foreach (var day in days)
        {
            var folder = Path.Combine(Folder(repositoryRoot), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
            {
                if (Parse(File.ReadAllText(file)) is { } brief)
                {
                    result[brief.EventId] = brief;
                }
            }
        }

        return result;
    }

    public static void Save(string repositoryRoot, DateOnly date, MeetingBrief brief)
    {
        var path = PathFor(repositoryRoot, date, brief.EventId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Format(brief), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Applies the caps and cleaning, so what is saved is what reads back.</summary>
    public static MeetingBrief Clean(MeetingBrief b) => b with
    {
        Title = BriefRules.Line(b.Title, 200),
        Attendees =
        [
            .. b.Attendees
                .Select(a => new BriefAttendee(BriefRules.Name(a.Name), BriefRules.Name(a.Org), Role(a.Role), a.Organizer))
                .Where(a => a.Name.Length > 0)
                .Take(BriefRules.MaxAttendees),
        ],
        Agenda = BriefRules.Items(b.Agenda),
        Context = BriefRules.Items(b.Context),
        Owes = BriefRules.Items(b.Owes),
        Gaps = BriefRules.Items(b.Gaps),
        Sources = BriefRules.Sources(b.Sources),
        WrittenOn = BriefRules.Name(b.WrittenOn),
        Model = BriefRules.Name(b.Model),
    };

    public static string Format(MeetingBrief brief)
    {
        var b = Clean(brief);
        string Q(string s) => JsonSerializer.Serialize(s, Quote);
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("eventId: ").Append(Q(b.EventId)).Append('\n');
        sb.Append("title: ").Append(Q(b.Title)).Append('\n');
        sb.Append("start: ").Append(Iso(b.Start)).Append('\n');
        if (b.EventUpdated is { } u)
        {
            sb.Append("eventUpdated: ").Append(Iso(u)).Append('\n');
        }

        sb.Append("written: ").Append(Iso(b.Written)).Append('\n');
        sb.Append("writtenOn: ").Append(Q(b.WrittenOn)).Append('\n');
        sb.Append("model: ").Append(Q(b.Model)).Append('\n');
        sb.Append("sources: [").Append(string.Join(", ", b.Sources.Select(Q))).Append("]\n");
        sb.Append("---\n");

        Section(sb, Who, b.Attendees.Select(Attendee));
        Section(sb, Agenda, b.Agenda);
        Section(sb, Context, b.Context);
        Section(sb, Owes, b.Owes);
        Section(sb, Gaps, b.Gaps);
        return sb.ToString();
    }

    /// <summary>Null for a file that is not a brief or cannot be read: skipped, never fatal.</summary>
    public static MeetingBrief? Parse(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        Front? f;
        try
        {
            f = Yaml.Deserialize<Front?>(text[4..end]);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        if (f?.EventId is not { Length: > 0 } eventId || Time(f.Start) is not { } start)
        {
            return null;
        }

        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        var bodyStart = text.IndexOf('\n', end + 1);
        foreach (var raw in bodyStart < 0 ? [] : text[(bodyStart + 1)..].Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = sections[line[3..].Trim()] = [];
            }
            else if (current is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                current.Add(line);
            }
        }

        IReadOnlyList<string> Items(string name) =>
            sections.TryGetValue(name, out var lines) ? [.. lines.Select(l => l[2..].Trim())] : [];

        return new MeetingBrief(
            eventId,
            f.Title ?? "",
            start,
            Time(f.EventUpdated),
            sections.TryGetValue(Who, out var who) ? [.. who.Select(ParseAttendee).OfType<BriefAttendee>()] : [],
            Items(Agenda),
            Items(Context),
            Items(Owes),
            Items(Gaps),
            f.Sources ?? [],
            Time(f.Written) ?? DateTimeOffset.UnixEpoch,
            f.WrittenOn ?? "",
            f.Model ?? "");
    }

    /// <summary>"- Name (Org): role [organizer]", each part after the name optional.</summary>
    internal static string Attendee(BriefAttendee a)
    {
        var sb = new StringBuilder("- ").Append(a.Name);
        if (a.Org.Length > 0)
        {
            sb.Append(" (").Append(a.Org).Append(')');
        }

        if (a.Role.Length > 0)
        {
            sb.Append(": ").Append(a.Role);
        }

        if (a.Organizer)
        {
            sb.Append(" [organizer]");
        }

        return sb.ToString();
    }

    private static BriefAttendee? ParseAttendee(string line)
    {
        var m = AttendeeLine().Match(line);
        return m.Success
            ? new BriefAttendee(m.Groups["name"].Value.Trim(), m.Groups["org"].Value.Trim(), m.Groups["role"].Value.Trim(), m.Groups["organizer"].Success)
            : null;
    }

    private static string Role(string? role)
    {
        var s = BriefRules.Line(role, BriefRules.MaxRoleLength);
        return s.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Trim();
    }

    private static void Section(StringBuilder sb, string heading, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        sb.Append('\n').Append("## ").Append(heading).Append('\n');
        foreach (var item in list)
        {
            sb.Append(item.StartsWith("- ", StringComparison.Ordinal) ? item : "- " + item).Append('\n');
        }
    }

    private static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    [GeneratedRegex(@"^- (?<name>[^():\[\]]+?)(?: \((?<org>[^()]*)\))?(?:: (?<role>.*?))?(?<organizer> \[organizer\])?$")]
    private static partial Regex AttendeeLine();

    private sealed class Front
    {
        public string? EventId { get; set; }

        public string? Title { get; set; }

        public string? Start { get; set; }

        public string? EventUpdated { get; set; }

        public string? Written { get; set; }

        public string? WrittenOn { get; set; }

        public string? Model { get; set; }

        public List<string>? Sources { get; set; }
    }
}
