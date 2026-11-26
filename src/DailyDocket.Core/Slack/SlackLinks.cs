using System.Text.Json;
using System.Text.RegularExpressions;

namespace DailyDocket.Core.Slack;

/// <summary>
/// Links that open a Slack conversation in the Slack app, where Mike replies himself. Sending
/// through the connector adds a "Sent using @Claude" line that neither DailyDocket nor the
/// connector can turn off, so Reply hands the conversation to Slack instead.
/// </summary>
public static partial class SlackLinks
{
    /// <summary>
    /// The <c>slack://</c> link to a conversation: the channel or DM, and the message and thread
    /// when known. Slack documents <c>team</c> and <c>id</c>; <c>message</c> and <c>thread_ts</c>
    /// are what Slack's own links carry, and an app that ignores them still opens the conversation.
    /// Null without the workspace's team id, which only the Slack app's own state on this computer
    /// gives (the connector's results name the workspace by its domain alone).
    /// </summary>
    public static string? AppLink(string? teamId, string channelId, string? messageTs, string? threadTs)
    {
        if (string.IsNullOrEmpty(teamId) || !Id().IsMatch(teamId) || !Id().IsMatch(channelId))
        {
            return null;
        }

        var link = $"slack://channel?team={teamId}&id={channelId}";
        if (messageTs is not null && Ts().IsMatch(messageTs))
        {
            link += $"&message={messageTs}";
        }

        if (threadTs is not null && Ts().IsMatch(threadTs))
        {
            link += $"&thread_ts={threadTs}";
        }

        return link;
    }

    /// <summary>The workspace domain in a permalink (maccosslab in https://maccosslab.slack.com/archives/...), or null.</summary>
    public static string? Domain(string? permalink) =>
        permalink is not null && PermalinkHost().Match(permalink) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>
    /// Team ids by workspace domain, from the Slack desktop app's <c>root-state.json</c>
    /// (<c>workspaces</c>: id, domain). Empty when the file is missing or not in that shape: a
    /// Slack update that changes it sends Reply to the browser rather than failing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TeamsFromSlackState(string? json)
    {
        var teams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return teams;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("workspaces", out var workspaces)
                && workspaces.ValueKind == JsonValueKind.Object)
            {
                foreach (var w in workspaces.EnumerateObject())
                {
                    if (w.Value.ValueKind == JsonValueKind.Object
                        && w.Value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && w.Value.TryGetProperty("domain", out var domain) && domain.ValueKind == JsonValueKind.String
                        && domain.GetString() is { Length: > 0 } d && id.GetString() is { } t && Id().IsMatch(t))
                    {
                        teams[d] = t;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not the shape this reads; the browser link is used instead.
        }

        return teams;
    }

    // Slack ids are upper-case letters and digits; nothing else may reach a link handed to Windows.
    [GeneratedRegex(@"^[A-Z0-9]{2,30}$")]
    private static partial Regex Id();

    [GeneratedRegex(@"^\d{6,12}\.\d{1,8}$")]
    private static partial Regex Ts();

    [GeneratedRegex(@"^https://([A-Za-z0-9-]+)\.slack\.com/", RegexOptions.IgnoreCase)]
    private static partial Regex PermalinkHost();
}
