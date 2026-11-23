using System.Text.Json.Nodes;
using DailyDocket.Core.Mail;
using DailyDocket.Core.Outbox;
using DailyDocket.Data;
using DailyDocket.Hook;
using DailyDocket.Tests.Fixtures;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Tests.Outbox;

public sealed class OutboxTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 16, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-outbox-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MailMessage Msg(string id, string sender, int hour, string[] to, string[]? cc = null) =>
        new(id, "t", sender, to, cc ?? [], new DateTimeOffset(2026, 11, 20, hour, 0, 0, TimeSpan.Zero), ["INBOX"], "Subject", null, "body");

    private static OutboxItem Email(OutboxStatus status = OutboxStatus.Draft) => new(
        "o1", OutboxKind.EmailReply, status, "thread-a", "msg-a3", null, ["pat@example.org"], ["cc@example.org"], "Re: QC report", "msg-a3",
        "<FlowDocument/>", "<p>Thanks, <b>Friday</b> works.</p>", "Thanks, Friday works.", Now, Now, null, null, "skiddaw", 0, null, null);

    [Fact]
    public void Reply_goes_to_the_person_he_is_answering()
    {
        var thread = new[] { Msg("1", "Pat Example <pat@example.org>", 9, ["owner@example.org", "sam@example.org"], ["lee@example.org"]) };

        var (to, cc) = OutboxRules.Recipients(thread, replyAll: false, Fixture.Config());

        to.ShouldBe(["pat@example.org"]);
        cc.ShouldBeEmpty();
    }

    [Fact]
    public void Reply_all_adds_everyone_else_but_never_him()
    {
        var thread = new[] { Msg("1", "Pat Example <pat@example.org>", 9, ["owner@example.org", "Sam <sam@example.org>"], ["lee@example.org", "owner@example.org"]) };

        var (to, cc) = OutboxRules.Recipients(thread, replyAll: true, Fixture.Config());

        to.ShouldBe(["pat@example.org", "sam@example.org"]);
        cc.ShouldBe(["lee@example.org"]);
    }

    [Fact]
    public void A_follow_up_to_his_own_message_goes_to_the_people_he_wrote_to()
    {
        var thread = new[] { Msg("1", "owner@example.org", 9, ["pat@example.org"]) };

        OutboxRules.Recipients(thread, replyAll: false, Fixture.Config()).To.ShouldBe(["pat@example.org"]);
    }

    [Theory]
    [InlineData("pat@example.org", true)]
    [InlineData("Pat <pat@example.org>", false)]
    [InlineData("pat@example", false)]
    [InlineData("a@b.org, c@d.org", false)]
    public void Typed_addresses_are_checked(string text, bool ok) => OutboxRules.IsAddress(text).ShouldBe(ok);

    [Fact]
    public void An_email_reply_names_its_recipients_and_both_bodies()
    {
        var call = OutboxRules.Call(Email(), includeSignature: true);

        call.ToolName.ShouldBe("mcp__claude_ai_Gmail__reply");
        call.Input["messageId"]!.GetValue<string>().ShouldBe("msg-a3");
        call.Input["to"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["pat@example.org"]);
        call.Input["cc"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["cc@example.org"]);
        call.Input["htmlBody"]!.GetValue<string>().ShouldContain("<b>Friday</b>");
        call.Input["body"]!.GetValue<string>().ShouldBe("Thanks, Friday works.");
        call.Input["includeSignature"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void A_slack_reply_goes_in_its_thread_when_it_has_one()
    {
        var slack = Email() with { Kind = OutboxKind.SlackReply, Target = "C1~1795880000.000050", ReplyTo = "1795880000.000050", Channel = "C1", Text = "**Swap** it." };

        var call = OutboxRules.Call(slack, includeSignature: true);

        call.ToolName.ShouldBe("mcp__claude_ai_Slack__slack_send_message");
        call.Input.ToJsonString().ShouldBe("""{"channel_id":"C1","message":"**Swap** it.","thread_ts":"1795880000.000050"}""");
        OutboxRules.Call(slack with { ReplyTo = null }, true).Input.ContainsKey("thread_ts").ShouldBeFalse();
    }

    [Fact]
    public void The_guard_allows_the_claimed_call_exactly_once_and_nothing_else()
    {
        var item = Email();
        var call = OutboxRules.Call(item, includeSignature: true);
        var claims = Path.Combine(_dir, "claims.json");
        File.WriteAllText(claims, OutboxRules.ClaimsJson(item, call));

        string Attempt(string tool, JsonObject input) =>
            SendGuard.Decide(new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = input }.ToJsonString(), claims) ?? "allowed";

        var reworded = (JsonObject)call.Input.DeepClone();
        reworded["body"] = "Thanks, Friday works!";
        Attempt(call.ToolName, reworded).ShouldContain("DailyDocket send guard");

        var extra = (JsonObject)call.Input.DeepClone();
        extra["bcc"] = new JsonArray("attacker@example.com");
        Attempt(call.ToolName, extra).ShouldContain("DailyDocket send guard");

        Attempt("mcp__claude_ai_Gmail__send_message", (JsonObject)call.Input.DeepClone()).ShouldContain("DailyDocket send guard");

        // Same call, keys in another order: allowed, once.
        var reordered = new JsonObject();
        foreach (var (k, v) in call.Input.Reverse())
        {
            reordered[k] = v!.DeepClone();
        }

        Attempt(call.ToolName, reordered).ShouldBe("allowed");
        Attempt(call.ToolName, (JsonObject)call.Input.DeepClone()).ShouldContain("DailyDocket send guard");
    }

    [Fact]
    public void Only_a_guard_refusal_marks_an_unconfirmed_send_failed()
    {
        OutboxRules.AfterUnconfirmedSend(null, "DailyDocket send guard: no outbox item matches this call.").Status.ShouldBe(OutboxStatus.Failed);

        // Claude says it sent, but no capture was left: it may well have gone, so it is held, never retried blindly.
        OutboxRules.AfterUnconfirmedSend(null, "SENT").Status.ShouldBe(OutboxStatus.Held);
        OutboxRules.AfterUnconfirmedSend("The send sync took longer than 15 minutes and was stopped.", null).Status.ShouldBe(OutboxStatus.Held);
    }

    [Fact]
    public void An_email_is_held_when_anything_arrived_after_the_message_he_answered()
    {
        var fresh = new[] { Msg("msg-a1", "pat@example.org", 9, []), Msg("msg-a3", "pat@example.org", 10, []) };

        OutboxRules.EmailMovedOn(fresh, "msg-a3").ShouldBeFalse();
        OutboxRules.EmailMovedOn([.. fresh, Msg("msg-a4", "owner@example.org", 11, [])], "msg-a3").ShouldBeTrue();
    }

    [Fact]
    public void A_slack_reply_is_held_when_a_later_message_appears()
    {
        const string read = "Message TS: 1795880000.000050\nFrom: Pat (U2)\n...\nMessage TS: 1795881600.000200\n";

        OutboxRules.SlackMovedOn(read, "1795881600.000200").ShouldBeFalse();
        OutboxRules.SlackMovedOn(read + "Message TS: 1795881700.000300\n", "1795881600.000200").ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"id":"19b0aa","threadId":"thread-a","viewUrl":"https://mail.google.com/x"}""", "19b0aa")]
    [InlineData("Message sent: https://example.slack.com/archives/C1/p1795881700000300?thread_ts=1", "https://example.slack.com/archives/C1/p1795881700000300?thread_ts=1")]
    [InlineData("""{"ok":true,"ts":"1795881700.000300"}""", "1795881700.000300")]
    public void The_sent_messages_id_is_kept(string response, string expected) => OutboxRules.RemoteId(response).ShouldBe(expected);

    [Fact]
    public void The_outbox_is_saved_and_mirrored_to_json_without_sent_items()
    {
        var db = DocketDatabase.Open(Path.Combine(_dir, "docket.db"), "k");
        var export = Path.Combine(_dir, "outbox.json");
        var store = new OutboxStore(db, export);

        store.Save(Email(OutboxStatus.Queued) with { SendAfter = Now.AddSeconds(60) });
        store.Save(Email(OutboxStatus.Sent) with { Id = "o2", RemoteId = "19b0aa" });

        var back = store.Get("o1").ShouldNotBeNull();
        back.To.ShouldBe(["pat@example.org"]);
        back.SendAfter.ShouldBe(Now.AddSeconds(60));
        store.WithStatus(OutboxStatus.Queued).ShouldHaveSingleItem();
        store.ForTarget("thread-a").Select(i => i.Id).ShouldBe(["o1"]);
        var json = File.ReadAllText(export);
        json.ShouldContain("\"o1\"");
        json.ShouldNotContain("\"o2\"");

        store.Delete("o1");
        File.ReadAllText(export).ShouldNotContain("\"o1\"");
    }

    [Fact]
    public void A_database_the_key_cannot_open_is_kept_and_the_outbox_comes_back_from_its_export()
    {
        var path = Path.Combine(_dir, "docket.db");
        var export = Path.Combine(_dir, "outbox.json");
        var first = new OutboxStore(DocketDatabase.Open(path, "old-key"), export);
        first.Save(Email(OutboxStatus.Draft));
        first.Save(Email(OutboxStatus.Queued) with { Id = "o2", SendAfter = Now });
        SqliteConnection.ClearAllPools();

        var db = DocketDatabase.Open(path, "new-key");
        var again = new OutboxStore(db, export);

        db.MovedAside.ShouldNotBeNull();
        File.Exists(db.MovedAside).ShouldBeTrue();
        again.Restored.ShouldBe(2);
        again.Get("o1").ShouldNotBeNull().Status.ShouldBe(OutboxStatus.Draft);
        again.Get("o2").ShouldNotBeNull().Status.ShouldBe(OutboxStatus.Held); // never re-queued blindly
    }

    [Fact]
    public void Adding_the_outbox_keeps_the_existing_cache()
    {
        var path = Path.Combine(_dir, "v1.db");
        var db = DocketDatabase.Open(path, "k");
        using (var c = db.Connect())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO watermark (source, scope, value) VALUES ('mail','full','x'); DROP TABLE outbox_item; PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var again = new Store(DocketDatabase.Open(path, "k"));

        again.Watermark("mail", "full").ShouldBe("x");
        new OutboxStore(again.Database, null).WithStatus(OutboxStatus.Draft).ShouldBeEmpty();
    }
}
