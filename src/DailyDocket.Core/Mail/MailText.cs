using System.Text.RegularExpressions;

namespace DailyDocket.Core.Mail;

/// <summary>How a message's text is shown in the reader.</summary>
public static partial class MailText
{
    /// <summary>
    /// Splits a plain-text body into what this message says and the quoted history below it.
    /// </summary>
    /// <remarks>
    /// Almost every reply repeats the thread beneath it, and the reader already shows each earlier
    /// message on its own, so the repeat is folded away. The split is at the first quote header
    /// ("On ... wrote:", Outlook's "From: / Sent:" block, "Original Message") or at a run of "&gt;"
    /// lines at the end. When nothing is left above the split, the whole text is the message.
    /// </remarks>
    public static (string Message, string? Quoted) SplitQuoted(string? body)
    {
        var text = (body ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.Length == 0)
        {
            return ("", null);
        }

        var cut = FirstHeader(text);
        if (cut is null)
        {
            // A trailing block of "> " lines with nothing after it.
            var lines = text.Split('\n');
            var start = lines.Length;
            while (start > 0 && (lines[start - 1].StartsWith('>') || lines[start - 1].Trim().Length == 0))
            {
                start--;
            }

            if (start < lines.Length && start > 0)
            {
                cut = string.Join('\n', lines.Take(start)).Length;
            }
        }

        if (cut is not { } at)
        {
            return (text, null);
        }

        var message = text[..at].TrimEnd();
        var quoted = text[at..].Trim();
        return message.Length == 0 ? (text, null) : (message, quoted.Length == 0 ? null : quoted);
    }

    private static int? FirstHeader(string text)
    {
        var found = new[] { OnWrote().Match(text), Outlook().Match(text), Original().Match(text) }
            .Where(m => m.Success)
            .Select(m => m.Index)
            .DefaultIfEmpty(-1)
            .Min();
        return found >= 0 ? found : null;
    }

    // "On Mon, Nov 20, 2026 at 9:15 AM Pat Example <pat@example.org> wrote:", which mail clients
    // often wrap onto a second line.
    [GeneratedRegex(@"(?m)^On [^\n]{4,200}?(\n[^\n]{0,120}?)?wrote:\s*$")]
    private static partial Regex OnWrote();

    // Outlook: "From: ..." followed within a few lines by "Sent:" or "Date:".
    [GeneratedRegex(@"(?m)^(?:_{5,}\s*\n)?\*?From:\*? [^\n]+\n(?:[^\n]*\n){0,2}?\*?(Sent|Date):\*? ")]
    private static partial Regex Outlook();

    [GeneratedRegex(@"(?m)^-{2,}\s*(Original Message|Forwarded message)\s*-{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Original();
}
