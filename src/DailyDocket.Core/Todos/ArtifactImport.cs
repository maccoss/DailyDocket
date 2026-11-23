using System.Globalization;
using System.Text.Json;

namespace DailyDocket.Core.Todos;

/// <summary>How many items an import brought across.</summary>
public sealed record ImportResult(int Todos, int EmailHandled, int SlackHandled, int Skipped);

/// <summary>
/// Brings the dashboard artifact's to-dos and Handled marks into the data repository, from its
/// database exported as one JSON file per document (todos/, handled/, slackHandled/).
/// </summary>
/// <remarks>
/// Ids are kept, and an item already in the repository is left alone, so running it twice changes
/// nothing. From then on the repository is the only to-do list (design, Open decisions).
/// </remarks>
public static class ArtifactImport
{
    public static ImportResult Import(string exportFolder, DataRepository repo, string machine, DateTimeOffset now)
    {
        var existing = repo.LoadTodos().Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var all = ReadAll(Path.Combine(exportFolder, "todos")).Select(d => ToTodo(d.Id, d.Doc, machine, now)).ToList();
        var todos = all.OfType<TodoItem>().ToList();
        var skipped = all.Count - todos.Count; // no title

        // The artifact sorted ranked tasks first, then by due date, then by age; give the unranked
        // ones ranks after the last ranked one in that order, so the list looks as it did.
        var maxRank = todos.Where(t => t.Rank > 0).Select(t => t.Rank).DefaultIfEmpty(0).Max();
        var ordered = todos.Where(t => t.Rank > 0).OrderBy(t => t.Rank)
            .Concat(todos.Where(t => t.Rank <= 0).OrderBy(t => t.Due is null).ThenBy(t => t.Due).ThenBy(t => t.CreatedAt).Select((t, i) => t with { Rank = maxRank + i + 1 }));

        var imported = 0;
        foreach (var todo in ordered)
        {
            if (existing.Contains(todo.Id))
            {
                skipped++;
                continue;
            }

            repo.Save(todo);
            imported++;
        }

        var email = 0;
        var emailExisting = repo.LoadHandled("email");
        foreach (var (id, doc) in ReadAll(Path.Combine(exportFolder, "handled")))
        {
            if (emailExisting.ContainsKey(id) || !doc.TryGetProperty("lastMsg", out var last) || !last.TryGetInt64(out var ms))
            {
                skipped++;
                continue;
            }

            var upTo = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O", CultureInfo.InvariantCulture);
            repo.Save(new HandledMark("email", id, upTo, Millis(doc, "at") ?? now, machine));
            email++;
        }

        var slack = 0;
        var slackExisting = repo.LoadHandled("slack");
        foreach (var (rawId, doc) in ReadAll(Path.Combine(exportFolder, "slackHandled")))
        {
            // Marks from before conversations were keyed by thread named only the channel; they
            // covered the channel's messages outside threads.
            var key = rawId.Replace('@', '~');
            key = key.Contains('~', StringComparison.Ordinal) ? key : $"{key}~main";

            // The raw token, not a double: a Slack ts has more digits than a double keeps exactly.
            if (slackExisting.ContainsKey(key) || !doc.TryGetProperty("lastTs", out var ts) || ts.ValueKind != JsonValueKind.Number)
            {
                skipped++;
                continue;
            }

            repo.Save(new HandledMark("slack", key, ts.GetRawText(), Millis(doc, "at") ?? now, machine));
            slack++;
        }

        return new ImportResult(imported, email, slack, skipped);
    }

    private static TodoItem? ToTodo(string id, JsonElement d, string machine, DateTimeOffset now)
    {
        var title = Str(d, "title")?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var due = DateOnly.TryParseExact(Str(d, "due"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd) ? dd : (DateOnly?)null;
        var tags = d.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        var rank = d.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : 0;
        var done = d.TryGetProperty("done", out var dn) && dn.ValueKind == JsonValueKind.True;
        TodoSource? source = null;
        if (d.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object && Str(s, "kind") is { } kind && Str(s, "id") is { } sid)
        {
            source = new TodoSource(kind, sid);
        }

        var created = Millis(d, "createdAt") ?? now;
        return new TodoItem(id, title, Str(d, "notes")?.Trim() ?? "", due, tags, rank, done, done ? Millis(d, "doneAt") : null, created, source, now, machine);
    }

    private static IEnumerable<(string Id, JsonElement Doc)> ReadAll(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            yield return (Path.GetFileNameWithoutExtension(file), doc.RootElement.Clone());
        }
    }

    private static DateTimeOffset? Millis(JsonElement d, string name) =>
        d.TryGetProperty(name, out var v) && v.TryGetInt64(out var ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static string? Str(JsonElement d, string name) =>
        d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
