using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Mail;

namespace DailyDocket.Core.Outbox;

/// <summary>The one connector call an item may make, exactly.</summary>
public sealed record SendCall(string ToolName, JsonObject Input);

/// <summary>Recipients for a reply, and the exact call each item makes.</summary>
public static partial class OutboxRules
{
    /// <summary>
    /// Reply goes to whoever he is answering; Reply all adds everyone else on the newest message,
    /// never himself. Each list can then be edited in the compose box.
    /// </summary>
    public static (IReadOnlyList<string> To, IReadOnlyList<string> Cc) Recipients(IReadOnlyList<MailMessage> thread, bool replyAll, DocketConfig config)
    {
        var msgs = thread.OrderBy(m => m.Date ?? DateTimeOffset.MinValue).ToList();
        if (msgs.Count == 0)
        {
            return ([], []);
        }

        var last = msgs[^1];
        var answering = msgs.LastOrDefault(m => !config.Owner.IsMe(m.Sender)) ?? last;

        // When his own message is newest (a follow-up), reply to the people he wrote to.
        var to = config.Owner.IsMe(answering.Sender)
            ? answering.To.Select(Address).ToList()
            : [Address(answering.Sender)];

        var cc = new List<string>();
        if (replyAll)
        {
            foreach (var a in last.To.Concat([Address(last.Sender)]).Select(Address))
            {
                if (!to.Contains(a, StringComparer.OrdinalIgnoreCase))
                {
                    to.Add(a);
                }
            }

            cc.AddRange(last.Cc.Select(Address).Where(a => !to.Contains(a, StringComparer.OrdinalIgnoreCase)));
        }

        bool NotMe(string a) => a.Length > 0 && !config.Owner.IsMe(a);
        return ([.. to.Where(NotMe).Distinct(StringComparer.OrdinalIgnoreCase)], [.. cc.Where(NotMe).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The plain address from "Pat Example &lt;pat@example.org&gt;", as the connector requires.</summary>
    public static string Address(string text)
    {
        var m = AngleAddress().Match(text);
        return (m.Success ? m.Groups[1].Value : text).Trim().Trim('"', '\'').ToLowerInvariant();
    }

    /// <summary>A plausible single address: what the compose box accepts when he types one.</summary>
    public static bool IsAddress(string text) => SimpleAddress().IsMatch(text.Trim());

    /// <summary>
    /// The exact call for <paramref name="item"/>. The send guard allows this call and nothing else,
    /// once; the Send run is told to make it as written.
    /// </summary>
    public static SendCall Call(OutboxItem item, bool includeSignature) => item.Kind switch
    {
        OutboxKind.EmailReply => new SendCall(
            ConnectorTools.Gmail + "reply",
            new JsonObject
            {
                ["messageId"] = item.ReplyTo ?? throw new InvalidOperationException("An email reply needs the message it answers."),
                ["to"] = new JsonArray([.. item.To.Select(a => (JsonNode)a)]),
                ["cc"] = new JsonArray([.. item.Cc.Select(a => (JsonNode)a)]),
                ["body"] = item.Text,
                ["htmlBody"] = item.Html,
                ["includeSignature"] = includeSignature,
            }),
        OutboxKind.SlackReply => new SendCall(
            ConnectorTools.Slack + "slack_send_message",
            item.ReplyTo is { } threadTs
                ? new JsonObject { ["channel_id"] = item.Channel, ["message"] = item.Text, ["thread_ts"] = threadTs }
                : new JsonObject { ["channel_id"] = item.Channel, ["message"] = item.Text }),
        _ => throw new InvalidOperationException($"Unknown outbox kind {item.Kind}."),
    };

    /// <summary>The claims file the send guard reads: this item's one allowed call.</summary>
    public static string ClaimsJson(OutboxItem item, SendCall call) =>
        new JsonArray(new JsonObject { ["id"] = item.Id, ["tool_name"] = call.ToolName, ["tool_input"] = call.Input.DeepClone() }).ToJsonString();

    /// <summary>
    /// Email: whether the thread's newest message (from a fresh read) is newer than the one he was
    /// answering. Any newer message holds the item, his own from his phone included.
    /// </summary>
    public static bool EmailMovedOn(IReadOnlyList<MailMessage> fresh, string baseMessageId) =>
        fresh.Count > 0 && fresh.OrderBy(m => m.Date ?? DateTimeOffset.MinValue).Last().Id != baseMessageId;

    /// <summary>
    /// Slack: whether a fresh read of the conversation shows any message later than
    /// <paramref name="baseTs"/>. The read's format is text, so every ts in it is checked; this
    /// errs toward holding, which only costs Mike a look.
    /// </summary>
    public static bool SlackMovedOn(string freshResponse, string baseTs)
    {
        if (!decimal.TryParse(baseTs, NumberStyles.Number, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        return SlackTs().Matches(freshResponse)
            .Select(m => decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Any(v => v > b);
    }

    /// <summary>The sent message's id from the connector's answer: Gmail's message id, or Slack's ts or link.</summary>
    public static string? RemoteId(string response)
    {
        var text = response.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    return id.GetString();
                }

                if (doc.RootElement.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String)
                {
                    return ts.GetString();
                }
            }
            catch (JsonException)
            {
                // Fall through to the text forms.
            }
        }

        if (SlackLink().Match(text) is { Success: true } link)
        {
            return link.Value;
        }

        return SlackTs().Match(text) is { Success: true } t ? t.Value : null;
    }

    [GeneratedRegex(@"<([^<>@\s]+@[^<>\s]+)>")]
    private static partial Regex AngleAddress();

    [GeneratedRegex(@"^[^@\s,;<>]+@[^@\s,;<>]+\.[^@\s,;<>]+$")]
    private static partial Regex SimpleAddress();

    [GeneratedRegex(@"\b\d{10}\.\d{6}\b")]
    private static partial Regex SlackTs();

    [GeneratedRegex(@"https://[A-Za-z0-9.-]+\.slack\.com/archives/[A-Z0-9]+/p\d+\S*")]
    private static partial Regex SlackLink();
}
