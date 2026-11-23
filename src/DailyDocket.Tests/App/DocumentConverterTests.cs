using System.Windows;
using System.Windows.Documents;
using DailyDocket.App.Services;

namespace DailyDocket.Tests.App;

public sealed class DocumentConverterTests
{
    /// <summary>WPF documents belong to an STA thread; xUnit's are MTA.</summary>
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw new InvalidOperationException("STA work failed", error);
    }

    private static ComposedText Sample() => OnSta(() =>
    {
        var first = new Paragraph();
        first.Inlines.Add(new Run("Thanks, "));
        first.Inlines.Add(new Bold(new Run("Fri")));
        first.Inlines.Add(new Run("day") { FontWeight = FontWeights.Bold });
        first.Inlines.Add(new Run(" works <for> me."));

        var list = new List { MarkerStyle = TextMarkerStyle.Disc };
        list.ListItems.Add(new ListItem(new Paragraph(new Italic(new Run("plasma")))));
        list.ListItems.Add(new ListItem(new Paragraph(new Underline(new Run("CSF")))));

        var numbered = new List { MarkerStyle = TextMarkerStyle.Decimal };
        numbered.ListItems.Add(new ListItem(new Paragraph(new Run("first"))));

        var link = new Paragraph();
        link.Inlines.Add(new Run("See "));
        link.Inlines.Add(new Hyperlink(new Run("the report")) { NavigateUri = new Uri("https://example.org/qc") });

        var doc = new FlowDocument();
        doc.Blocks.Add(first);
        doc.Blocks.Add(new Paragraph());
        doc.Blocks.Add(list);
        doc.Blocks.Add(numbered);
        doc.Blocks.Add(link);
        return DocumentConverter.Convert(doc);
    });

    [Fact]
    public void Html_is_a_div_per_line_with_the_toolbars_formatting()
    {
        var html = Sample().Html;

        html.ShouldStartWith("<div>Thanks, <b>Friday</b> works &lt;for&gt; me.</div><div><br></div>");
        html.ShouldContain("<ul><li><i>plasma</i></li><li><u>CSF</u></li></ul>");
        html.ShouldContain("<ol><li>first</li></ol>");
        html.ShouldContain("<div>See <a href=\"https://example.org/qc\">the report</a></div>");
    }

    [Fact]
    public void The_plain_text_alternative_keeps_lists_and_link_addresses()
    {
        Sample().Text.ShouldBe("Thanks, Friday works <for> me.\n\n- plasma\n- CSF\n1. first\nSee the report (https://example.org/qc)");
    }

    [Fact]
    public void Slack_gets_markdown_with_markers_hugging_the_text()
    {
        Sample().Markdown.ShouldBe("Thanks, **Friday** works <for> me.\n\n- _plasma_\n- CSF\n1. first\nSee [the report](https://example.org/qc)");
    }

    [Fact]
    public void A_draft_reopens_as_it_was_left()
    {
        var text = OnSta(() => DocumentConverter.Convert(DocumentConverter.Load(Sample().Xaml)).Text);

        text.ShouldBe(Sample().Text);
    }

    [Fact]
    public void Claudes_markdown_draft_becomes_an_editable_document()
    {
        var back = OnSta(() => DocumentConverter.Convert(DocumentConverter.FromMarkdown(
            "Hi Catherine,\n\nThe figures look **good**, _thanks_.\n- keywords\n- data statement\n1. submit\n\nSee [the guide](https://example.org/g).\nMike")));

        back.Html.ShouldBe("<div>Hi Catherine,</div><div><br></div><div>The figures look <b>good</b>, <i>thanks</i>.</div>"
            + "<ul><li>keywords</li><li>data statement</li></ul><ol><li>submit</li></ol><div><br></div>"
            + "<div>See <a href=\"https://example.org/g\">the guide</a>.</div><div>Mike</div>");
    }

    [Fact]
    public void Drafts_never_keep_an_em_dash()
    {
        DailyDocket.Core.Drafting.DraftPlans.HouseStyle("Yes — Friday works—thanks.").ShouldBe("Yes, Friday works, thanks.");
    }

    [Fact]
    public void A_draft_run_has_no_tools_at_all()
    {
        var plan = DailyDocket.Core.Drafting.DraftPlans.Draft(Fixtures.Fixture.Config(), true, "--- From: pat@example.org\nCan you send it?", ["Thanks, will do.\nMike"], "yes, by Friday", "sonnet");

        plan.AllowedTools.ShouldBeEmpty();
        plan.DisallowedTools.ShouldContain("ToolSearch");
        plan.DisallowedTools.ShouldContain("mcp__claude_ai_Gmail__reply");
        plan.Prompt.ShouldContain("yes, by Friday");
        plan.Prompt.ShouldContain("Never use em dashes");
        plan.Capture.ShouldBeFalse();
    }

    [Fact]
    public void An_unreadable_draft_opens_empty()
    {
        OnSta(() => DocumentConverter.Convert(DocumentConverter.Load("<not xaml")).IsEmpty).ShouldBeTrue();
    }
}
