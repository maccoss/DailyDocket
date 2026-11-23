using DailyDocket.Core.Notifications;
using DailyDocket.Core.Outbox;

namespace DailyDocket.Tests.Notifications;

public sealed class NoticeTests
{
    private static AttentionItem Email(string key) => new(NoticeTab.Email, key, key, $"Pat {key} is waiting on a reply", "\"QC report\": asks for data");

    private static AttentionItem Slack(string key) => new(NoticeTab.Slack, key, key, "Sam is waiting in Slack", "#lab: asks a question");

    [Fact]
    public void The_first_look_only_sets_the_baseline()
    {
        NoticeRules.Diff(null, [Email("t1"), Slack("C1~main")]).ShouldBeEmpty();
    }

    [Fact]
    public void Only_what_is_new_since_the_last_look_is_announced()
    {
        var before = NoticeRules.Keys([Email("t1")]);

        var notices = NoticeRules.Diff(before, [Email("t1"), Email("t2")]);

        notices.ShouldHaveSingleItem().ShouldBe(new Notice(NoticeTab.Email, "t2", "Pat t2 is waiting on a reply", "\"QC report\": asks for data"));
    }

    [Fact]
    public void Several_new_items_of_a_kind_are_one_notice_that_opens_the_first()
    {
        var notices = NoticeRules.Diff(new HashSet<string>(), [Email("t1"), Email("t2"), Email("t3"), Slack("C1~main")]);

        notices.Count.ShouldBe(2);
        notices[0].Title.ShouldBe("3 emails need a reply");
        notices[0].Select.ShouldBe("t1");
        notices[1].Tab.ShouldBe(NoticeTab.Slack);
    }

    [Fact]
    public void Something_answered_and_back_again_is_news_again()
    {
        var first = NoticeRules.Keys([Email("t1")]);
        var answered = NoticeRules.Keys([]);

        NoticeRules.Diff(first, [Email("t1")]).ShouldBeEmpty();
        NoticeRules.Diff(answered, [Email("t1")]).ShouldHaveSingleItem();
    }

    [Fact]
    public void Held_and_failed_replies_come_first_and_name_the_conversation()
    {
        var held = Item(OutboxKind.EmailReply, OutboxStatus.Held);
        var failed = Item(OutboxKind.SlackReply, OutboxStatus.Failed) with { Problem = "channel_not_found" };

        var heldItem = NoticeRules.FromOutbox(held)!;
        var failedItem = NoticeRules.FromOutbox(failed)!;
        var notices = NoticeRules.Diff(new HashSet<string>(), [Email("t1"), heldItem]);

        heldItem.Tab.ShouldBe(NoticeTab.Email);
        heldItem.Select.ShouldBe("thread-9");
        heldItem.Title.ShouldContain("held");
        failedItem.Tab.ShouldBe(NoticeTab.Slack);
        failedItem.Detail.ShouldContain("channel_not_found");
        notices[0].Title.ShouldContain("held");
    }

    [Fact]
    public void A_held_reply_that_then_fails_is_news_twice()
    {
        var held = NoticeRules.FromOutbox(Item(OutboxKind.EmailReply, OutboxStatus.Held))!;
        var failed = NoticeRules.FromOutbox(Item(OutboxKind.EmailReply, OutboxStatus.Failed))!;

        NoticeRules.Diff(NoticeRules.Keys([held]), [failed]).ShouldHaveSingleItem();
    }

    [Fact]
    public void Items_on_their_way_and_housekeeping_are_not_news()
    {
        NoticeRules.FromOutbox(Item(OutboxKind.EmailReply, OutboxStatus.Queued)).ShouldBeNull();
        NoticeRules.FromOutbox(Item(OutboxKind.EmailReply, OutboxStatus.Sent)).ShouldBeNull();
        NoticeRules.FromOutbox(Item(OutboxKind.EmailMarkRead, OutboxStatus.Failed)).ShouldBeNull();
        NoticeRules.FromOutbox(Item(OutboxKind.CalendarResponse, OutboxStatus.Failed))!.Tab.ShouldBe(NoticeTab.Schedule);
    }

    private static OutboxItem Item(OutboxKind kind, OutboxStatus status) =>
        OutboxRules.Action(kind, "thread-9", null, "", "QC report", DateTimeOffset.UtcNow, "pillar") with { Status = status };
}
