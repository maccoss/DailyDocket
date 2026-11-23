using DailyDocket.Core.Processes;
using DailyDocket.Core.Sync;
using DailyDocket.Core.Todos;

namespace DailyDocket.Tests.Todos;

public sealed class TodoBookTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-book-").FullName;
    private readonly TodoBook _book;
    private int _changes;

    public TodoBookTests()
    {
        var tools = new ToolLocator();
        _book = new TodoBook(new DataRepository(_dir), new DataSync(new ProcessRunner(tools), tools, _dir, "skiddaw"), "skiddaw");
        _book.Changed += () => _changes++;
    }

    public void Dispose()
    {
        _book.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_new_task_is_written_at_once_at_the_bottom()
    {
        var first = _book.Add("First", "", null, []);
        var second = _book.Add("Second", "notes", new DateOnly(2026, 12, 1), ["lab"], new TodoSource("email", "t1", "Subject"));

        second.Rank.ShouldBeGreaterThan(first.Rank);
        new DataRepository(_dir).LoadTodos().Count.ShouldBe(2);
        new DataRepository(_dir).LoadTodos().Single(t => t.Id == second.Id).Source.ShouldBe(new TodoSource("email", "t1", "Subject"));
        _changes.ShouldBe(2);
    }

    [Fact]
    public void Finishing_and_reopening_records_when()
    {
        var t = _book.Add("Task", "", null, []);

        _book.SetDone(t.Id, true);
        _book.Items.Single().DoneAt.ShouldNotBeNull();

        _book.SetDone(t.Id, false);
        _book.Items.Single().Done.ShouldBeFalse();
        _book.Items.Single().DoneAt.ShouldBeNull();
    }

    [Fact]
    public void Moving_and_nudging_change_only_the_moved_tasks_rank()
    {
        var a = _book.Add("A", "", null, []);
        var b = _book.Add("B", "", null, []);
        var c = _book.Add("C", "", null, []);

        _book.Move(c.Id, a.Id);
        TodoRules.OpenInOrder(_book.Items).Select(t => t.Title).ShouldBe(["C", "A", "B"]);
        _book.Items.Single(t => t.Id == a.Id).Rank.ShouldBe(a.Rank);

        _book.Nudge(b.Id, -1);
        TodoRules.OpenInOrder(_book.Items).Select(t => t.Title).ShouldBe(["C", "B", "A"]);

        _book.Nudge(c.Id, -1); // already first
        TodoRules.OpenInOrder(_book.Items).Select(t => t.Title).ShouldBe(["C", "B", "A"]);
    }

    [Fact]
    public void A_removed_task_leaves_the_list_and_the_folder()
    {
        var t = _book.Add("Gone", "", null, []);

        _book.Delete(t.Id);

        _book.Items.ShouldBeEmpty();
        new DataRepository(_dir).LoadTodos().ShouldBeEmpty();
    }

    [Fact]
    public void Handled_and_undo_update_the_set_the_tabs_read()
    {
        _book.MarkHandled("email", "thread1", "2026-11-20T17:00:00.0000000+00:00");
        _book.MarkHandled("slack", "C1~main", "1795881600.000200");

        _book.Handled.CoversEmail("thread1", new DateTimeOffset(2026, 11, 20, 17, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        _book.Handled.CoversSlack("C1~main", "1795881600.000200").ShouldBeTrue();

        _book.Unhandle("email", "thread1");

        _book.Handled.CoversEmail("thread1", null).ShouldBeFalse();
        new DataRepository(_dir).LoadHandled("email").ShouldBeEmpty();
        new DataRepository(_dir).LoadHandled("slack").ShouldContainKey("C1~main");
    }

    [Fact]
    public void Reload_picks_up_files_another_machine_wrote()
    {
        new DataRepository(_dir).Save(new TodoItem("tother", "From pillar", "", null, [], 1, false, null, DateTimeOffset.Now, null, DateTimeOffset.Now, "pillar"));

        _book.Reload();

        _book.Items.ShouldHaveSingleItem().UpdatedOn.ShouldBe("pillar");
    }
}
