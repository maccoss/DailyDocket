using System.Globalization;
using System.Text;
using System.Text.Json;
using DailyDocket.Core.Todos;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DailyDocket.Core.Verdicts;

/// <summary>
/// Claude's needs-reply verdict as the data repository shares it:
/// <c>verdicts/email/&lt;threadId&gt;.yaml</c> or <c>verdicts/slack/&lt;channel&gt;~&lt;threadTs&gt;.yaml</c>.
/// </summary>
/// <remarks>
/// Shared so a conversation judged on one machine is not judged again (and paid for again) on the
/// others. Like the cache's verdict, it is kept against the newest message it read.
/// </remarks>
/// <param name="Source">mail or slack, as in the cache.</param>
/// <param name="ForMessage">The newest message the verdict read: a Gmail message id, or a Slack ts.</param>
/// <param name="MessageAt">That message's time, for mail (a Slack ts is its own time); null when unknown.</param>
/// <param name="Why">Claude's reason, capped at 70 characters as in the artifact.</param>
public sealed record SharedVerdict(string Source, string Key, string ForMessage, DateTimeOffset? MessageAt, bool NeedsReply, string Why, DateTimeOffset At, string Machine);

/// <summary>The merge rule, as a pure function.</summary>
public static class VerdictRules
{
    public const int MaxWhy = 70;

    /// <summary>
    /// Whether <paramref name="incoming"/> should replace <paramref name="existing"/>: never when it
    /// was judged against an older message (a machine with stale mail must not undo a newer verdict),
    /// and not for the same message (the first judgment stands, so machines do not trade verdicts).
    /// </summary>
    public static bool ShouldReplace(SharedVerdict? existing, SharedVerdict incoming)
    {
        if (existing is null)
        {
            return true;
        }

        if (existing.ForMessage == incoming.ForMessage)
        {
            return false;
        }

        var a = MessageTime(existing);
        var b = MessageTime(incoming);
        return (a, b) switch
        {
            ({ } older, { } newer) => newer > older,
            (null, not null) => true,
            (not null, null) => false,
            _ => incoming.At > existing.At,
        };
    }

    /// <summary>A Slack ts is seconds since 1970; mail carries its time beside the id.</summary>
    public static DateTimeOffset? MessageTime(SharedVerdict v) =>
        v.Source == "slack" && decimal.TryParse(v.ForMessage, NumberStyles.Number, CultureInfo.InvariantCulture, out var ts)
            ? DateTimeOffset.UnixEpoch.AddTicks((long)(ts * TimeSpan.TicksPerSecond))
            : v.MessageAt;

    public static string Why(string? why)
    {
        var s = (why ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= MaxWhy ? s : s[..MaxWhy].TrimEnd();
    }
}

/// <summary>Reads and writes the verdict files.</summary>
public static class VerdictFile
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions Quote = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The folder name per source: the repository says email, as the Handled marks do.</summary>
    public static string Folder(string repositoryRoot, string source) =>
        Path.Combine(repositoryRoot, "verdicts", source == "mail" ? "email" : source == "slack" ? "slack" : throw new ArgumentException($"Unknown source {source}.", nameof(source)));

    public static string PathFor(string repositoryRoot, string source, string key) =>
        Path.Combine(Folder(repositoryRoot, source), $"{DataRepository.SafeName(key)}.yaml");

    public static SharedVerdict? Load(string repositoryRoot, string source, string key)
    {
        var path = PathFor(repositoryRoot, source, key);
        return File.Exists(path) ? Parse(source, key, File.ReadAllText(path)) : null;
    }

    public static IReadOnlyList<SharedVerdict> LoadAll(string repositoryRoot, string source)
    {
        var folder = Folder(repositoryRoot, source);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(folder, "*.yaml")
                .Select(f => Parse(source, Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)))
                .OfType<SharedVerdict>(),
        ];
    }

    /// <summary>Writes <paramref name="verdict"/> unless the file already holds one for a newer message; true when written.</summary>
    public static bool Merge(string repositoryRoot, SharedVerdict verdict)
    {
        if (!VerdictRules.ShouldReplace(Load(repositoryRoot, verdict.Source, verdict.Key), verdict))
        {
            return false;
        }

        var path = PathFor(repositoryRoot, verdict.Source, verdict.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Format(verdict), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return true;
    }

    public static string Format(SharedVerdict v)
    {
        string Q(string s) => JsonSerializer.Serialize(s, Quote);
        var sb = new StringBuilder();
        // Quoted: a Slack ts read as a number would lose its trailing zeros.
        sb.Append("forMessage: ").Append(Q(v.ForMessage)).Append('\n');
        if (v.MessageAt is { } m)
        {
            sb.Append("messageAt: ").Append(Iso(m)).Append('\n');
        }

        sb.Append("needsReply: ").Append(v.NeedsReply ? "true" : "false").Append('\n');
        sb.Append("why: ").Append(Q(VerdictRules.Why(v.Why))).Append('\n');
        sb.Append("at: ").Append(Iso(v.At)).Append('\n');
        sb.Append("machine: ").Append(Q(v.Machine)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Null for a file that cannot be read: skipped, never fatal.</summary>
    public static SharedVerdict? Parse(string source, string key, string text)
    {
        Front? f;
        try
        {
            f = Yaml.Deserialize<Front?>(text);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        if (f?.ForMessage is not { Length: > 0 } forMessage || f.NeedsReply is not { } needsReply)
        {
            return null;
        }

        return new SharedVerdict(source, key, forMessage, Time(f.MessageAt), needsReply, VerdictRules.Why(f.Why), Time(f.At) ?? DateTimeOffset.UnixEpoch, f.Machine ?? "");
    }

    private static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private sealed class Front
    {
        public string? ForMessage { get; set; }

        public string? MessageAt { get; set; }

        public bool? NeedsReply { get; set; }

        public string? Why { get; set; }

        public string? At { get; set; }

        public string? Machine { get; set; }
    }
}
