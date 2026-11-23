using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DailyDocket.Core.Todos;

/// <summary>
/// The to-dos and Handled marks in the data repository's working folder: one file per item.
/// </summary>
/// <remarks>
/// <para>
/// One file per item means two machines editing different tasks offline never conflict, and a
/// reorder rewrites one file. The files are written by hand rather than by a YAML serializer so a
/// change shows in git as the one line that changed.
/// </para>
/// <para>
/// No message bodies go in this repository: a task carries what Mike typed and its source's id and
/// subject, and a Handled mark carries ids and a time.
/// </para>
/// </remarks>
public sealed class DataRepository
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions Quote = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public DataRepository(string root) => Root = root;

    public string Root { get; }

    public string TodoFolder => Path.Combine(Root, "todos");

    public string ConfigFile => Path.Combine(Root, "config", "docket.yaml");

    private string HandledFolder(string kind) => Path.Combine(Root, "handled", kind);

    // ---- to-dos -----------------------------------------------------------------

    public IReadOnlyList<TodoItem> LoadTodos()
    {
        if (!Directory.Exists(TodoFolder))
        {
            return [];
        }

        var items = new List<TodoItem>();
        foreach (var file in Directory.EnumerateFiles(TodoFolder, "*.md"))
        {
            if (Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    public void Save(TodoItem item)
    {
        Directory.CreateDirectory(TodoFolder);
        WriteAtomically(Path.Combine(TodoFolder, $"{item.Id}.md"), Format(item));
    }

    public void Delete(string id)
    {
        var path = Path.Combine(TodoFolder, $"{id}.md");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static string Format(TodoItem t)
    {
        string Q(string s) => JsonSerializer.Serialize(s, Quote);
        string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("title: ").Append(Q(t.Title)).Append('\n');
        if (t.Due is { } due)
        {
            sb.Append("due: ").Append(due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("tags: [").Append(string.Join(", ", t.Tags.Select(Q))).Append("]\n");
        sb.Append("rank: ").Append(t.Rank.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("done: ").Append(t.Done ? "true" : "false").Append('\n');
        if (t.DoneAt is { } doneAt)
        {
            sb.Append("doneAt: ").Append(Iso(doneAt)).Append('\n');
        }

        if (t.Source is { } s)
        {
            sb.Append("source: {kind: ").Append(s.Kind).Append(", id: ").Append(Q(s.Id));
            if (s.Subject is { Length: > 0 })
            {
                sb.Append(", subject: ").Append(Q(s.Subject));
            }

            sb.Append("}\n");
        }

        sb.Append("created: ").Append(Iso(t.CreatedAt)).Append('\n');
        sb.Append("updated: ").Append(Iso(t.Updated)).Append('\n');
        sb.Append("updatedOn: ").Append(t.UpdatedOn).Append('\n');
        sb.Append("---\n");
        if (t.Notes.Length > 0)
        {
            sb.Append(t.Notes.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()).Append('\n');
        }

        return sb.ToString();
    }

    internal static TodoItem? Parse(string id, string text)
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

        TodoFront? f;
        try
        {
            f = Yaml.Deserialize<TodoFront?>(text[4..end]);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null; // A file hand-edited into something unreadable is skipped, not fatal.
        }

        if (f is null || string.IsNullOrWhiteSpace(f.Title))
        {
            return null;
        }

        var bodyStart = text.IndexOf('\n', end + 1);
        var notes = bodyStart < 0 ? "" : text[(bodyStart + 1)..].Trim();
        return new TodoItem(
            id,
            f.Title.Trim(),
            notes,
            DateOnly.TryParseExact(f.Due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) ? due : null,
            f.Tags ?? [],
            f.Rank,
            f.Done,
            Time(f.DoneAt),
            Time(f.Created) ?? Time(f.Updated) ?? DateTimeOffset.UnixEpoch,
            f.Source is { Kind: { Length: > 0 } kind, Id: { Length: > 0 } sid } ? new TodoSource(kind, sid, f.Source.Subject) : null,
            Time(f.Updated) ?? DateTimeOffset.UnixEpoch,
            f.UpdatedOn ?? "");
    }

    // ---- handled marks ------------------------------------------------------------

    public IReadOnlyDictionary<string, HandledMark> LoadHandled(string kind)
    {
        var folder = HandledFolder(kind);
        var marks = new Dictionary<string, HandledMark>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
        {
            return marks;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.yaml"))
        {
            try
            {
                var m = Yaml.Deserialize<HandledFront?>(File.ReadAllText(file));
                var key = Path.GetFileNameWithoutExtension(file);
                if (m?.UpTo is { Length: > 0 } upTo)
                {
                    marks[key] = new HandledMark(kind, key, upTo, Time(m.At) ?? DateTimeOffset.UnixEpoch, m.Machine ?? "");
                }
            }
            catch (YamlDotNet.Core.YamlException)
            {
                // Skipped, as for tasks.
            }
        }

        return marks;
    }

    public void Save(HandledMark mark)
    {
        var folder = HandledFolder(mark.Kind);
        Directory.CreateDirectory(folder);
        // "machine", not "on": YAML 1.1 readers take a bare on as the boolean true.
        var text = $"upTo: \"{mark.UpTo}\"\nat: {mark.At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}\nmachine: {mark.On}\n";
        WriteAtomically(Path.Combine(folder, $"{SafeName(mark.Key)}.yaml"), text);
    }

    public void RemoveHandled(string kind, string key)
    {
        var path = Path.Combine(HandledFolder(kind), $"{SafeName(key)}.yaml");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Thread ids and conversation keys are letters, digits, "." and "~"; anything else would be a path.</summary>
    internal static string SafeName(string key)
    {
        if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '~' or '-' or '_')))
        {
            throw new ArgumentException($"'{key}' is not a thread id or conversation key.", nameof(key));
        }

        return key;
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private sealed class TodoFront
    {
        public string Title { get; set; } = "";

        public string? Due { get; set; }

        public List<string>? Tags { get; set; }

        public double Rank { get; set; }

        public bool Done { get; set; }

        public string? DoneAt { get; set; }

        public SourceFront? Source { get; set; }

        public string? Created { get; set; }

        public string? Updated { get; set; }

        public string? UpdatedOn { get; set; }
    }

    private sealed class SourceFront
    {
        public string? Kind { get; set; }

        public string? Id { get; set; }

        public string? Subject { get; set; }
    }

    private sealed class HandledFront
    {
        public string? UpTo { get; set; }

        public string? At { get; set; }

        public string? Machine { get; set; }
    }
}
