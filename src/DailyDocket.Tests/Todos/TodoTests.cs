using DailyDocket.Core.Processes;
using DailyDocket.Core.Sync;
using DailyDocket.Core.Todos;

namespace DailyDocket.Tests.Todos;

public sealed class TodoTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 16, 20, 0, TimeSpan.FromHours(-8));
    private readonly string _dir = Directory.CreateTempSubdirectory("dd-todo-").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal); // git marks its objects read-only
        }

        Directory.Delete(_dir, recursive: true);
    }

    private static TodoItem Task(string id = "t1", double rank = 1, DateOnly? due = null, bool done = false) =>
        new(id, "Send Eisai the PRM target list", "Use the \"final\" list:\nCSF first.", due ?? new DateOnly(2026, 11, 26), ["eisai", "assays"], rank, done,
            done ? Now : null, Now.AddDays(-2), new TodoSource("email", "19a2c0f3e8b1", "PRM targets"), Now, "skiddaw");

    [Fact]
    public void A_task_file_round_trips_with_quotes_and_line_breaks()
    {
        var task = Task();
        var text = DataRepository.Format(task);
        var back = DataRepository.Parse("t1", text).ShouldNotBeNull();

        back.Tags.ShouldBe(["eisai", "assays"]);
        (back with { Tags = task.Tags }).ShouldBe(task);
        text.ShouldStartWith("---\ntitle: \"Send Eisai the PRM target list\"\ndue: 2026-11-26\ntags: [\"eisai\", \"assays\"]\nrank: 1\n");
        text.ShouldContain("source: {kind: email, id: \"19a2c0f3e8b1\", subject: \"PRM targets\"}");
    }

    [Fact]
    public void A_task_with_no_date_no_tags_and_no_source_round_trips()
    {
        var plain = Task() with { Due = null, Tags = [], Source = null, Notes = "" };

        var back = DataRepository.Parse("t1", DataRepository.Format(plain)).ShouldNotBeNull();

        back.Tags.ShouldBeEmpty();
        (back with { Tags = plain.Tags }).ShouldBe(plain);
    }

    [Fact]
    public void An_unreadable_file_is_skipped_not_fatal()
    {
        DataRepository.Parse("t1", "no front matter").ShouldBeNull();
        DataRepository.Parse("t1", "---\ntitle: [unclosed\n---\n").ShouldBeNull();
    }

    [Fact]
    public void Tasks_and_marks_are_saved_and_loaded_and_deleted()
    {
        var repo = new DataRepository(_dir);
        repo.Save(Task("t1"));
        repo.Save(Task("t2", rank: 2));
        repo.Delete("t1");
        repo.Save(new HandledMark("slack", "C1~1795880000.000050", "1795881600.000200", Now, "skiddaw"));

        repo.LoadTodos().ShouldHaveSingleItem().Id.ShouldBe("t2");
        repo.LoadHandled("slack").ShouldContainKey("C1~1795880000.000050");
        repo.LoadHandled("slack")["C1~1795880000.000050"].UpTo.ShouldBe("1795881600.000200");
        repo.LoadHandled("slack")["C1~1795880000.000050"].On.ShouldBe("skiddaw");
    }

    [Fact]
    public void A_key_that_would_be_a_path_is_refused()
    {
        Should.Throw<ArgumentException>(() => new DataRepository(_dir).Save(new HandledMark("email", "..\\x", "u", Now, "m")));
    }

    [Fact]
    public void The_open_list_keeps_its_dragged_order_and_done_tasks_follow_newest_first()
    {
        var items = new[] { Task("a", 3), Task("b", 1), Task("c", 2, done: true), Task("d", 2) };

        TodoRules.OpenInOrder(items).Select(t => t.Id).ShouldBe(["b", "d", "a"]);
        TodoRules.DoneRecentFirst(items).Select(t => t.Id).ShouldBe(["c"]);
        TodoRules.BottomRank(items).ShouldBe(4);
    }

    [Fact]
    public void A_drop_takes_the_midpoint_so_only_one_file_changes()
    {
        TodoRules.RankBetween(Task(rank: 1), Task(rank: 2)).ShouldBe(1.5);
        TodoRules.RankBetween(null, Task(rank: 1)).ShouldBe(0);
        TodoRules.RankBetween(Task(rank: 4), null).ShouldBe(5);
        TodoRules.RankBetween(null, null).ShouldBe(1);
    }

    [Theory]
    [InlineData("2026-11-19", "3d late", true)]
    [InlineData("2026-11-22", "today", false)]
    [InlineData("2026-11-23", "tomorrow", false)]
    [InlineData("2026-11-25", "Wed", false)]
    [InlineData("2026-12-10", "Dec 10", false)]
    public void Due_labels_read_like_the_dashboards(string due, string text, bool late) =>
        TodoRules.DueLabel(DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture), new DateOnly(2026, 11, 22)).ShouldBe((text, late));

    [Fact]
    public void Tags_are_trimmed_deduplicated_and_capped()
    {
        TodoRules.ParseTags(" eisai, assays ,EISAI,, a,b,c,d,e,f,g").ShouldBe(["eisai", "assays", "a", "b", "c", "d", "e", "f"]);
    }

    [Fact]
    public void A_tags_color_is_the_same_everywhere_and_ignores_case()
    {
        TodoRules.TagHue("Eisai").ShouldBe(TodoRules.TagHue("eisai"));
        TodoRules.TagHue("eisai").ShouldBeInRange(0, TodoRules.TagHues - 1);
    }

    [Fact]
    public void New_ids_are_file_safe_and_distinct()
    {
        var a = TodoRules.NewId(Now);
        var b = TodoRules.NewId(Now);

        a.ShouldMatch("^t[0-9a-z]{12,}$");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void A_handled_mark_lapses_when_someone_writes_again()
    {
        var mail = new HandledMark("email", "t", "2026-11-20T17:00:00.0000000+00:00", Now, "m");
        TodoRules.CoversEmail(mail, new DateTimeOffset(2026, 11, 20, 17, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        TodoRules.CoversEmail(mail, new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero)).ShouldBeFalse();

        var slack = new HandledMark("slack", "C1~main", "1795881600.000200", Now, "m");
        TodoRules.CoversSlack(slack, "1795881600.000200").ShouldBeTrue();
        TodoRules.CoversSlack(slack, "1795881600.000201").ShouldBeFalse();
    }

    [Fact]
    public void The_artifacts_export_is_imported_once_in_its_displayed_order()
    {
        var export = Path.Combine(_dir, "export");
        Directory.CreateDirectory(Path.Combine(export, "todos"));
        Directory.CreateDirectory(Path.Combine(export, "handled"));
        Directory.CreateDirectory(Path.Combine(export, "slackHandled"));
        File.WriteAllText(Path.Combine(export, "todos", "tA.json"), """{"title":"Ranked","notes":"","due":"","done":false,"tags":["lab"],"rank":2,"createdAt":1790000000000}""");
        File.WriteAllText(Path.Combine(export, "todos", "tB.json"), """{"title":"Unranked, due","notes":"n","due":"2026-12-01","done":false,"tags":[],"createdAt":1790000000001}""");
        File.WriteAllText(Path.Combine(export, "todos", "tC.json"), """{"title":"Done one","notes":"","due":"","done":true,"doneAt":1791000000000,"tags":[],"createdAt":1789000000000,"source":{"kind":"slack","id":"D1~main","link":"https://example.slack.com/x"}}""");
        File.WriteAllText(Path.Combine(export, "todos", "tD.json"), """{"title":"  ","notes":"","due":"","done":false,"tags":[]}""");
        File.WriteAllText(Path.Combine(export, "handled", "19a6fed1d02c18c9.json"), """{"at":1789794229459,"lastMsg":1789740479000}""");
        File.WriteAllText(Path.Combine(export, "slackHandled", "D0000000009@1792906568.844569.json"), """{"at":1793164858592,"lastTs":1793046270.298769}""");
        File.WriteAllText(Path.Combine(export, "slackHandled", "C0AK7Q3ABRA.json"), """{"at":1793164858592,"lastTs":1793046270.5}""");
        var repo = new DataRepository(Path.Combine(_dir, "repo"));

        var first = ArtifactImport.Import(export, repo, "skiddaw", Now);
        var again = ArtifactImport.Import(export, repo, "skiddaw", Now);

        first.ShouldBe(new ImportResult(3, 1, 2, 1));
        again.Todos.ShouldBe(0);
        var todos = repo.LoadTodos();
        TodoRules.OpenInOrder(todos).Select(t => t.Title).ShouldBe(["Ranked", "Unranked, due"]);
        todos.Single(t => t.Id == "tC").Source.ShouldBe(new TodoSource("slack", "D1~main"));
        todos.Single(t => t.Id == "tC").DoneAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1791000000000));
        repo.LoadHandled("email")["19a6fed1d02c18c9"].UpTo.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1789740479000).ToString("O"));
        repo.LoadHandled("slack")["D0000000009~1792906568.844569"].UpTo.ShouldBe("1793046270.298769");
        repo.LoadHandled("slack").ShouldContainKey("C0AK7Q3ABRA~main");
    }

    [Fact]
    public async Task Two_machines_sync_through_the_remote_and_a_conflict_is_set_aside()
    {
        var tools = new ToolLocator();
        var runner = new ProcessRunner(tools);
        var bare = Path.Combine(_dir, "remote.git");
        var git = new GitClient(runner, tools) { RepositoryPath = _dir };
        await git.RequireAsync(["init", "--bare", "--initial-branch=main", bare]);
        var url = new Uri(bare).AbsoluteUri;

        var a = new DataSync(runner, tools, Path.Combine(_dir, "a"), "pillar");
        var b = new DataSync(runner, tools, Path.Combine(_dir, "b"), "surface");
        (await a.EnsureClonedAsync(url)).ShouldBeNull();
        await Identify(runner, tools, a.RepositoryPath);
        var repoA = new DataRepository(a.RepositoryPath);
        repoA.Save(Task("t1"));
        (await a.SyncAsync("Add a task")).Ok.ShouldBeTrue();

        (await b.EnsureClonedAsync(url)).ShouldBeNull();
        await Identify(runner, tools, b.RepositoryPath);
        var repoB = new DataRepository(b.RepositoryPath);
        repoB.LoadTodos().ShouldHaveSingleItem().Id.ShouldBe("t1");

        // Different tasks on each machine: both survive.
        repoA.Save(Task("t2"));
        repoB.Save(Task("t3"));
        (await a.SyncAsync("t2")).Ok.ShouldBeTrue();
        (await b.SyncAsync("t3")).Ok.ShouldBeTrue();
        (await a.SyncAsync("pull")).Ok.ShouldBeTrue();
        repoA.LoadTodos().Select(t => t.Id).ShouldBe(["t1", "t2", "t3"], ignoreOrder: true);

        // The same task changed on both: the later push keeps its version on a set-aside branch.
        // Only the conflicting task is set aside: the Surface's other offline changes stay on the list.
        repoA.Save(Task("t1") with { Title = "Pillar's title" });
        repoB.Save(Task("t1") with { Title = "Surface's title" });
        repoB.Save(Task("t9") with { Title = "Added on the Surface offline" });
        repoB.Delete("t3");
        (await a.SyncAsync("edit")).Ok.ShouldBeTrue();
        var result = await b.SyncAsync("edit");

        result.Ok.ShouldBeTrue();
        result.SetAside.ShouldNotBeNull().ShouldStartWith("set-aside/surface-");
        var onB = repoB.LoadTodos();
        onB.Single(t => t.Id == "t1").Title.ShouldBe("Pillar's title");
        onB.Select(t => t.Id).ShouldContain("t9");
        onB.Select(t => t.Id).ShouldNotContain("t3");

        // And those changes reached the remote.
        (await a.SyncAsync("pull")).Ok.ShouldBeTrue();
        repoA.LoadTodos().Select(t => t.Id).ShouldBe(["t1", "t2", "t9"], ignoreOrder: true);
    }

    [Fact]
    public async Task Without_a_remote_a_change_stays_committed_locally()
    {
        var tools = new ToolLocator();
        var runner = new ProcessRunner(tools);
        var path = Path.Combine(_dir, "solo");
        Directory.CreateDirectory(path);
        await new GitClient(runner, tools) { RepositoryPath = path }.RequireAsync(["init", "--initial-branch=main"]);
        await Identify(runner, tools, path);
        await new GitClient(runner, tools) { RepositoryPath = path }.RequireAsync(["remote", "add", "origin", Path.Combine(_dir, "nowhere.git")]);
        new DataRepository(path).Save(Task());

        var result = await new DataSync(runner, tools, path, "skiddaw").SyncAsync("Add a task");

        result.Offline.ShouldBeTrue();
        result.Changed.ShouldBeTrue();
        (await new GitClient(runner, tools) { RepositoryPath = path }.RequireAsync(["log", "--oneline"])).ShouldContain("Add a task (skiddaw)");
    }

    private static async Task Identify(ProcessRunner runner, ToolLocator tools, string path)
    {
        var git = new GitClient(runner, tools) { RepositoryPath = path };
        await git.RequireAsync(["config", "user.email", "test@example.org"]);
        await git.RequireAsync(["config", "user.name", "Test"]);
        await git.RequireAsync(["config", "commit.gpgsign", "false"]);
    }
}
