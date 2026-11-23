using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DailyDocket.Hook;

/// <summary>
/// PreToolUse on every send tool: a call goes through only if it is, exactly, one the app claimed.
/// </summary>
/// <remarks>
/// <para>The claims file is a JSON array written by the app for one Send run:
/// <c>[{"id": "&lt;outbox id&gt;", "tool_name": "...", "tool_input": {...}}]</c>. A call matches
/// when its tool name is the same and its input, with object keys in a fixed order, is the same
/// JSON: every recipient, thread id and byte of the body. Each claim is used once; a second
/// identical call is denied, so Claude cannot send a message twice.</para>
/// <para>Comparing the whole input rather than chosen fields means a field we did not think of
/// (a bcc, an attachment) cannot slip through.</para>
/// </remarks>
internal static class SendGuard
{
    /// <summary>
    /// The hook's stdout: an allow for a claimed call, else a deny. The send tool is deliberately not
    /// on the run's allowlist, so only this allow lets it run: if the guard crashes, times out or
    /// cannot start (which Claude Code treats as non-blocking), permission mode dontAsk refuses the
    /// call instead of letting it through unchecked.
    /// </summary>
    public static string Decide(string input, string claimsPath)
    {
        var call = JsonNode.Parse(input) ?? throw new JsonException("The hook input is empty.");
        var toolName = call["tool_name"]?.GetValue<string>() ?? throw new JsonException("The hook input has no tool_name.");
        var toolInput = Canonical(call["tool_input"]);

        if (!File.Exists(claimsPath))
        {
            return Deny($"no outbox item is claimed in this run, so {toolName} is not allowed.");
        }

        var claims = JsonNode.Parse(File.ReadAllText(claimsPath)) as JsonArray
            ?? throw new JsonException("The claims file is not a JSON array.");

        foreach (var claim in claims.OfType<JsonObject>())
        {
            var id = claim["id"]?.GetValue<string>();
            if (id is null
                || !string.Equals(claim["tool_name"]?.GetValue<string>(), toolName, StringComparison.Ordinal)
                || !string.Equals(Canonical(claim["tool_input"]), toolInput, StringComparison.Ordinal))
            {
                continue;
            }

            // Claude Code runs the hooks of parallel tool calls at the same time, so reading a list
            // of used ids and appending later would let two identical calls both pass. Creating the
            // marker file is the claim: the file system lets exactly one CreateNew succeed.
            try
            {
                using (new FileStream(UsedMarker(claimsPath, id), FileMode.CreateNew, FileAccess.Write))
                {
                }
            }
            catch (IOException) when (File.Exists(UsedMarker(claimsPath, id)))
            {
                return Deny($"outbox item {id} has already been sent in this run.");
            }

            return Allow();
        }

        return Deny($"this {toolName} call does not match any claimed outbox item exactly "
            + $"(input hash {Hash(toolInput)}). Send only the claimed items, unchanged.");
    }

    /// <summary>The JSON with object keys sorted, compact, so key order cannot cause a mismatch.</summary>
    internal static string Canonical(JsonNode? node) => Sorted(node)?.ToJsonString() ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    /// <summary>
    /// The marker that an outbox item's call was let through. The id is hashed so whatever it holds
    /// cannot make a path; the app looks for any <c>claims.json.used.*</c> to know a call went ahead.
    /// </summary>
    internal static string UsedMarker(string claimsPath, string id) => $"{claimsPath}.used.{Hash(id)}";

    private static string Allow() => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "allow",
            ["permissionDecisionReason"] = "DailyDocket send guard: matches a claimed outbox item.",
        },
    }.ToJsonString();

    private static string Deny(string reason) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = $"DailyDocket send guard: {reason}",
        },
    }.ToJsonString();

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}
