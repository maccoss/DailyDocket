using DailyDocket.Core.Mail;

namespace DailyDocket.Tests.Mail;

public sealed class MailTextTests
{
    [Fact]
    public void A_gmail_quote_header_is_folded_away()
    {
        var (message, quoted) = MailText.SplitQuoted("Sounds good, Friday works.\n\nOn Mon, Nov 20, 2026 at 9:15 AM Pat Example <pat@example.org> wrote:\n> Could we meet Friday?");

        message.ShouldBe("Sounds good, Friday works.");
        quoted.ShouldNotBeNull().ShouldStartWith("On Mon");
    }

    [Fact]
    public void A_quote_header_wrapped_onto_two_lines_is_found()
    {
        var (message, quoted) = MailText.SplitQuoted("Yes.\n\nOn Mon, Nov 20, 2026 at 9:15 AM Pat Example <\npat@example.org> wrote:\n\n> Ok?");

        message.ShouldBe("Yes.");
        quoted.ShouldNotBeNull();
    }

    [Fact]
    public void An_outlook_header_block_is_folded_away()
    {
        var (message, quoted) = MailText.SplitQuoted("Attached is the letter.\r\n\r\nFrom: Pat Example <pat@example.org>\r\nSent: Monday, November 20, 2026 9:15 AM\r\nTo: Owner\r\nSubject: Letter\r\n\r\nCould you sign?");

        message.ShouldBe("Attached is the letter.");
        quoted.ShouldNotBeNull().ShouldContain("Could you sign?");
    }

    [Fact]
    public void Trailing_angle_quoted_lines_are_folded_away()
    {
        var (message, quoted) = MailText.SplitQuoted("Done.\n\n> Can you check the column?\n> Thanks");

        message.ShouldBe("Done.");
        quoted.ShouldBe("> Can you check the column?\n> Thanks");
    }

    [Fact]
    public void Text_with_no_quote_is_all_message()
    {
        MailText.SplitQuoted("Just a note.\nSecond line.").ShouldBe(("Just a note.\nSecond line.", null));
    }

    [Fact]
    public void A_message_that_is_only_a_quote_is_shown_whole()
    {
        MailText.SplitQuoted("On Mon, Nov 20, 2026 Pat wrote:\n> hi").Quoted.ShouldBeNull();
    }

    [Fact]
    public void A_sentence_starting_with_on_is_not_a_quote_header()
    {
        MailText.SplitQuoted("On Friday we ran the samples.\nThe column held.").Quoted.ShouldBeNull();
    }
}
