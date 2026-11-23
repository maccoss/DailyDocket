using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Prep;
using ModelContextProtocol.Server;

namespace DailyDocket.Core.Claude;

/// <summary>A tool Claude wants to use that is not on the run's allowlist.</summary>
public sealed record PermissionRequest(string ToolName, JsonElement Input, string Description);

/// <summary>The answer to a <see cref="PermissionRequest"/>.</summary>
public sealed record PermissionDecision(bool Allow, string? Message = null);

/// <summary>Answers permission prompts for a run that has someone watching (a drafting chat).</summary>
public interface IPermissionHandler
{
    Task<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken cancellationToken);
}

/// <summary>What the docket tools act on during one run.</summary>
public interface IRunContext
{
    /// <summary>Ingests the run's captures so far, then lists the threads that need reading.</summary>
    IReadOnlyList<string> ThreadsToRead();

    IReadOnlyList<TriageItem> TriageBatch();

    /// <summary>Instructions per source ("mail", "slack") for judging triage items.</summary>
    IReadOnlyDictionary<string, string> TriageInstructions { get; }

    void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why);

    /// <summary>The meetings this run may write briefs for: a prep run's, empty for every other run.</summary>
    IReadOnlyList<PrepTarget> PrepTargets => [];

    /// <summary>Saves a brief for one of <see cref="PrepTargets"/>; returns what to tell the model.</summary>
    string SaveBrief(PrepTarget target, MeetingBrief brief) => throw new InvalidOperationException("This run does not write briefs.");
}

/// <summary>One attendee as write_brief takes it.</summary>
public sealed record BriefAttendeeArg(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("org")] string? Org = null,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("organizer")] bool Organizer = false);

/// <summary>
/// The tools the app gives Claude, served over MCP by <see cref="AppToolServer"/> (LabOps' pattern),
/// one server per run.
/// </summary>
/// <remarks>
/// <c>approve</c> answers Claude Code's permission prompts. The docket tools let a run ask the app
/// what to fetch and record what it judged; a run gets only the ones on its allowlist.
/// </remarks>
[McpServerToolType]
public sealed class AppTools
{
    public const string ServerName = "docket";

    /// <summary>The name Claude Code knows the permission tool by.</summary>
    public const string PermissionToolName = $"mcp__{ServerName}__approve";

    public const string ThreadsToReadToolName = $"mcp__{ServerName}__threads_to_read";

    public const string TriageBatchToolName = $"mcp__{ServerName}__triage_batch";

    public const string RecordVerdictsToolName = $"mcp__{ServerName}__record_verdicts";

    public const string WriteBriefToolName = $"mcp__{ServerName}__write_brief";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// The items this run was given to judge, with the message each verdict must be kept against.
    /// A verdict for anything else is refused: text inside one email must not be able to make the
    /// run clear other threads off the Email tab.
    /// </summary>
    private readonly Dictionary<(string Source, string Key), string> _issued = [];

    /// <summary>
    /// Who answers permission prompts. Null for unattended runs (pulls, sends, the nightly jobs),
    /// where everything outside the allowlist is denied: a scheduled run must never widen itself.
    /// </summary>
    public IPermissionHandler? Handler { get; set; }

    /// <summary>The run the docket tools act on.</summary>
    public IRunContext? Run { get; set; }

    /// <summary>
    /// Answers Claude Code's permission prompts. Claude Code calls this itself, with these exact
    /// parameter names, whenever a tool is not pre-approved.
    /// </summary>
    [McpServerTool(Name = "approve", Title = "Permission prompt")]
    [Description("Decides whether a tool call may run. Called by Claude Code, not by the model.")]
    public async Task<string> Approve(
        [Description("The tool that wants to run.")] string tool_name,
        [Description("The tool's input.")] JsonElement input,
        [Description("The tool use id.")] string? tool_use_id = null,
        CancellationToken cancellationToken = default)
    {
        _ = tool_use_id;
        var decision = Handler is null
            ? new PermissionDecision(false, "This run is unattended; only its pre-approved tools may run.")
            : await Handler.RequestPermissionAsync(
                new PermissionRequest(tool_name, input.Clone(), ToolDescriptions.Describe(tool_name, input)),
                cancellationToken).ConfigureAwait(false);

        return decision.Allow
            ? JsonSerializer.Serialize(new { behavior = "allow", updatedInput = input })
            : JsonSerializer.Serialize(new { behavior = "deny", message = decision.Message ?? "Mike declined this step." });
    }

    [McpServerTool(Name = "threads_to_read", Title = "Threads to read")]
    [Description("Lists the Gmail thread ids the app needs read with get_thread: new threads, and threads with a message newer than the app has.")]
    public string ThreadsToRead()
    {
        var ids = RequireRun().ThreadsToRead();
        return JsonSerializer.Serialize(new { threadIds = ids, note = ids.Count == 0 ? "Nothing to read." : null }, Json);
    }

    [McpServerTool(Name = "triage_batch", Title = "Next triage batch")]
    [Description("Returns the next conversations to judge, each with a transcript, and the instructions for judging each source.")]
    public string TriageBatch()
    {
        var run = RequireRun();
        var items = run.TriageBatch();
        lock (_issued)
        {
            foreach (var i in items)
            {
                _issued[(i.Source, i.Key)] = i.ForMessage;
            }
        }

        return JsonSerializer.Serialize(
            new
            {
                instructions = run.TriageInstructions,
                items = items.Select(i => new { source = i.Source, key = i.Key, for_message = i.ForMessage, transcript = i.Transcript }),
            },
            Json);
    }

    [McpServerTool(Name = "record_verdicts", Title = "Record verdicts")]
    [Description("Records a verdict for each judged item: [{\"source\", \"key\", \"for_message\", \"needs_reply\", \"why\"}].")]
    public string RecordVerdicts([Description("The verdicts, one per item, with source, key and for_message exactly as given.")] JsonElement verdicts)
    {
        var run = RequireRun();
        var saved = 0;
        var problems = new List<string>();

        // Some models send the array as a JSON string; accept both.
        var list = verdicts.ValueKind == JsonValueKind.String ? JsonDocument.Parse(verdicts.GetString()!).RootElement : verdicts;
        if (list.ValueKind != JsonValueKind.Array)
        {
            return "verdicts must be an array.";
        }

        foreach (var v in list.EnumerateArray())
        {
            var source = Str(v, "source");
            var key = Str(v, "key");
            var forMessage = Str(v, "for_message");
            if (source is not ("mail" or "slack") || key is null || forMessage is null ||
                !v.TryGetProperty("needs_reply", out var nr) || nr.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problems.Add(key ?? "(no key)");
                continue;
            }

            bool issued;
            lock (_issued)
            {
                issued = _issued.TryGetValue((source, key), out var expected) && expected == forMessage;
            }

            if (!issued)
            {
                problems.Add(key);
                continue;
            }

            run.RecordVerdict(source, key, forMessage, nr.GetBoolean(), Str(v, "why") ?? "");
            saved++;
        }

        return problems.Count == 0 ? $"Saved {saved}." : $"Saved {saved}. Not saved (not in a batch you were given, or missing source, key, for_message or needs_reply): {string.Join(", ", problems)}.";
    }

    /// <summary>
    /// Saves a meeting brief. The arguments are typed and every one is checked here: the event must
    /// be one this run was asked to prepare (text in an email cannot point the run at another
    /// meeting), and each item is cleaned and capped before it reaches git.
    /// </summary>
    [McpServerTool(Name = "write_brief", Title = "Write meeting brief")]
    [Description("Saves the brief for the meeting you were asked to prepare. Every item is one short sentence in your own words for Mike, never copied text.")]
    public string WriteBrief(
        [Description("The event id exactly as given.")] string event_id,
        [Description("Everyone invited except Mike: name, organization, their role or relationship to Mike, and organizer true for whoever organized it.")] BriefAttendeeArg[] attendees,
        [Description("What the meeting is for and will cover: 1 to 5 items.")] string[] agenda,
        [Description("Background: the email threads, documents or events that led to it, summarized: up to 6 items.")] string[] context,
        [Description("What Mike owes these people or promised them; empty when nothing.")] string[] owes,
        [Description("What you could not find out, and questions or decisions Mike should settle: up to 3 items; empty when nothing.")] string[] gaps,
        [Description("Gmail thread ids and https document links you drew on.")] string[]? sources = null)
    {
        var run = RequireRun();
        var target = run.PrepTargets.FirstOrDefault(t => t.Event.EventId == event_id);
        if (target is null)
        {
            return $"Not saved: {event_id} is not the meeting this run was asked to prepare.";
        }

        var problems = new List<string>();
        var people = (attendees ?? []).Where(a => !string.IsNullOrWhiteSpace(a?.Name)).ToList();
        if (people.Count == 0)
        {
            problems.Add("attendees must list who is coming, each with a name");
        }

        if (BriefRules.Items(agenda).Count == 0)
        {
            problems.Add("agenda needs at least one item");
        }

        if (problems.Count > 0)
        {
            return $"Not saved: {string.Join("; ", problems)}. Call write_brief again with these fixed.";
        }

        var brief = new MeetingBrief(
            target.Event.EventId,
            target.Event.Title,
            target.Event.Start,
            target.Event.Updated,
            [.. people.Select(a => new BriefAttendee(a.Name, a.Org ?? "", a.Role ?? "", a.Organizer))],
            agenda,
            context ?? [],
            owes ?? [],
            gaps ?? [],
            sources ?? [],
            DateTimeOffset.UtcNow,
            "",
            "");
        return run.SaveBrief(target, brief);
    }

    private IRunContext RequireRun() =>
        Run ?? throw new InvalidOperationException("This run has no docket context.");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
