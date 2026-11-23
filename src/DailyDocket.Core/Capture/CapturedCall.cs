using System.Text;
using System.Text.Json;

namespace DailyDocket.Core.Capture;

/// <summary>One connector call as the capture hook saved it: what was asked and what came back.</summary>
/// <param name="ToolName">The full tool name, e.g. mcp__claude_ai_Gmail__get_thread.</param>
/// <param name="Input">The tool's arguments, as JSON.</param>
/// <param name="ResponseText">The result's text: the JSON string Gmail and Calendar return, or Slack's text blocks joined.</param>
public sealed record CapturedCall(
    string ToolName, JsonElement Input, string ResponseText, string ToolUseId, string? SessionId, DateTimeOffset CapturedAt)
{
    /// <summary>The short tool name after the server prefix, e.g. get_thread.</summary>
    public string Tool => ToolName[(ToolName.LastIndexOf("__", StringComparison.Ordinal) + 2)..];

    /// <summary>The connector: Gmail, Google_Calendar or Slack.</summary>
    public string Server
    {
        get
        {
            var parts = ToolName.Split("__");
            return parts.Length >= 3 ? parts[1].Replace("claude_ai_", "", StringComparison.Ordinal) : "";
        }
    }

    public string? InputString(string name) =>
        Input.ValueKind == JsonValueKind.Object && Input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Reads a capture file written by <c>DailyDocket.Hook capture</c>.</summary>
    public static CapturedCall Parse(string json, DateTimeOffset capturedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var toolName = root.GetProperty("tool_name").GetString() ?? "";
        var input = root.TryGetProperty("tool_input", out var i) ? i.Clone() : default;
        var response = root.TryGetProperty("tool_response", out var r) ? ResponseToText(r) : "";
        return new CapturedCall(
            toolName,
            input,
            response,
            root.TryGetProperty("tool_use_id", out var id) ? id.GetString() ?? "" : "",
            root.TryGetProperty("session_id", out var s) ? s.GetString() : null,
            capturedAt);
    }

    /// <summary>
    /// A connector result arrives as a JSON string (Gmail, Calendar), as MCP content blocks
    /// (Slack), or as an object holding content blocks. Every parser wants the text.
    /// </summary>
    internal static string ResponseToText(JsonElement response)
    {
        switch (response.ValueKind)
        {
            case JsonValueKind.String:
                return response.GetString() ?? "";
            case JsonValueKind.Array:
                return JoinBlocks(response);
            case JsonValueKind.Object when response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                return JoinBlocks(content);
            default:
                return response.GetRawText();
        }
    }

    private static string JoinBlocks(JsonElement blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(t.GetString());
            }
        }

        return sb.ToString();
    }
}
