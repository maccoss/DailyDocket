using System.Text.RegularExpressions;

namespace DailyDocket.Core.Calendar;

/// <summary>
/// Opening a meeting's join link in the meeting app itself rather than a browser page that then
/// asks to open the app.
/// </summary>
/// <remarks>
/// The https link opened the browser, which only offers to start Zoom (and joins in the browser
/// where Zoom is not installed). Zoom's and Teams' own link schemes start the app directly; they are
/// used only where Windows has the scheme registered, and the https link otherwise.
/// </remarks>
public static partial class MeetingLinks
{
    /// <summary>The scheme the meeting's app registers (zoommtg, msteams), or null for a link no app takes.</summary>
    public static string? AppScheme(string joinLink) => AppLink(joinLink) is { } app ? app[..app.IndexOf(':', StringComparison.Ordinal)] : null;

    /// <summary>
    /// The link that starts the meeting's app: Zoom's zoommtg://host/join?action=join&amp;confno=..&amp;pwd=..
    /// for a /j/ link, Teams' msteams: plus the link's path for a meeting link; null otherwise
    /// (Meet and Webex open in the browser, which is how they join).
    /// </summary>
    public static string? AppLink(string joinLink)
    {
        if (!Uri.TryCreate(joinLink, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (IsHost(host, "zoom.us") || IsHost(host, "zoom.com") || IsHost(host, "zoomgov.com"))
        {
            var m = ZoomMeeting().Match(uri.AbsolutePath);
            if (!m.Success)
            {
                return null;
            }

            var link = $"zoommtg://{host}/join?action=join&confno={m.Groups[1].Value}";
            var pwd = Query(uri, "pwd");
            return pwd is null ? link : link + "&pwd=" + Uri.EscapeDataString(pwd);
        }

        if ((host == "teams.microsoft.com" || host == "teams.live.com")
            && (uri.AbsolutePath.StartsWith("/l/meetup-join/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/meet/", StringComparison.OrdinalIgnoreCase)))
        {
            return "msteams:" + uri.PathAndQuery;
        }

        return null;
    }

    /// <summary>
    /// A join link to show: host and path, without the query (Zoom's pwd stays out of sight); a
    /// Teams link, whose path is a long encoded id, as "Microsoft Teams meeting".
    /// </summary>
    public static string Short(string joinLink)
    {
        if (!Uri.TryCreate(joinLink, UriKind.Absolute, out var uri))
        {
            return joinLink;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return host is "teams.microsoft.com" or "teams.live.com" ? "Microsoft Teams meeting" : uri.Host + uri.AbsolutePath.TrimEnd('/');
    }

    private static bool IsHost(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static string? Query(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && string.Equals(part[..eq], name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return null;
    }

    [GeneratedRegex(@"^/(?:j|w|s)/(\d{9,12})/?$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoomMeeting();
}
