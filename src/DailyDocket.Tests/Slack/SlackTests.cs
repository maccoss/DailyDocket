using System.Text.Json.Nodes;
using DailyDocket.Core.Slack;
using DailyDocket.Tests.Fixtures;

namespace DailyDocket.Tests.Slack;

public sealed class SlackTests
{
    private static string Envelope(string cursor = "next==") =>
        new JsonObject { ["results"] = Fixture.Read("slack-search.txt"), ["pagination_info"] = $"For the next page of results use cursor `{cursor}`" }.ToJsonString();

    [Fact]
    public void The_report_inside_the_envelope_is_read_with_its_cursor()
    {
        var (messages, cursor) = SlackParser.ParseSearch(Envelope());

        cursor.ShouldBe("next==");
        messages.Count.ShouldBe(5); // four results and one context reply

        var dm = messages[0];
        dm.ChannelId.ShouldBe("D0000000001");
        dm.IsDm.ShouldBeTrue();
        dm.FromId.ShouldBe("U0000000002");
        dm.FromName.ShouldBe("Pat Example");
        dm.Ts.ShouldBe("1795878902.000100");
        dm.ThreadTs.ShouldBeNull();
        dm.Text.ShouldBe("Could you send the QC report for the plasma batch by Friday?");
    }

    [Fact]
    public void A_threaded_message_takes_its_thread_from_the_permalink_and_stops_before_the_context()
    {
        var mention = SlackParser.ParseSearch(Envelope()).Messages[1];

        mention.ThreadTs.ShouldBe("1795880000.000050");
        mention.ChannelLabel.ShouldBe("lab-instruments");
        mention.Text.ShouldEndWith("swap it or keep running?");
        mention.Text.ShouldNotContain("this afternoon");
    }

    [Fact]
    public void A_context_reply_joins_its_results_thread()
    {
        var reply = SlackParser.ParseSearch(Envelope()).Messages.Single(m => m.Ts == "1795881700.000300");

        reply.FromId.ShouldBe("U0000000005");
        reply.FromName.ShouldBe("Lee Lab");
        reply.ChannelId.ShouldBe("C0000000003");
        reply.ThreadTs.ShouldBe("1795880000.000050");
        reply.Text.ShouldBe("I can look at it this afternoon.");
    }

    [Fact]
    public void His_reply_in_the_context_clears_the_mention()
    {
        var text = Fixture.Read("slack-search.txt").Replace(
            "- From: Lee Lab <lee@example.org> (ID: U0000000005)",
            "- From: Owner Person <owner@example.org> (ID: U0000000001)",
            StringComparison.Ordinal);
        var envelope = new JsonObject { ["results"] = text, ["pagination_info"] = "" }.ToJsonString();

        var thread = SlackRules.Group(SlackParser.ParseSearch(envelope).Messages).Single(c => c.Key == "C0000000003~1795880000.000050");

        SlackRules.IsWaiting(thread, "U0000000001").ShouldBeFalse();
    }

    [Fact]
    public void An_app_with_no_name_is_named_by_its_id()
    {
        SlackParser.ParseSearch(Envelope()).Messages.Single(m => m.FromId == "B0000000007").FromName.ShouldBe("app B0000000007");
    }

    [Fact]
    public void The_last_page_has_no_cursor()
    {
        var text = new JsonObject { ["results"] = Fixture.Read("slack-search.txt"), ["pagination_info"] = "" }.ToJsonString();

        SlackParser.ParseSearch(text).Cursor.ShouldBeNull();
    }

    [Fact]
    public void A_dm_is_one_conversation_so_his_answer_clears_the_question()
    {
        var conversations = SlackRules.Group(SlackParser.ParseSearch(Envelope()).Messages);

        var dm = conversations.Single(c => c.Key == "D0000000001~main");
        dm.Messages.Count.ShouldBe(2);
        dm.Last.FromId.ShouldBe("U0000000002");
        SlackRules.IsWaiting(dm, "U0000000001").ShouldBeTrue();

        conversations.Single(c => c.Key == "C0000000003~1795880000.000050").ThreadTs.ShouldBe("1795880000.000050");
    }

    [Fact]
    public void His_message_last_means_nothing_is_waiting()
    {
        var all = SlackParser.ParseSearch(Envelope()).Messages.ToList();
        var mine = all.Single(m => m.FromId == "U0000000001") with { Ts = "1795999999.000000" };

        var dm = SlackRules.Group([.. all, mine]).Single(c => c.Key == "D0000000001~main");

        SlackRules.IsWaiting(dm, "U0000000001").ShouldBeFalse();
    }

    [Fact]
    public void A_dm_is_titled_by_the_others_in_it()
    {
        SlackRules.Others(SlackParser.ParseSearch(Envelope()).Messages[0], "Owner Person").ShouldBe("Pat Example");
    }

    [Fact]
    public void A_reaction_from_him_is_found_by_his_id()
    {
        SlackParser.ReactedBy("""{"reactions":[{"name":"thumbsup","users":["U0000000001"]}]}""", "U0000000001").ShouldBeTrue();
        SlackParser.ReactedBy("""{"reactions":[]}""", "U0000000001").ShouldBeFalse();
    }
}
