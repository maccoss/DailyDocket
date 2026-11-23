using System.Text.RegularExpressions;

namespace DailyDocket.Core.Text;

/// <summary>One piece of text: plain, or a web address to show as a link.</summary>
public sealed record TextPiece(string Text, Uri? Link);

/// <summary>Finds the web addresses in text Mike wrote (a to-do's notes), so they can be clicked.</summary>
/// <remarks>
/// To-do notes often hold a Gmail or document address to go back to, and as plain text it could
/// only be copied (November 2026). Only http and https become links: a note is his own text, but a
/// to-do drafted from a message can carry what the message said, and no other scheme should run.
/// </remarks>
public static partial class Links
{
    public static IReadOnlyList<TextPiece> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var pieces = new List<TextPiece>();
        var at = 0;
        foreach (Match m in Url().Matches(text))
        {
            // Sentence punctuation after an address belongs to the sentence.
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                continue;
            }

            if (m.Index > at)
            {
                pieces.Add(new TextPiece(text[at..m.Index], null));
            }

            pieces.Add(new TextPiece(url, uri));
            at = m.Index + url.Length;
        }

        if (at < text.Length)
        {
            pieces.Add(new TextPiece(text[at..], null));
        }

        return pieces;
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();
}
