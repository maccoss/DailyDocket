using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DailyDocket.Hook;

/// <summary>PostToolUse: keep the raw result, optionally hand Claude a receipt instead.</summary>
internal static partial class Capture
{
    // Keys whose values the model needs to keep paging or to ask for one item.
    private static readonly HashSet<string> IdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "threadId", "messageId", "eventId", "ts", "thread_ts", "channel", "channel_id",
    };

    private static readonly HashSet<string> PagingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "nextPageToken", "next_page_token", "next_cursor", "nextCursor", "cursor", "resultSizeEstimate",
    };

    private const int MaxIds = 60;

    /// <summary>Where Claude Code keeps the results it was too big to pass on: under ~/.claude.</summary>
    internal static readonly string ClaudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>Saves <paramref name="input"/> and returns the hook's stdout, or null for none.</summary>
    public static string? Run(string input, string directory, bool receipt, DateTimeOffset now) =>
        Run(input, directory, receipt, now, ClaudeHome);

    internal static string? Run(string input, string directory, bool receipt, DateTimeOffset now, string claudeHome)
    {
        var node = JsonNode.Parse(input) ?? throw new JsonException("The hook input is empty.");
        var toolUseId = node["tool_use_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(directory);
        var name = $"{now:yyyyMMdd'T'HHmmssfff}-{Sanitize(toolUseId)}.json";
        var path = Path.Combine(directory, name);
        var temp = path + ".tmp";

        // Verbatim: the parsers are tested against exactly what the connector returned. The one
        // exception is a result Claude Code found too big to pass on: it hands the hook a note
        // naming the file it saved the result in, and the result is put back from that file.
        if (SavedResult(node["tool_response"], claudeHome) is { } saved)
        {
            node["tool_response"] = saved;
            input = node.ToJsonString();
        }

        File.WriteAllText(temp, input, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);

        if (!receipt)
        {
            return null;
        }

        var response = node["tool_response"];
        var text = Receipt(node["tool_name"]?.GetValue<string>() ?? "?", response, input.Length);
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["updatedToolOutput"] = Shaped(response, text),
            },
        };
        return output.ToJsonString();
    }

    /// <summary>
    /// The full result, when <paramref name="response"/> is Claude Code's note that a result
    /// "exceeds maximum allowed tokens" and was saved to a file; otherwise null.
    /// </summary>
    /// <remarks>
    /// Without this a long email thread was saved as the note itself, never counted as read, and
    /// asked for again on every pull: 39 such threads took all of each pull's reads and about 600
    /// others waited behind them (November 2026). The runs raise the limit
    /// (JobRunner.Environment), so this is the fallback for a thread larger still. Only a file in a
    /// tool-results folder under ~/.claude is read, so a connector result that merely imitates the
    /// note cannot have the hook copy some other file into the docket.
    /// </remarks>
    internal static string? SavedResult(JsonNode? response, string claudeHome)
    {
        if (response is not JsonValue value || !value.TryGetValue<string>(out var text)
            || SavedNote().Match(text) is not { Success: true } match)
        {
            return null;
        }

        string file;
        try
        {
            file = Path.GetFullPath(match.Groups[1].Value);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(claudeHome)) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "tool-results", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(file))
        {
            return null;
        }

        return File.ReadAllText(file, Encoding.UTF8);
    }

    /// <summary>The receipt text: what was captured, and the ids and paging tokens in it.</summary>
    internal static string Receipt(string toolName, JsonNode? response, int length)
    {
        var ids = new List<string>();
        var paging = new Dictionary<string, (string Value, int Depth)>(StringComparer.Ordinal);
        Walk(response, ids, paging, depth: 0);

        var sb = new StringBuilder();
        sb.Append($"DailyDocket saved this {toolName} result ({length:N0} characters) for the app; its text is not shown to you.");
        if (paging.Count > 0)
        {
            sb.Append(" Paging: ").Append(string.Join(", ", paging.Select(p => $"{p.Key}={p.Value.Value}"))).Append('.');
        }

        if (ids.Count > 0)
        {
            sb.Append($" Ids ({ids.Count}{(ids.Count >= MaxIds ? "+" : "")}): ").Append(string.Join(", ", ids)).Append('.');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The receipt in the same outer shape as the original result. Claude Code does not validate an
    /// MCP tool's replacement output, but a familiar shape (content blocks, or a string) keeps the
    /// model from treating the receipt as a malformed answer.
    /// </summary>
    internal static JsonNode Shaped(JsonNode? original, string text)
    {
        JsonObject Block() => new() { ["type"] = "text", ["text"] = text };
        return original switch
        {
            JsonArray => new JsonArray(Block()),
            JsonObject o when o["content"] is JsonArray => new JsonObject { ["content"] = new JsonArray(Block()) },
            _ => JsonValue.Create(text),
        };
    }

    /// <summary>
    /// The receipt is the one place a reading run sees text that came from outside, so only values
    /// shaped like tokens get into it: a subject or snippet that happens to be JSON (which the walk
    /// below parses, since MCP text blocks carry JSON as strings) cannot put sentences in front of
    /// the model. Where the same paging key appears twice, the shallower one wins, so a key planted
    /// inside a message cannot replace the connector's own page token.
    /// </summary>
    private static void SetPaging(Dictionary<string, (string Value, int Depth)> paging, string key, string value, int depth)
    {
        if (Token().IsMatch(value) && (!paging.TryGetValue(key, out var existing) || depth < existing.Depth))
        {
            paging[key] = (value, depth);
        }
    }

    private static void Walk(JsonNode? node, List<string> ids, Dictionary<string, (string Value, int Depth)> paging, int depth)
    {
        if (node is null || depth > 12)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    {
                        // Slack's cursor is prose, not a field: "use cursor `<token>`".
                        if (key == "pagination_info" && SlackCursor().Match(s) is { Success: true } cursor)
                        {
                            SetPaging(paging, "cursor", cursor.Groups[1].Value, depth);
                            continue;
                        }

                        if (PagingKeys.Contains(key))
                        {
                            SetPaging(paging, key, s, depth);
                            continue;
                        }

                        if (IdKeys.Contains(key) && ids.Count < MaxIds && !ids.Contains(s) && s.Length <= 64 && Token().IsMatch(s))
                        {
                            ids.Add(s);
                            continue;
                        }
                    }
                    else if (value is JsonValue n && PagingKeys.Contains(key))
                    {
                        SetPaging(paging, key, n.ToJsonString(), depth);
                        continue;
                    }

                    Walk(value, ids, paging, depth + 1);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, ids, paging, depth + 1);
                }

                break;

            case JsonValue value when value.TryGetValue<string>(out var str):
                // MCP text blocks often carry JSON as a string; look inside it too.
                var trimmed = str.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    try
                    {
                        Walk(JsonNode.Parse(str), ids, paging, depth + 1);
                    }
                    catch (JsonException)
                    {
                        // Plain text that happens to start with a brace.
                    }
                }
                else
                {
                    SlackIds(str, ids);
                }

                break;
        }
    }

    /// <summary>
    /// Slack answers with a labeled text report, not JSON, so its ids are read from the labels:
    /// each result becomes channel:ts (plus :thread_ts when it is in a thread), which is what a
    /// run needs to read that thread or its reactions next.
    /// </summary>
    internal static void SlackIds(string text, List<string> ids)
    {
        foreach (var block in SlackResult().Split(text).Skip(1))
        {
            var channel = SlackChannel().Match(block);
            var ts = SlackTs().Match(block);
            if (!channel.Success || !ts.Success || ids.Count >= MaxIds)
            {
                continue;
            }

            var thread = SlackThread().Match(block);
            var id = $"{channel.Groups[1].Value}:{ts.Groups[1].Value}" + (thread.Success ? $":{thread.Groups[1].Value}" : "");
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }
    }

    /// <summary>A page token or id: base64url and the punctuation ids use, no spaces, not too long.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_\-.:=+/@]{1,200}$")]
    private static partial System.Text.RegularExpressions.Regex Token();

    /// <summary>Claude Code's note for a result it would not pass on; the path ends at the first ".\n".</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^Error: result \([\d,]+ characters\) exceeds maximum allowed tokens\. Output has been saved to (.+?)\.\r?\n")]
    private static partial System.Text.RegularExpressions.Regex SavedNote();

    [System.Text.RegularExpressions.GeneratedRegex(@"cursor[^`]*`([^`]+)`")]
    private static partial System.Text.RegularExpressions.Regex SlackCursor();

    [System.Text.RegularExpressions.GeneratedRegex(@"###\s+Result\s+\d+\s+of\s+\d+")]
    private static partial System.Text.RegularExpressions.Regex SlackResult();

    [System.Text.RegularExpressions.GeneratedRegex(@"Channel:[^\n]*\(ID:\s*([A-Z0-9]+)\)")]
    private static partial System.Text.RegularExpressions.Regex SlackChannel();

    [System.Text.RegularExpressions.GeneratedRegex(@"Message_ts:\s*([\d.]+)")]
    private static partial System.Text.RegularExpressions.Regex SlackTs();

    [System.Text.RegularExpressions.GeneratedRegex(@"thread_ts=([\d.]+)")]
    private static partial System.Text.RegularExpressions.Regex SlackThread();

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars, 0, Math.Min(chars.Length, 80));
    }
}
