using System.Text.Json;

namespace DailyDocket.Core.Claude;

/// <summary>Something Claude Code reported on its stream-json output.</summary>
public abstract record ClaudeEvent;

/// <summary>The session started; <see cref="SessionId"/> is what <c>--resume</c> takes.</summary>
public sealed record SessionStarted(string SessionId, string? Model, IReadOnlyList<McpServerState> McpServers) : ClaudeEvent;

/// <summary>An MCP server and whether Claude Code connected to it.</summary>
public sealed record McpServerState(string Name, string Status)
{
    public bool Connected => string.Equals(Status, "connected", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Text Claude wrote for the user.</summary>
public sealed record AssistantText(string Text) : ClaudeEvent;

/// <summary>Claude started using a tool.</summary>
public sealed record ToolStarted(string Id, string Name, JsonElement Input) : ClaudeEvent
{
    /// <summary>A one-line, plain-language description for the activity list.</summary>
    public string Describe() => ToolDescriptions.Describe(Name, Input);
}

/// <summary>A tool finished.</summary>
public sealed record ToolFinished(string ToolUseId, bool IsError, string? Content) : ClaudeEvent;

/// <summary>Claude finished responding to the last message.</summary>
public sealed record TurnFinished(
    bool IsError,
    string? Result,
    string? SessionId,
    decimal? CostUsd,
    int Turns,
    IReadOnlyList<string> PermissionDenials) : ClaudeEvent;

/// <summary>The Claude process ended.</summary>
public sealed record SessionEnded(int ExitCode, string? ErrorOutput) : ClaudeEvent;

/// <summary>Turns tool calls into words a non-expert can follow.</summary>
public static class ToolDescriptions
{
    public static string Describe(string name, JsonElement input)
    {
        string? Field(string key) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        string File(string? path) => path is null ? "a file" : ShortPath(path);

        return name switch
        {
            "Bash" or "PowerShell" => DescribeCommand(Field("command")),
            "Read" => $"Reading {File(Field("file_path"))}",
            "Edit" or "MultiEdit" => $"Editing {File(Field("file_path"))}",
            "Write" => $"Writing {File(Field("file_path"))}",
            "Glob" or "Grep" => "Searching the files",
            "Skill" => Field("skill") is { } skill ? $"Following the {skill} procedure" : "Following a procedure",
            "TodoWrite" => "Planning the steps",
            _ when name.StartsWith("mcp__docket__", StringComparison.Ordinal) => "",
            _ when name.Contains("gmail", StringComparison.OrdinalIgnoreCase) => "Reading mail in Gmail",
            _ when name.Contains("calendar", StringComparison.OrdinalIgnoreCase) => "Reading the calendar",
            _ when name.Contains("slack", StringComparison.OrdinalIgnoreCase) => "Reading Slack",
            _ => $"Using {name}",
        };
    }

    private static string DescribeCommand(string? command) =>
        command is null ? "Running a command"
        : command.Length > 80 ? $"Running: {command[..77]}..."
        : $"Running: {command}";

    /// <summary>The file name alone, for the activity list.</summary>
    internal static string ShortPath(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
}
