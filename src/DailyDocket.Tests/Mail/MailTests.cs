using DailyDocket.Core.Mail;
using DailyDocket.Tests.Fixtures;

namespace DailyDocket.Tests.Mail;

public sealed class GmailParserTests
{
    [Fact]
    public void A_summary_search_gives_each_threads_newest_message_and_the_page_token()
    {
        var (threads, next) = GmailParser.ParseSearch(Fixture.Read("gmail-search-summary.json"));

        next.ShouldBe("page-2");
        threads.Count.ShouldBe(2);
        threads[0].ThreadId.ShouldBe("thread-a");
        threads[0].NewestMessageId.ShouldBe("msg-a3");
        threads[0].Cc.ShouldBe(["cc@example.org"]);
        threads[1].Cc.ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_result_is_an_empty_object()
    {
        GmailParser.ParseSearch("{}").Threads.ShouldBeEmpty();
        GmailParser.ParseDrafts("{}").ShouldBeEmpty();
    }

    [Fact]
    public void A_thread_read_is_sorted_by_internal_date()
    {
        var (id, messages) = GmailParser.ParseThread(Fixture.Read("gmail-thread.json"));

        id.ShouldBe("thread-a");
        messages.Select(m => m.Id).ShouldBe(["msg-a1", "msg-a2", "msg-a3"]);
        messages[0].Date.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1795280400000));
        messages[2].Labels.ShouldContain("UNREAD");
        messages[2].Body.ShouldStartWith("Thanks. One more thing");
    }

    [Fact]
    public void A_bare_sender_address_gets_its_display_name()
    {
        GmailParser.ParseThread(Fixture.Read("gmail-thread.json")).Messages[1].Sender.ShouldBe("Owner Person <owner@example.org>");
    }
}

public sealed class MailRulesTests
{
    private static readonly IReadOnlyList<MailMessage> Thread = GmailParser.ParseThread(Fixture.Read("gmail-thread.json")).Messages;

    private static MailMessage Msg(string id, string sender, int day, string[]? to = null, string subject = "Hello", string body = "Can you help?", string[]? labels = null) =>
        new(id, "t", sender, to ?? ["owner@example.org"], [], new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero), labels ?? ["INBOX"], subject, body, body);

    [Fact]
    public void Something_arriving_after_his_reply_is_open()
    {
        var c = MailRules.Classify(Thread, hasDraft: false, Fixture.Config());

        c.Category.ShouldBe(MailCategory.Open);
        c.Trailing.ShouldBe(1);
        c.RepliedEarlier.ShouldBeTrue();
        c.WaitingOn.ShouldBe("Pat Example");
        c.NewestMessageId.ShouldBe("msg-a3");
        c.Unread.ShouldBeTrue();
    }

    [Fact]
    public void His_reply_last_is_the_last_word()
    {
        var c = MailRules.Classify([Msg("1", "pat@example.org", 1), Msg("2", "owner@example.org", 2, ["pat@example.org"])], false, Fixture.Config());

        c.Category.ShouldBe(MailCategory.LastWord);
    }

    [Fact]
    public void An_unsent_draft_outranks_the_last_word()
    {
        var c = MailRules.Classify([Msg("1", "pat@example.org", 1), Msg("2", "owner@example.org", 2, ["pat@example.org"])], true, Fixture.Config());

        c.Category.ShouldBe(MailCategory.Draft);
    }

    [Fact]
    public void Copied_on_a_conversation_he_never_wrote_in_is_being_informed()
    {
        var c = MailRules.Classify([Msg("1", "pat@example.org", 1, ["sam@example.org"])], false, Fixture.Config());

        c.Category.ShouldBe(MailCategory.Informed);
    }

    [Fact]
    public void Mail_to_a_group_he_answers_for_is_addressed_to_him()
    {
        // A request to the lab's services address names the group, not him, in To.
        var config = Fixture.Config();
        var request = new[] { Msg("1", "Erin Example <erin@example.edu>", 1, ["services@lab.example.org"]) };

        MailRules.Classify(request, false, config).Category.ShouldBe(MailCategory.Informed);
        config.Mail.Groups = ["services@lab.example.org"];
        MailRules.Classify(request, false, config).Category.ShouldBe(MailCategory.Open);

        // Only that address: one that merely contains it is someone else.
        config.Mail.IsGroup("notservices@lab.example.org").ShouldBeFalse();
        config.Mail.IsGroup("Lab Services <SERVICES@lab.example.org>").ShouldBeTrue();
    }

    [Fact]
    public void Copied_on_a_conversation_he_did_write_in_is_still_open()
    {
        var c = MailRules.Classify(
            [Msg("1", "pat@example.org", 1, ["sam@example.org"]), Msg("2", "owner@example.org", 2, ["pat@example.org"]), Msg("3", "sam@example.org", 3, ["pat@example.org"])],
            false,
            Fixture.Config());

        c.Category.ShouldBe(MailCategory.Open);
    }

    [Theory]
    [InlineData("Out of office Re: QC report", "pat@example.org", "Away", MailCategory.Auto)]
    [InlineData("Accepted: Lab meeting", "pat@example.org", "", MailCategory.Auto)]
    [InlineData("Weekly digest", "news@newsletter.example.com", "", MailCategory.Auto)]
    [InlineData("Build failed", "notifications@github.com", "", MailCategory.Auto)]
    [InlineData("Project sync", "pat@example.org", "https://zoom.us/j/123 Meeting ID: 123", MailCategory.Invite)]
    [InlineData("Project sync", "pat@example.org", "This meeting may be recorded.", MailCategory.Invite)]
    public void Machine_mail_and_invitations_are_never_read(string subject, string sender, string body, MailCategory expected)
    {
        MailRules.Classify([Msg("1", sender, 1, subject: subject, body: body)], false, Fixture.Config()).Category.ShouldBe(expected);
    }

    [Fact]
    public void A_note_asking_for_times_is_not_an_invitation()
    {
        MailRules.DropReason("Meeting next week?", "Could we find a time to meet next week?", "pat@example.org").ShouldBeNull();
    }

    [Fact]
    public void Trash_and_spam_are_hidden()
    {
        MailRules.Classify([Msg("1", "pat@example.org", 1, labels: ["TRASH"])], false, Fixture.Config()).Category.ShouldBe(MailCategory.Trash);
    }

    [Fact]
    public void The_triage_transcript_holds_only_what_arrived_after_his_reply()
    {
        var text = MailRules.TriageTranscript(Thread, Fixture.Config());

        text.ShouldContain("He ALREADY replied");
        text.ShouldContain("which normalization");
        text.ShouldNotContain("Could you send the QC report for the plasma batch");
        text.ShouldNotContain("Attached.");
    }

    [Fact]
    public void Each_judged_message_gets_at_least_the_minimum_share()
    {
        var long1 = new string('a', 5000);
        var msgs = Enumerable.Range(1, 6).Select(i => Msg(i.ToString(), "pat@example.org", i, body: long1)).ToList();

        var text = MailRules.TriageTranscript(msgs, Fixture.Config());

        text.Split("--- From:").Skip(1).ShouldAllBe(part => part.Count(ch => ch == 'a') >= MailRules.MinimumPerMessage);
    }

    [Theory]
    [InlineData("\"Pat Example\" <pat@example.org>", "Pat Example")]
    [InlineData("Pat Example <pat@example.org>", "Pat Example")]
    [InlineData("pat@example.org", "pat@example.org")]
    [InlineData("<pat@example.org>", "pat@example.org")]
    public void Display_names(string sender, string expected) => MailRules.DisplayName(sender).ShouldBe(expected);
}
