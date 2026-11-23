using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Processes;
using DailyDocket.Core.Setup;
using DailyDocket.Core.Updates;

namespace DailyDocket.Tests.Setup;

public sealed class SetupChecksTests
{
    /// <summary>Answers every command with one canned result, and records what was run.</summary>
    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null,
            IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default, Action<string>? onLine = null)
        {
            Commands.Add($"{fileName} {string.Join(' ', arguments)}");
            return Task.FromResult(result);
        }
    }

    private static Func<Tool, string?> Installed(params Tool[] tools) =>
        tool => tools.Contains(tool) ? $@"C:\tools\{tool}.exe" : null;

    private const string SignedIn = """{"loggedIn": true, "authMethod": "claude.ai", "email": "someone@example.org"}""";
    private const string SignedOut = """{"loggedIn": false}""";

    [Fact]
    public async Task Nothing_is_reported_when_everything_is_in_place()
    {
        var runner = new FakeRunner(new ProcessResult(0, SignedIn, ""));
        var problems = await new SetupChecks(runner, Installed(Tool.Claude, Tool.Git)).CheckAsync();

        problems.ShouldBeEmpty();
        SetupChecks.Describe(problems).ShouldBeNull();
        runner.Commands.ShouldBe([@"C:\tools\Claude.exe auth status"]);
    }

    [Fact]
    public async Task A_missing_Claude_Code_says_how_to_install_it_and_skips_the_sign_in_check()
    {
        var runner = new FakeRunner(new ProcessResult(0, SignedIn, ""));
        var problems = await new SetupChecks(runner, Installed(Tool.Git)).CheckAsync();

        problems.ShouldBe([SetupChecks.ClaudeMissing]);
        problems[0].ShouldContain("install.ps1");
        runner.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Signed_out_suggests_the_claude_ai_login()
    {
        // claude auth status exits 1 when signed out, and still prints its JSON.
        var runner = new FakeRunner(new ProcessResult(1, SignedOut, ""));
        var problems = await new SetupChecks(runner, Installed(Tool.Claude, Tool.Git)).CheckAsync();

        problems.ShouldBe([SetupChecks.ClaudeSignedOut]);
        problems[0].ShouldContain("claude auth login --claudeai");
    }

    [Fact]
    public async Task A_status_that_cannot_be_read_is_not_reported_as_signed_out()
    {
        var runner = new FakeRunner(new ProcessResult(-1, "", "claude did not finish within 00:00:30."));
        var problems = await new SetupChecks(runner, Installed(Tool.Claude, Tool.Git)).CheckAsync();

        problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_git_is_reported_with_its_install_command_after_Claude()
    {
        var runner = new FakeRunner(new ProcessResult(1, SignedOut, ""));
        var problems = await new SetupChecks(runner, Installed(Tool.Claude)).CheckAsync();

        problems.ShouldBe([SetupChecks.ClaudeSignedOut, SetupChecks.GitMissing]);
        problems[1].ShouldContain("winget install --id Git.Git");
        SetupChecks.Describe(problems).ShouldBe($"{SetupChecks.ClaudeSignedOut} {SetupChecks.GitMissing}");
    }

    [Theory]
    [InlineData("""{"loggedIn": true}""", true)]
    [InlineData("""{"loggedIn": false, "authMethod": "none"}""", false)]
    [InlineData("""{"authMethod": "none"}""", null)]
    [InlineData("""{"loggedIn": "yes"}""", null)]
    [InlineData("Logged in as someone@example.org", null)]
    [InlineData("[]", null)]
    [InlineData("", null)]
    public void The_status_is_read_from_loggedIn_only(string output, bool? expected) =>
        SetupChecks.ParseLoggedIn(output).ShouldBe(expected);
}

public sealed class UpdatePolicyTests
{
    [Theory]
    [InlineData("win-x64", false, "win-x64")]
    [InlineData("win-arm64", false, "win-arm64")]
    [InlineData("win-x64", true, "win-x64-beta")]
    [InlineData("win-arm64", true, "win-arm64-beta")]
    public void Each_runtime_reads_its_own_channel(string rid, bool beta, string expected) =>
        AppInfo.UpdateChannelFor(rid, beta).ShouldBe(expected);

    [Fact]
    public void This_build_reads_its_own_runtimes_channel()
    {
        AppInfo.UpdateChannel(beta: false).ShouldBe(AppInfo.RuntimeIdentifier);
        AppInfo.UpdateChannel(beta: true).ShouldBe(AppInfo.RuntimeIdentifier + "-beta");
    }

    [Theory]
    [InlineData(UpdateStage.Idle, true)]
    [InlineData(UpdateStage.UpToDate, true)]
    [InlineData(UpdateStage.Failed, true)]
    [InlineData(UpdateStage.Checking, false)]
    [InlineData(UpdateStage.Downloading, false)]
    [InlineData(UpdateStage.ReadyToApply, false)]
    [InlineData(UpdateStage.NotInstalled, false)]
    public void A_check_is_due_after_the_interval_unless_busy_staged_or_not_installed(UpdateStage stage, bool due)
    {
        var now = DateTimeOffset.Now;
        UpdatePolicy.IsCheckDue(new UpdateStatus(stage), now - TimeSpan.FromHours(5), now, UpdatePolicy.CheckInterval).ShouldBe(due);
    }

    [Fact]
    public void A_check_is_not_due_before_the_interval_but_is_before_the_first()
    {
        var now = DateTimeOffset.Now;
        var idle = new UpdateStatus(UpdateStage.UpToDate);
        UpdatePolicy.IsCheckDue(idle, now - TimeSpan.FromHours(1), now, UpdatePolicy.CheckInterval).ShouldBeFalse();
        UpdatePolicy.IsCheckDue(idle, null, now, UpdatePolicy.CheckInterval).ShouldBeTrue();
    }

    [Fact]
    public void Only_a_staged_update_shows_text_and_a_failed_check_stays_quiet()
    {
        UpdatePolicy.TextFor(new UpdateStatus(UpdateStage.ReadyToApply, "26.2.0", 100)).ShouldBe("Update 26.2.0 ready: restart to install");
        UpdatePolicy.TextFor(new UpdateStatus(UpdateStage.Failed, Error: "No such host is known.")).ShouldBeNull();
        UpdatePolicy.TextFor(new UpdateStatus(UpdateStage.Downloading, "26.2.0", 40)).ShouldBeNull();
        UpdatePolicy.TextFor(new UpdateStatus(UpdateStage.UpToDate)).ShouldBeNull();
    }

    [Fact]
    public void A_restart_that_left_the_update_waiting_says_why()
    {
        UpdatePolicy.InstallFailed(restartedByInstaller: true, updateStillWaiting: true).ShouldBeTrue();
        UpdatePolicy.InstallFailed(restartedByInstaller: true, updateStillWaiting: false).ShouldBeFalse();
        UpdatePolicy.InstallFailed(restartedByInstaller: false, updateStillWaiting: true).ShouldBeFalse();

        var message = UpdatePolicy.InstallFailedMessage("26.2.0");
        UpdatePolicy.TextFor(new UpdateStatus(UpdateStage.ReadyToApply, "26.2.0", 100, message)).ShouldBe(message);
        UpdatePolicy.NoteFor(new UpdateStatus(UpdateStage.ReadyToApply, "26.2.0", 100, message)).ShouldBe(message);
    }

    [Fact]
    public void The_upper_right_button_names_the_version_and_needs_no_note_when_all_is_well()
    {
        var ready = new UpdateStatus(UpdateStage.ReadyToApply, "26.1.2", 100);

        UpdatePolicy.ButtonFor(ready).ShouldBe("Restart to update to 26.1.2");
        UpdatePolicy.NoteFor(ready).ShouldBeNull();
        UpdatePolicy.NoteFor(new UpdateStatus(UpdateStage.Failed, Error: "No such host is known.")).ShouldBeNull();
    }
}
