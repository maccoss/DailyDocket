using DailyDocket.Core.Drafting;

namespace DailyDocket.Tests.Drafting;

public sealed class ReplyGuideTests
{
    private const string Guide = """
        ---
        title: Prospective graduate students
        keywords: [PhD, graduate, rotation, Genome Sciences, applying]
        minMatches: 2
        ---
        Name only the program the student named.
        """;

    [Fact]
    public void A_guide_is_read_from_its_front_matter_and_text()
    {
        var g = ReplyGuide.Parse("graduate-inquiries", Guide).ShouldNotBeNull();

        g.Title.ShouldBe("Prospective graduate students");
        g.Keywords.Count.ShouldBe(5);
        g.Text.ShouldBe("Name only the program the student named.");
    }

    [Fact]
    public void It_is_chosen_only_when_enough_keywords_appear_as_whole_words()
    {
        var g = ReplyGuide.Parse("g", Guide)!;

        ReplyGuide.Choose([g], "I am applying to the Genome Sciences PhD program.").ShouldBe(g);
        ReplyGuide.Choose([g], "Our graduate seminar is Friday.").ShouldBeNull();       // one keyword
        ReplyGuide.Choose([g], "The PhDs and graduated students met.").ShouldBeNull(); // not whole words
    }

    [Fact]
    public void A_file_without_keywords_or_text_is_skipped()
    {
        ReplyGuide.Parse("x", "---\ntitle: t\n---\ntext").ShouldBeNull();
        ReplyGuide.Parse("x", "---\nkeywords: [a]\n---\n").ShouldBeNull();
        ReplyGuide.Parse("x", "no front matter").ShouldBeNull();
    }
}
