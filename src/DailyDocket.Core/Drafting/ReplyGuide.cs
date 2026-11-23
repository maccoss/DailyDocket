using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DailyDocket.Core.Drafting;

/// <summary>
/// How Mike answers one kind of email (prospective graduate students, review invitations, ...):
/// a Markdown file in the data repository's config/replies folder, given to Draft with Claude when
/// a conversation matches it.
/// </summary>
/// <remarks>
/// A guide lives in the private data repository, not in the app, because it is his own practice
/// and names people. Its front matter says when it applies: <c>keywords</c>, of which at least
/// <c>minMatches</c> distinct ones must appear in the conversation, so one stray word does not pull
/// in the wrong guide.
/// </remarks>
public sealed record ReplyGuide(string Name, string Title, IReadOnlyList<string> Keywords, int MinMatches, string Text)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<ReplyGuide> Load(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var guides = new List<ReplyGuide>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").Order(StringComparer.Ordinal))
        {
            if (Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)) is { } guide)
            {
                guides.Add(guide);
            }
        }

        return guides;
    }

    internal static ReplyGuide? Parse(string name, string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal) || text.IndexOf("\n---", 4, StringComparison.Ordinal) is var end && end < 0)
        {
            return null;
        }

        Front? front;
        try
        {
            front = Yaml.Deserialize<Front?>(text[4..end]);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        var body = text[(text.IndexOf('\n', end + 1) + 1)..].Trim();
        return front is { Keywords.Count: > 0 } && body.Length > 0
            ? new ReplyGuide(name, front.Title ?? name, front.Keywords, Math.Max(1, front.MinMatches), body)
            : null;
    }

    /// <summary>How many of the guide's keywords appear in <paramref name="conversation"/>, as whole words.</summary>
    public int Score(string conversation) =>
        Keywords.Count(k => Regex.IsMatch(conversation, $@"(?<![\w]){Regex.Escape(k)}(?![\w])", RegexOptions.IgnoreCase));

    /// <summary>The guide that fits the conversation best, if any reaches its minimum.</summary>
    public static ReplyGuide? Choose(IEnumerable<ReplyGuide> guides, string conversation) =>
        guides.Select(g => (Guide: g, Score: g.Score(conversation)))
            .Where(x => x.Score >= x.Guide.MinMatches)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Guide)
            .FirstOrDefault();

    private sealed class Front
    {
        public string? Title { get; set; }

        public List<string> Keywords { get; set; } = [];

        public int MinMatches { get; set; } = 2;
    }
}
