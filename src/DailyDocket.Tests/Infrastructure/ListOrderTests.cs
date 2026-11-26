using DailyDocket.Core.Infrastructure;

namespace DailyDocket.Tests.Infrastructure;

public sealed class ListOrderTests
{
    private sealed record Row(string Id, string From, string Topic, DateTimeOffset? Since, bool Draft = false);

    private static readonly DateTimeOffset Day = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(-8));

    private static readonly Row[] Rows =
    [
        new("a", "Pat Lee", "Re: QC report", Day),
        new("b", "sam Ortiz", "Astral calibration", Day.AddDays(3)),
        new("c", "Pat Lee", "Fwd: Budget", Day.AddDays(1)),
        new("d", "Ana Ruiz", "Seminar date", Day.AddDays(2), Draft: true),
        new("e", "Ana Ruiz", "#lab", null),
    ];

    private static string Ids(ListOrder order) =>
        string.Concat(ListOrdering.Sort(Rows, order, r => r.Since, r => r.From, r => r.Topic, r => r.Draft).Select(r => r.Id));

    [Fact]
    public void Each_order_sorts_the_list_and_an_unsent_draft_stays_on_top()
    {
        Ids(ListOrder.OldestFirst).ShouldBe("dacbe"); // no date sorts last when oldest is first
        Ids(ListOrder.NewestFirst).ShouldBe("debca");
        Ids(ListOrder.Sender).ShouldBe("decab"); // by name ignoring case, their newest first
    }

    [Fact]
    public void A_subject_sorts_without_its_reply_and_forward_prefixes_and_a_channel_without_its_hash()
    {
        ListOrdering.TopicKey("Re: Fwd: QC report").ShouldBe("QC report");
        ListOrdering.TopicKey("FW: re:Budget").ShouldBe("Budget");
        ListOrdering.TopicKey("#lab").ShouldBe("lab");
        ListOrdering.TopicKey("Regarding the grant").ShouldBe("Regarding the grant");

        // Astral, Budget, lab, QC report, Seminar date (the draft first).
        Ids(ListOrder.Topic).ShouldBe("dbcea");
    }

    [Fact]
    public void An_unknown_sort_choice_falls_back_to_oldest_first_and_keeps_the_other_settings()
    {
        var dir = Directory.CreateTempSubdirectory("dd-order-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);

            File.WriteAllText(path, """{"MailOrder": "Sideways", "SlackOrder": "newestfirst", "PullIntervalMinutes": 5}""");
            var loaded = store.Load();
            loaded.MailOrder.ShouldBe(ListOrder.OldestFirst);
            loaded.SlackOrder.ShouldBe(ListOrder.NewestFirst);
            loaded.PullIntervalMinutes.ShouldBe(5);

            loaded.MailOrder = ListOrder.Sender;
            store.Save(loaded);
            File.ReadAllText(path).ShouldContain("\"MailOrder\": \"Sender\"");
            store.Load().MailOrder.ShouldBe(ListOrder.Sender);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
