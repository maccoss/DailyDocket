using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Notifications;
using DailyDocket.Core.Outbox;

namespace DailyDocket.Tests.Notifications;

public sealed class NoticeTests
{
    private static AttentionItem Email(string key) => new(NoticeTab.Email, key, key, $"Pat {key} is waiting on a reply", "\"QC report\": asks for data");

    private static AttentionItem Slack(string key) => new(NoticeTab.Slack, key, key, "Sam is waiting in Slack", "#lab: asks a question");

    private static AttentionItem Failed(string id) => new(NoticeTab.Email, $"outbox:{id}:Failed", "t9", "A reply was not sent", "\"QC report\": offline");

    [Fact]
    public void The_setting_picks_what_is_announced_but_everything_listed_counts_as_seen()
    {
        var before = NoticeRules.Keys([Email("t1")]);
        IReadOnlyList<AttentionItem> now = [Email("t1"), Email("t2"), Slack("C1~main"), Failed("o1")];

        NoticeRules.Diff(before, NoticeRules.Wanted(now, NotifyAbout.SendProblems)).ShouldHaveSingleItem().Title.ShouldBe("A reply was not sent");
        NoticeRules.Diff(before, NoticeRules.Wanted(now, NotifyAbout.Nothing)).ShouldBeEmpty();
        NoticeRules.Diff(before, NoticeRules.Wanted(now, NotifyAbout.Everything)).Count.ShouldBe(3);

        // Switched to Everything after a look: only what arrives from then on is announced.
        var seen = NoticeRules.Keys(now);
        NoticeRules.Diff(seen, NoticeRules.Wanted([.. now, Email("t3")], NotifyAbout.Everything)).ShouldHaveSingleItem().Select.ShouldBe("t3");
    }

    [Fact]
    public void The_old_on_off_switch_keeps_off_and_moves_on_to_the_new_default()
    {
        var dir = Directory.CreateTempSubdirectory("dd-settings-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);

            File.WriteAllText(path, """{"Notifications": false}""");
            store.Load().Notify.ShouldBe(NotifyAbout.Nothing);

            File.WriteAllText(path, """{"Notifications": true}""");
            var on = store.Load();
            on.Notify.ShouldBe(NotifyAbout.SendProblems);

            // Saved once, the old switch is gone and the choice is written by name.
            on.Notify = NotifyAbout.Everything;
            store.Save(on);
            var text = File.ReadAllText(path);
            text.ShouldNotContain("\"Notifications\"");
            text.ShouldContain("\"Notify\": \"Everything\"");
            store.Load().Notify.ShouldBe(NotifyAbout.Everything);

            // A choice this version does not know falls back to the default and keeps the other settings.
            foreach (var unknown in new[] { "\"Sometimes\"", "7", "\"7\"" })
            {
                File.WriteAllText(path, $$"""{"Notify": {{unknown}}, "PullIntervalMinutes": 5}""");
                var loaded = store.Load();
                loaded.Notify.ShouldBe(NotifyAbout.SendProblems);
                loaded.PullIntervalMinutes.ShouldBe(5);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

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
