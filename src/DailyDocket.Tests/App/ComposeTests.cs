using DailyDocket.App.Services;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Outbox;
using DailyDocket.Data;
using Microsoft.Data.Sqlite;

namespace DailyDocket.Tests.App;

public sealed class ComposeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-compose-").FullName;
    private readonly OutboxStore _outbox;
    private readonly ComposeViewModel _vm;
    private ComposedText _box = new("", "", "", "");

    public ComposeTests()
    {
        _outbox = new OutboxStore(DocketDatabase.Open(Path.Combine(_dir, "d.db"), "k"), null);
        _vm = new ComposeViewModel(_outbox, new AppSettings { UndoWindowSeconds = 60 }, OutboxKind.EmailReply) { CurrentContent = () => _box };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static ComposedText Text(string s) => new($"<FlowDocument>{s}</FlowDocument>", $"<div>{s}</div>", s, s);

    private void OpenThread(string id) => _vm.Open(id, $"{id}-m1", null, "Subject", $"{id}-m1", ["pat@example.org"], [], "Reply");

    [Fact]
    public void Switching_threads_saves_what_was_typed_before_the_pause_timer_fired()
    {
        OpenThread("thread-a");
        _box = Text("Typed just now");

        OpenThread("thread-b");

        _outbox.ForTarget("thread-a").ShouldHaveSingleItem().Text.ShouldBe("Typed just now");
    }

    [Fact]
    public void Closing_the_box_saves_what_was_typed()
    {
        OpenThread("thread-a");
        _box = Text("Half a reply");

        _vm.CloseCommand.Execute(null);

        _outbox.ForTarget("thread-a").ShouldHaveSingleItem().Status.ShouldBe(OutboxStatus.Draft);
    }

    [Fact]
    public void Send_refuses_while_a_typed_address_is_not_understood()
    {
        OpenThread("thread-a");
        _vm.NewTo = "pat@example";

        _vm.Send(Text("Thanks")).ShouldBeFalse();

        _vm.Note.ShouldNotBeNull().ShouldContain("pat@example");
        _outbox.WithStatus(OutboxStatus.Queued).ShouldBeEmpty();
    }

    [Fact]
    public void Send_adds_a_valid_typed_address_and_queues_behind_the_undo_window()
    {
        OpenThread("thread-a");
        _vm.NewCc = "lee@example.org";

        _vm.Send(Text("Thanks")).ShouldBeTrue();

        var queued = _outbox.WithStatus(OutboxStatus.Queued).ShouldHaveSingleItem();
        queued.Cc.ShouldBe(["lee@example.org"]);
        queued.SendAfter.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(50));
    }
}
