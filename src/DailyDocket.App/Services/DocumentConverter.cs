using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;

namespace DailyDocket.App.Services;

/// <summary>What the compose box holds, in each form a send needs.</summary>
/// <param name="Xaml">The document itself, so a draft reopens exactly as it was left.</param>
/// <param name="Html">Email's formatted body: a div per line, as Gmail writes it.</param>
/// <param name="Text">Email's plain-text alternative.</param>
/// <param name="Markdown">Slack's message (its send tool takes standard Markdown).</param>
public sealed record ComposedText(string Xaml, string Html, string Text, string Markdown)
{
    public bool IsEmpty => Text.Trim().Length == 0;
}

/// <summary>
/// Turns the compose box's document into HTML, plain text and Markdown. The formatting it knows is
/// the toolbar's: bold, italic, underline, bulleted and numbered lists, and links.
/// </summary>
public static class DocumentConverter
{
    private sealed record Segment(string Text, bool Bold, bool Italic, bool Underline, string? Link, bool Break);

    public static ComposedText Convert(FlowDocument document)
    {
        var html = new StringBuilder();
        var text = new StringBuilder();
        var md = new StringBuilder();

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    var segments = Merge(Segments(p.Inlines));
                    var line = Html(segments);
                    html.Append("<div>").Append(line.Length == 0 ? "<br>" : line).Append("</div>");
                    text.Append(Plain(segments)).Append('\n');
                    md.Append(Markdown(segments)).Append('\n');
                    break;

                case List list:
                    var numbered = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
                        or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;
                    html.Append(numbered ? "<ol>" : "<ul>");
                    var n = 1;
                    foreach (var item in list.ListItems)
                    {
                        var itemSegments = Merge(item.Blocks.OfType<Paragraph>().SelectMany(ip => Segments(ip.Inlines)).ToList());
                        var marker = numbered ? $"{n}. " : "- ";
                        html.Append("<li>").Append(Html(itemSegments)).Append("</li>");
                        text.Append(marker).Append(Plain(itemSegments)).Append('\n');
                        md.Append(marker).Append(Markdown(itemSegments)).Append('\n');
                        n++;
                    }

                    html.Append(numbered ? "</ol>" : "</ul>");
                    break;
            }
        }

        return new ComposedText(XamlWriter.Save(document), html.ToString(), text.ToString().TrimEnd(), md.ToString().TrimEnd());
    }

    /// <summary>
    /// A draft written in light Markdown (Claude's): paragraphs, "- " and "1. " lists, **bold**,
    /// _italic_ and [links](url), as a document the compose box can edit.
    /// </summary>
    public static FlowDocument FromMarkdown(string markdown)
    {
        var doc = new FlowDocument();
        List? list = null;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var bullet = System.Text.RegularExpressions.Regex.Match(line, @"^\s*[-*]\s+(.*)$");
            var number = System.Text.RegularExpressions.Regex.Match(line, @"^\s*\d+[.)]\s+(.*)$");
            if (bullet.Success || number.Success)
            {
                var style = number.Success ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                if (list is null || list.MarkerStyle != style)
                {
                    list = new List { MarkerStyle = style };
                    doc.Blocks.Add(list);
                }

                var item = new Paragraph();
                AddInlines(item.Inlines, (bullet.Success ? bullet : number).Groups[1].Value);
                list.ListItems.Add(new ListItem(item));
                continue;
            }

            list = null;
            var p = new Paragraph();
            AddInlines(p.Inlines, line);
            doc.Blocks.Add(p);
        }

        if (doc.Blocks.Count == 0)
        {
            doc.Blocks.Add(new Paragraph());
        }

        return doc;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"\*\*(.+?)\*\*|(?<![\w*])_(.+?)_(?![\w*])|\[([^\]]+)\]\((https?://[^)\s]+)\)");
        var at = 0;
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
        {
            if (m.Index > at)
            {
                inlines.Add(new Run(text[at..m.Index]));
            }

            if (m.Groups[1].Success)
            {
                inlines.Add(new Bold(new Run(m.Groups[1].Value)));
            }
            else if (m.Groups[2].Success)
            {
                inlines.Add(new Italic(new Run(m.Groups[2].Value)));
            }
            else
            {
                inlines.Add(new Hyperlink(new Run(m.Groups[3].Value)) { NavigateUri = new Uri(m.Groups[4].Value) });
            }

            at = m.Index + m.Length;
        }

        if (at < text.Length)
        {
            inlines.Add(new Run(text[at..]));
        }
    }

    /// <summary>Reopens a saved draft's document; an unreadable one opens empty rather than failing.</summary>
    public static FlowDocument Load(string? xaml)
    {
        if (!string.IsNullOrWhiteSpace(xaml))
        {
            try
            {
                if (XamlReader.Parse(xaml) is FlowDocument doc)
                {
                    return doc;
                }
            }
            catch (XamlParseException)
            {
                // Fall through to an empty document.
            }
        }

        return new FlowDocument(new Paragraph());
    }

    private static List<Segment> Segments(InlineCollection inlines)
    {
        var list = new List<Segment>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    list.Add(new Segment(run.Text, IsBold(run), run.FontStyle == FontStyles.Italic, IsUnderlined(run), LinkOf(run), false));
                    break;
                case LineBreak:
                    list.Add(new Segment("", false, false, false, null, true));
                    break;
                case Span span:
                    list.AddRange(Segments(span.Inlines));
                    break;
            }
        }

        return list;
    }

    /// <summary>Joins neighbors with the same formatting, so "Fri" and "day" bolded apart are one bold word.</summary>
    private static List<Segment> Merge(List<Segment> segments)
    {
        var merged = new List<Segment>();
        foreach (var s in segments.Where(s => s.Break || s.Text.Length > 0))
        {
            if (merged.Count > 0 && !s.Break && !merged[^1].Break && merged[^1] with { Text = "" } == s with { Text = "" })
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + s.Text };
            }
            else
            {
                merged.Add(s);
            }
        }

        return merged;
    }

    private static string Html(List<Segment> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (s.Break)
            {
                sb.Append("<br>");
                continue;
            }

            var t = WebUtility.HtmlEncode(s.Text);
            if (s.Bold)
            {
                t = $"<b>{t}</b>";
            }

            if (s.Italic)
            {
                t = $"<i>{t}</i>";
            }

            if (s.Underline && s.Link is null)
            {
                t = $"<u>{t}</u>";
            }

            if (s.Link is { } href)
            {
                t = $"<a href=\"{WebUtility.HtmlEncode(href)}\">{t}</a>";
            }

            sb.Append(t);
        }

        return sb.ToString();
    }

    private static string Plain(List<Segment> segments) =>
        string.Concat(segments.Select(s => s.Break ? "\n" : s.Link is { } href && href != s.Text ? $"{s.Text} ({href})" : s.Text));

    private static string Markdown(List<Segment> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (s.Break)
            {
                sb.Append('\n');
                continue;
            }

            // Markers must hug the text: "** word**" is not bold, so spaces stay outside them.
            var lead = s.Text[..(s.Text.Length - s.Text.TrimStart().Length)];
            var trail = s.Text[s.Text.TrimEnd().Length..];
            var core = s.Text.Trim();
            if (core.Length == 0)
            {
                sb.Append(s.Text);
                continue;
            }

            if (s.Bold)
            {
                core = $"**{core}**";
            }

            if (s.Italic)
            {
                core = $"_{core}_";
            }

            if (s.Link is { } href)
            {
                core = $"[{core}]({href})";
            }

            sb.Append(lead).Append(core).Append(trail);
        }

        return sb.ToString();
    }

    private static bool IsBold(Run run) => run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();

    /// <summary>Underline is not inherited, so it is looked for on the run and the spans around it.</summary>
    private static bool IsUnderlined(Inline inline)
    {
        for (DependencyObject? e = inline; e is Inline i; e = i.Parent)
        {
            if (i.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static string? LinkOf(Inline inline)
    {
        for (DependencyObject? e = inline; e is Inline i; e = i.Parent)
        {
            if (i is Hyperlink { NavigateUri: { } uri })
            {
                return uri.OriginalString;
            }
        }

        return null;
    }
}
