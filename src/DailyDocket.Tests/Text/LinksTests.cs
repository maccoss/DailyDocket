using DailyDocket.Core.Text;

namespace DailyDocket.Tests.Text;

public sealed class LinksTests
{
    [Fact]
    public void Web_addresses_in_a_note_become_links_and_the_rest_stays_text()
    {
        var pieces = Links.Split("See https://mail.google.com/mail/u/0/#inbox/FMfcgz and the doc (https://docs.example.org/d/1).");

        pieces.Select(p => p.Text).ShouldBe(["See ", "https://mail.google.com/mail/u/0/#inbox/FMfcgz", " and the doc (", "https://docs.example.org/d/1", ")."]);
        pieces[1].Link!.AbsoluteUri.ShouldBe("https://mail.google.com/mail/u/0/#inbox/FMfcgz");
        pieces[0].Link.ShouldBeNull();
    }

    [Fact]
    public void Only_web_addresses_become_links()
    {
        Links.Split("Call file://server/share or javascript:alert(1) later").ShouldAllBe(p => p.Link == null);
        Links.Split("").ShouldBeEmpty();
        Links.Split(null).ShouldBeEmpty();
    }
}
