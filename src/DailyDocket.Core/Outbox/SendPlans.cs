using System.Text.Json;
using System.Text.Json.Nodes;
using DailyDocket.Core.Jobs;

namespace DailyDocket.Core.Outbox;

/// <summary>
/// The two runs behind every send. First a reader fetches the one conversation fresh, so the app
/// can hold the item if it moved on; then a sender, which sees only the outbox item, makes the one
/// exact call the guard allows.
/// </summary>
/// <remarks>
/// Two runs, not one, because readers cannot send and senders cannot read (design, Security): a
/// message written to steer Claude would have to reach a run that can send, and none does.
/// </remarks>
public static class SendPlans
{
    public const string PreCheckJob = "send-check";
    public const string SendJob = "send";

    private static readonly JsonSerializerOptions Literal = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The tool and arguments of the fresh read for <paramref name="item"/>.</summary>
    public static (string Tool, JsonObject Input) PreCheckCall(OutboxItem item) => item.Kind switch
    {
        OutboxKind.EmailReply => (ConnectorTools.GetThread, new JsonObject { ["threadId"] = item.Target, ["messageFormat"] = "METADATA_ONLY" }),
        OutboxKind.SlackReply when item.ReplyTo is { } thread =>
            (ConnectorTools.SlackReadThread, new JsonObject { ["channel_id"] = item.Channel, ["message_ts"] = thread, ["oldest"] = item.Base, ["limit"] = 50 }),
        _ => (ConnectorTools.Slack + "slack_read_channel", new JsonObject { ["channel_id"] = item.Channel, ["oldest"] = item.Base, ["limit"] = 20 }),
    };

    public static JobPlan PreCheck(OutboxItem item, string model)
    {
        var (tool, input) = PreCheckCall(item);
        var prompt =
            $"""
            You are running DailyDocket's pre-send check. The app saves the result itself; you see only a receipt.
            First call ToolSearch once with the query "select:{tool}". Then call {tool} {input.ToJsonString(Literal)} exactly once, and reply with the single word DONE.
            """;
        return new JobPlan(PreCheckJob, model, prompt, ["ToolSearch", tool], ConnectorTools.ReaderDisallowed, true, 6, new Dictionary<string, string>());
    }

    public static JobPlan Send(OutboxItem item, SendCall call, string model)
    {
        var prompt =
            $"""
            You are running DailyDocket's Send job. Mike asked for exactly this action in the app (a message he wrote and sent, a calendar response, or marking a thread read); your only task is to carry it out exactly.
            First call ToolSearch once with the query "select:{call.ToolName}".
            Then call {call.ToolName} exactly once, with exactly these arguments, copied character for character (do not reword, reformat, add or remove anything):
            {call.Input.ToJsonString(Literal)}
            If the call is refused or fails, do not try again with different arguments: reply with the refusal or error text word for word. Otherwise reply with the single word SENT.
            """;

        // Only the one send tool: every other write is disallowed, and so is every read, so
        // nothing the run could see can steer it.
        var disallowed = ConnectorTools.Writes.Where(w => w != call.ToolName)
            .Concat(ConnectorTools.Reads)
            .Concat(ConnectorTools.ReaderDisallowedBuiltIns)
            .ToList();
        // The send tool itself is not on the allowlist: only the guard's explicit allow lets it run.
        // A guard that crashes, times out or cannot start is non-blocking to Claude Code, so with
        // the tool allowlisted the call would go through unchecked; without it, dontAsk refuses.
        // (Checked live: a PreToolUse allow does run a tool that is off the allowlist under dontAsk.)
        return new JobPlan(SendJob, model, prompt, ["ToolSearch"], disallowed, true, 6, new Dictionary<string, string>())
        {
            Claims = OutboxRules.ClaimsJson(item, call),
            Receipt = false,
        };
    }
}
