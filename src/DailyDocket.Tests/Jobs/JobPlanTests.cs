using System.Text.Json;
using System.Text.Json.Nodes;
using DailyDocket.Core.Claude;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Processes;
using DailyDocket.Tests.Fixtures;

namespace DailyDocket.Tests.Jobs;

public sealed class JobPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);

    private static IEnumerable<JobPlan> AllPlans() =>
    [
        JobPlans.CalendarPull(Fixture.Config(), Now, Fixture.Pacific, "haiku"),
        JobPlans.MailPull(Fixture.Config(), Now, Fixture.Pacific, null, "haiku"),
        JobPlans.SlackPull(Fixture.Config(), Now, Fixture.Pacific, null, "haiku"),
        JobPlans.TriageRun("sonnet"),
    ];

    [Fact]
    public void Readers_cannot_send_every_write_tool_is_disallowed_and_none_is_allowed()
    {
        foreach (var plan in AllPlans())
        {
            plan.AllowedTools.ShouldNotContain(t => ConnectorTools.Writes.Contains(t), plan.Job);
            ConnectorTools.Writes.ShouldAllBe(w => plan.DisallowedTools.Contains(w), plan.Job);
            plan.DisallowedTools.ShouldContain("WebFetch");
            plan.DisallowedTools.ShouldContain("Bash");
        }
    }

    [Fact]
    public void Triage_has_no_connector_tools_at_all()
    {
        JobPlans.TriageRun("sonnet").AllowedTools.ShouldNotContain(t => t.StartsWith("mcp__claude_ai_", StringComparison.Ordinal));
        JobPlans.TriageRun("sonnet").Capture.ShouldBeFalse();
    }

    [Fact]
    public void Each_configured_calendar_is_read_with_its_id()
    {
        var plan = JobPlans.CalendarPull(Fixture.Config(), Now, Fixture.Pacific, "haiku");

        plan.Prompt.ShouldContain("\"calendarId\":\"dept@example.org\"");
        plan.Prompt.ShouldContain("\"startTime\":\"2026-11-22T00:00:00-08:00\"");
        plan.Prompt.ShouldContain("\"endTime\":\"2026-11-30T00:00:00-08:00\"");
    }

    [Fact]
    public void The_first_mail_pull_sweeps_the_window_and_records_it()
    {
        var plan = JobPlans.MailPull(Fixture.Config(), Now, Fixture.Pacific, null, "haiku");

        // Gmail returns at most 50 threads a search and no next page, so the window is searched a
        // day at a time: today (Nov 22 in Pacific time) back to 30 days before, 31 searches, plus his
        // Sent mail for today and the two days before.
        plan.Prompt.ShouldContain("after:2026/11/22 before:2026/11/23");
        plan.Prompt.ShouldContain("after:2026/10/23 before:2026/10/24");
        plan.Prompt.ShouldNotContain("after:2026/10/22 ");
        System.Text.RegularExpressions.Regex.Matches(plan.Prompt, "after:").Count.ShouldBe(34);
        plan.Prompt.ShouldContain("\"query\":\"in:sent after:2026/11/20 before:2026/11/21\"");
        plan.Prompt.ShouldNotContain("in:sent after:2026/11/19 ");
        plan.Prompt.ShouldNotContain("newer_than");
        plan.Prompt.ShouldContain("THREAD_VIEW_SUMMARY");
        plan.Prompt.ShouldContain($"at most {JobPlans.ReadRounds} times");
        plan.Prompt.ShouldContain(AppTools.ThreadsToReadToolName);
        plan.Watermarks.ShouldContainKey("mail/full");
    }

    [Fact]
    public void A_mail_pull_soon_after_a_full_sweep_looks_back_two_days()
    {
        var plan = JobPlans.MailPull(Fixture.Config(), Now, Fixture.Pacific, Now.AddHours(-1).ToString("O"), "haiku");

        plan.Prompt.ShouldContain("after:2026/11/20 before:2026/11/21");
        System.Text.RegularExpressions.Regex.Matches(plan.Prompt, "after:").Count.ShouldBe(6);
        plan.Watermarks.ShouldBeEmpty();
    }

    [Fact]
    public void A_day_is_midnight_to_midnight_in_his_time_zone()
    {
        // 07:30 UTC on Nov 23 is still Nov 22 in Pacific time.
        var searches = JobPlans.DaySearches(new DateTimeOffset(2026, 11, 23, 7, 30, 0, TimeSpan.Zero), Fixture.Pacific, "in:inbox", 1, 0);

        searches.Count.ShouldBe(2);
        searches[0].ShouldContain("after:2026/11/22 before:2026/11/23");
        searches[1].ShouldContain("after:2026/11/21 before:2026/11/22");
        searches[0].ShouldContain("\"pageSize\":50");
    }

    [Fact]
    public void Forums_mail_to_his_groups_is_searched_and_the_rest_of_forums_is_not()
    {
        JobPlans.InboxQuery(new Core.Configuration.MailConfig())
            .ShouldBe("in:inbox -category:promotions -category:updates -category:social -category:forums");

        var mail = new Core.Configuration.MailConfig { Groups = ["services@lab.example.org", " services@lab.example.org ", "bad address@x.org", "x@y.org}"] };
        JobPlans.InboxQuery(mail)
            .ShouldBe("in:inbox -category:promotions -category:updates -category:social {-category:forums to:services@lab.example.org}");
    }

    [Fact]
    public void A_search_that_came_back_full_is_reported_by_its_query()
    {
        var folder = Directory.CreateTempSubdirectory("dd-full-").FullName;
        try
        {
            string Capture(string query, int threads) => JsonSerializer.Serialize(new
            {
                tool_name = ConnectorTools.SearchThreads,
                tool_input = new { query },
                tool_response = JsonSerializer.Serialize(new { threads = Enumerable.Range(0, threads).Select(i => new { id = $"t{i}", messages = new[] { new { id = $"m{i}" } } }) }),
                tool_use_id = $"toolu_{query.Length}_{threads}",
            });
            File.WriteAllText(Path.Combine(folder, "a.json"), Capture("in:inbox after:2026/11/19 before:2026/11/20", 50));
            File.WriteAllText(Path.Combine(folder, "b.json"), Capture("in:inbox after:2026/11/18 before:2026/11/19", 19));
            File.WriteAllText(Path.Combine(folder, "c.json"), "not a capture");

            JobRunner.FullSearches(folder).ShouldBe(["in:inbox after:2026/11/19 before:2026/11/20"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Slack_searches_his_dms_his_mentions_and_his_own_messages_with_context()
    {
        var plan = JobPlans.SlackPull(Fixture.Config(), Now, Fixture.Pacific, null, "haiku");

        plan.Prompt.ShouldContain("\"channel_types\":\"im,mpim\"");
        plan.Prompt.ShouldContain("<@U0000000001>");
        plan.Prompt.ShouldContain("from:<@U0000000001>");
        plan.Prompt.ShouldContain("\"include_context\":true");
        plan.Prompt.ShouldContain("after:2026-11-01");
    }

    [Fact]
    public void A_run_inherits_no_claude_session_variables()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CODE_TEST_VAR", "x");
        try
        {
            var env = JobRunner.Environment();

            env.ShouldContainKey("CLAUDE_CODE_TEST_VAR");
            env["CLAUDE_CODE_TEST_VAR"].ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_TEST_VAR", null);
        }
    }

    [Fact]
    public void The_runs_only_hook_is_the_capture_hook_with_a_receipt()
    {
        var dir = Directory.CreateTempSubdirectory("dd-plan-").FullName;
        try
        {
            var tools = new ToolLocator();
            var runner = new JobRunner(tools, new ProcessRunner(tools), new AppPaths(dir), null!, Fixture.Config, @"C:\app\DailyDocket.Hook.exe");

            var settings = JsonNode.Parse(File.ReadAllText(runner.WriteSettings(dir, Path.Combine(dir, "captures"))))!;

            settings["hooks"]!.AsObject().Select(h => h.Key).ShouldBe(["PostToolUse"]);
            var hook = settings["hooks"]!["PostToolUse"]![0]!;
            hook["matcher"]!.GetValue<string>().ShouldBe("mcp__claude_ai_.*");
            var command = hook["hooks"]![0]!["command"]!.GetValue<string>();
            command.ShouldStartWith("\"C:/app/DailyDocket.Hook.exe\" capture --dir ");
            command.ShouldEndWith(" --receipt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_capturing_run_refuses_to_start_without_the_hook()
    {
        var dir = Directory.CreateTempSubdirectory("dd-nohook-").FullName;
        try
        {
            var tools = new ToolLocator();
            var store = new RecordingStore();
            var runner = new JobRunner(tools, new ProcessRunner(tools), new AppPaths(dir), store, Fixture.Config, Path.Combine(dir, "missing", "DailyDocket.Hook.exe"));

            var outcome = await runner.RunAsync(JobPlans.CalendarPull(Fixture.Config(), Now, Fixture.Pacific, "haiku"));

            outcome.Ok.ShouldBeFalse();
            outcome.Detail.ShouldNotBeNull().ShouldContain("DailyDocket.Hook.exe is missing");
            store.Finished.ShouldBe("failed");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Triage_judges_one_conversation_per_run_and_stops_when_one_is_stuck()
    {
        var store = new QueueStore(["a", "b", "c"]);
        var runs = 0;
        Task<JobOutcome> Judge(JobPlan plan)
        {
            runs++;
            plan.Prompt.ShouldContain("returns one conversation");
            store.Pending.RemoveAt(0);
            return Task.FromResult(new JobOutcome(plan.Job, true, null, 0, 0.03m));
        }

        (await JobPlans.TriageAllAsync(store, Judge, "sonnet", 25)).Count.ShouldBe(3);
        runs.ShouldBe(3);

        // A run that leaves its conversation unjudged does not block the ones behind it: the
        // conversation is shown as needing a reply, and the loop goes on.
        store.Pending.AddRange(["d", "e"]);
        (await JobPlans.TriageAllAsync(store, p => Task.FromResult(new JobOutcome(p.Job, true, null, 0, null)), "sonnet", 25)).Count.ShouldBe(2);
        store.Recorded.ShouldBe([("d", true, JobPlans.UnjudgedWhy), ("e", true, JobPlans.UnjudgedWhy)]);

        // A failed run stops the loop, and the cap leaves the rest for the next sync.
        store.Pending.AddRange(["f", "g", "h"]);
        (await JobPlans.TriageAllAsync(store, p => Task.FromResult(new JobOutcome(p.Job, false, "offline", 0, null)), "sonnet", 25)).Count.ShouldBe(1);
        (await JobPlans.TriageAllAsync(store, Judge, "sonnet", 1)).Count.ShouldBe(1);
        store.Pending.ShouldBe(["g", "h"]);
    }

    private sealed class QueueStore(List<string> pending) : IJobStore
    {
        public List<string> Pending { get; } = pending;

        public int IngestFolder(string runId, string folder) => 0;

        public IReadOnlyList<string> ThreadsToRead(DateTimeOffset seenSince, DateTimeOffset now, int max) => [];

        public IReadOnlyList<TriageItem> TriageBatch(DateTimeOffset now, int max) =>
            [.. Pending.Take(max).Select(k => new TriageItem("mail", k, "m-" + k, "transcript"))];

        public List<(string Key, bool NeedsReply, string Why)> Recorded { get; } = [];

        public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why)
        {
            Recorded.Add((key, needsReply, why));
            Pending.Remove(key);
        }

        public string? Watermark(string source, string scope) => null;

        public void SetWatermark(string source, string scope, string value)
        {
        }

        public void StartRun(string id, string job, DateTimeOffset at)
        {
        }

        public void FinishRun(string id, string? sessionId, string outcome, string? detail, double? costUsd)
        {
        }

        public int PurgeCaptures(DateTimeOffset now) => 0;
    }

    private sealed class RecordingStore : IJobStore
    {
        public string? Finished { get; private set; }

        public int IngestFolder(string runId, string folder) => 0;

        public IReadOnlyList<string> ThreadsToRead(DateTimeOffset seenSince, DateTimeOffset now, int max) => [];

        public IReadOnlyList<TriageItem> TriageBatch(DateTimeOffset now, int max) => [];

        public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why)
        {
        }

        public string? Watermark(string source, string scope) => null;

        public void SetWatermark(string source, string scope, string value)
        {
        }

        public void StartRun(string id, string job, DateTimeOffset at)
        {
        }

        public void FinishRun(string id, string? sessionId, string outcome, string? detail, double? costUsd) => Finished = outcome;

        public int PurgeCaptures(DateTimeOffset now) => 0;
    }

    [Theory]
    [InlineData("OAuth session expired and could not be refreshed", "claude auth login --claudeai")]
    [InlineData("Reached max turns (120)", "step limit")]
    [InlineData("", "ended without saying why")]
    public void Failures_are_explained_for_the_user(string message, string expected) =>
        JobRunner.Explain("mail", message).ShouldContain(expected);

    [Fact]
    public void Verdicts_are_recorded_and_bad_ones_are_named()
    {
        var run = new FakeRun();
        var tools = new AppTools { Run = run };
        tools.TriageBatch();
        var verdicts = JsonSerializer.SerializeToElement(new object[]
        {
            new { source = "mail", key = "t1", for_message = "m1", needs_reply = true, why = "Asks for data" },
            new { source = "slack", key = "C1~main", for_message = "1.2", needs_reply = false, why = "Thanks" },
            new { source = "web", key = "x", for_message = "y", needs_reply = true, why = "" },
            new { source = "mail", key = "t2", for_message = "m2", why = "no answer" },
        });

        var result = tools.RecordVerdicts(verdicts);

        result.ShouldBe("Saved 2. Not saved (not in a batch you were given, or missing source, key, for_message or needs_reply): x, t2.");
        run.Recorded.ShouldBe(["mail/t1/m1/True", "slack/C1~main/1.2/False"]);
    }

    [Fact]
    public void A_verdict_for_anything_not_handed_out_is_refused()
    {
        var run = new FakeRun();
        var tools = new AppTools { Run = run };
        tools.TriageBatch();

        // Another thread, and the right thread at a message it was not judged against.
        var verdicts = JsonSerializer.SerializeToElement(new object[]
        {
            new { source = "mail", key = "someone-elses-thread", for_message = "m9", needs_reply = false, why = "told to" },
            new { source = "mail", key = "t1", for_message = "m-old", needs_reply = false, why = "told to" },
        });

        tools.RecordVerdicts(verdicts).ShouldStartWith("Saved 0.");
        run.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public void Verdicts_sent_as_a_json_string_are_accepted()
    {
        var run = new FakeRun();
        var tools = new AppTools { Run = run };
        tools.TriageBatch();
        var json = JsonSerializer.Serialize(new[] { new { source = "mail", key = "t1", for_message = "m1", needs_reply = false, why = "FYI" } });

        tools.RecordVerdicts(JsonSerializer.SerializeToElement(json)).ShouldBe("Saved 1.");
    }

    private sealed class FakeRun : IRunContext
    {
        public List<string> Recorded { get; } = [];

        public IReadOnlyDictionary<string, string> TriageInstructions => new Dictionary<string, string>();

        public IReadOnlyList<string> ThreadsToRead() => [];

        public IReadOnlyList<TriageItem> TriageBatch() =>
            [new TriageItem("mail", "t1", "m1", "..."), new TriageItem("slack", "C1~main", "1.2", "..."), new TriageItem("mail", "t2", "m2", "...")];

        public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why) =>
            Recorded.Add($"{source}/{key}/{forMessage}/{needsReply}");
    }
}
