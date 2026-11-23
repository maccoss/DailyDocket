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
        JobPlans.MailPull(Fixture.Config(), Now, null, "haiku"),
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
        var plan = JobPlans.MailPull(Fixture.Config(), Now, null, "haiku");

        plan.Prompt.ShouldContain("newer_than:30d");
        plan.Prompt.ShouldContain("THREAD_VIEW_SUMMARY");
        plan.Prompt.ShouldContain(AppTools.ThreadsToReadToolName);
        plan.Watermarks.ShouldContainKey("mail/full");
    }

    [Fact]
    public void A_mail_pull_soon_after_a_full_sweep_looks_back_two_days()
    {
        var plan = JobPlans.MailPull(Fixture.Config(), Now, Now.AddHours(-1).ToString("O"), "haiku");

        plan.Prompt.ShouldContain("newer_than:2d");
        plan.Watermarks.ShouldBeEmpty();
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
