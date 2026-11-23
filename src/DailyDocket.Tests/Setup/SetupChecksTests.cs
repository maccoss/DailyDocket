using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Processes;
using DailyDocket.Core.Setup;
using DailyDocket.Core.Updates;

namespace DailyDocket.Tests.Setup;

public sealed class SetupChecksTests : IDisposable
{
    private const string Url = "https://github.com/someone/Docket-Data.git";
    private const string SignedIn = """{"loggedIn": true, "authMethod": "claude.ai", "email": "someone@example.org"}""";
    private const string SignedOut = """{"loggedIn": false}""";

    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"dd-setup-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repo))
        {
            Directory.Delete(_repo, recursive: true);
        }
    }

    /// <summary>Answers each command by its first matching rule (a piece of the command line), and records what was run.</summary>
    private sealed class FakeRunner(params (string Contains, ProcessResult Result)[] rules) : IProcessRunner
    {
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null,
            IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default, Action<string>? onLine = null)
        {
            var line = $"{fileName} {string.Join(' ', arguments)}";
            Commands.Add(line);
            var match = rules.FirstOrDefault(r => line.Contains(r.Contains, StringComparison.Ordinal));
            return Task.FromResult(match.Result ?? new ProcessResult(0, "", ""));
        }
    }

    private static ProcessResult Ok(string output = "") => new(0, output, "");

    private static ProcessResult Fail(string error) => new(128, "", error);

    private static Func<Tool, string?> Installed(params Tool[] tools) =>
        tool => tools.Contains(tool) ? $@"C:\tools\{tool}.exe" : null;

    private static readonly Tool[] All = [Tool.Claude, Tool.Git, Tool.GitHubCli];

    private void Clone() => Directory.CreateDirectory(Path.Combine(_repo, ".git"));

    private async Task<IReadOnlyList<SetupItem>> Check(FakeRunner runner, params Tool[] tools) =>
        await new SetupChecks(runner, Installed(tools)).CheckAsync(Url, _repo);

    private static SetupItem Row(IReadOnlyList<SetupItem> items, SetupStep step) => items.Single(i => i.Step == step);

    [Fact]
    public async Task Nothing_needs_attention_when_everything_is_in_place()
    {
        Clone();
        var runner = new FakeRunner(("auth status", Ok(SignedIn)), ("user.name", Ok("Someone\n")));
        var items = await Check(runner, All);

        items.ShouldAllBe(i => i.Done);
        SetupChecks.AllDone(items).ShouldBeTrue();
        SetupChecks.Describe(items).ShouldBeNull();
        Row(items, SetupStep.ClaudeSignIn).Detail.ShouldContain("someone@example.org");
        Row(items, SetupStep.ClaudeSignIn).AlternateLabel.ShouldBe("Sign in again");
        runner.Commands.ShouldContain(@$"C:\tools\Git.exe ls-remote --quiet {Url} HEAD");
    }

    [Fact]
    public async Task A_missing_Claude_Code_offers_Anthropics_installer_and_skips_the_sign_in_check()
    {
        Clone();
        var runner = new FakeRunner(("user.name", Ok("Someone")));
        var checks = new SetupChecks(runner, Installed(Tool.Git));
        var items = await checks.CheckAsync(Url, _repo);

        Row(items, SetupStep.Claude).Fix.ShouldBe(SetupFix.InstallClaude);
        Row(items, SetupStep.ClaudeSignIn).Unsure.ShouldBeTrue();
        runner.Commands.ShouldNotContain(c => c.Contains("auth status"));
        string.Join(' ', checks.ConsoleFix(SetupFix.InstallClaude)!.Arguments).ShouldContain("irm https://claude.ai/install.ps1 | iex");
        SetupChecks.Describe(items).ShouldBe("This computer is not fully set up (Claude Code). Press Setup to finish.");
    }

    [Fact]
    public async Task Signed_out_offers_the_claude_ai_sign_in()
    {
        Clone();
        var checks = new SetupChecks(new FakeRunner(("auth status", new ProcessResult(1, SignedOut, "")), ("user.name", Ok("Someone"))), Installed(All));
        var items = await checks.CheckAsync(Url, _repo);

        Row(items, SetupStep.ClaudeSignIn).Fix.ShouldBe(SetupFix.SignInToClaude);
        Row(items, SetupStep.ClaudeSignIn).NeedsAttention.ShouldBeTrue();
        string.Join(' ', checks.ConsoleFix(SetupFix.SignInToClaude)!.Arguments).ShouldContain("auth login --claudeai");
    }

    [Fact]
    public async Task A_status_that_cannot_be_read_is_not_reported_as_signed_out()
    {
        Clone();
        var items = await Check(new FakeRunner(("auth status", new ProcessResult(-1, "", "claude did not finish within 00:00:30.")), ("user.name", Ok("Someone"))), All);

        Row(items, SetupStep.ClaudeSignIn).Unsure.ShouldBeTrue();
        SetupChecks.Describe(items).ShouldBeNull();
    }

    [Fact]
    public async Task Missing_git_offers_winget_and_leaves_the_GitHub_check_for_later()
    {
        var checks = new SetupChecks(new FakeRunner(("auth status", Ok(SignedIn))), Installed(Tool.Claude));
        var items = await checks.CheckAsync(Url, _repo);

        Row(items, SetupStep.Git).Fix.ShouldBe(SetupFix.InstallGit);
        Row(items, SetupStep.GitHub).Unsure.ShouldBeTrue();
        string.Join(' ', checks.ConsoleFix(SetupFix.InstallGit)!.Arguments).ShouldContain("winget install --id Git.Git --exact --source winget");
    }

    [Fact]
    public async Task A_refused_read_without_gh_offers_to_install_it()
    {
        var items = await Check(new FakeRunner(("auth status", Ok(SignedIn)), ("ls-remote", Fail("fatal: could not read Username for 'https://github.com': terminal prompts disabled"))),
            Tool.Claude, Tool.Git);

        Row(items, SetupStep.GitHub).Fix.ShouldBe(SetupFix.InstallGitHubCli);
        Row(items, SetupStep.DataRepository).CanFix.ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_read_with_gh_signed_out_offers_the_GitHub_sign_in()
    {
        var runner = new FakeRunner(("Claude.exe auth status", Ok(SignedIn)), ("ls-remote", Fail("remote: Repository not found.")),
            ("GitHubCli.exe auth status", Fail("You are not logged into any GitHub hosts.")));
        var checks = new SetupChecks(runner, Installed(All));
        var items = await checks.CheckAsync(Url, _repo);

        Row(items, SetupStep.GitHub).Fix.ShouldBe(SetupFix.SignInToGitHub);
        string.Join(' ', checks.ConsoleFix(SetupFix.SignInToGitHub)!.Arguments).ShouldContain("auth setup-git");
    }

    [Fact]
    public async Task A_refused_read_with_gh_signed_in_offers_Connect()
    {
        var items = await Check(new FakeRunner(("ls-remote", Fail("fatal: Authentication failed for 'https://github.com/someone/Docket-Data.git/'")),
            ("auth status", Ok(SignedIn))), All);

        Row(items, SetupStep.GitHub).Fix.ShouldBe(SetupFix.ConnectGit);
        Row(items, SetupStep.GitHub).Alternate.ShouldBe(SetupFix.SignInToGitHub);
    }

    [Fact]
    public async Task Offline_is_not_reported_as_missing()
    {
        var items = await Check(new FakeRunner(("auth status", Ok(SignedIn)),
            ("ls-remote", Fail("fatal: unable to access 'https://github.com/someone/Docket-Data.git/': Could not resolve host: github.com"))), All);

        Row(items, SetupStep.GitHub).Unsure.ShouldBeTrue();
        Row(items, SetupStep.GitHub).NeedsAttention.ShouldBeFalse();
    }

    [Fact]
    public async Task A_readable_repository_not_yet_here_offers_Download()
    {
        var items = await Check(new FakeRunner(("auth status", Ok(SignedIn))), All);

        Row(items, SetupStep.DataRepository).Fix.ShouldBe(SetupFix.DownloadData);
        Row(items, SetupStep.DataRepository).Detail.ShouldContain("someone/Docket-Data");
    }

    [Fact]
    public async Task A_clone_without_a_git_name_offers_the_GitHub_name()
    {
        Clone();
        var items = await Check(new FakeRunner(("auth status", Ok(SignedIn)), ("user.name", new ProcessResult(1, "", ""))), All);

        Row(items, SetupStep.DataRepository).Fix.ShouldBe(SetupFix.SetGitName);
    }

    [Theory]
    [InlineData("someone/Docket-Data", "https://github.com/someone/Docket-Data.git")]
    [InlineData(" https://github.com/someone/Docket-Data ", "https://github.com/someone/Docket-Data")]
    [InlineData("git@github.com:someone/Docket-Data.git", "git@github.com:someone/Docket-Data.git")]
    [InlineData("Docket-Data", null)]
    [InlineData("some one/data", null)]
    [InlineData("", null)]
    public void What_is_typed_becomes_an_address_to_clone(string typed, string? expected) =>
        SetupChecks.NormalizeRepositoryUrl(typed).ShouldBe(expected);

    [Theory]
    [InlineData("https://github.com/someone/Docket-Data.git", "someone/Docket-Data")]
    [InlineData("https://github.com/someone/Docket-Data/", "someone/Docket-Data")]
    [InlineData("git@github.com:someone/Docket-Data.git", "someone/Docket-Data")]
    public void A_repository_is_named_owner_slash_name(string url, string expected) =>
        SetupChecks.RepositoryName(url).ShouldBe(expected);

    [Fact]
    public void The_git_name_uses_GitHubs_private_address()
    {
        SetupChecks.ParseGitHubUser("""{"login":"someone","id":42,"name":"Some One"}""").ShouldBe(("Some One", "42+someone@users.noreply.github.com"));
        SetupChecks.ParseGitHubUser("""{"login":"someone","id":42,"name":null}""").ShouldBe(("someone", "42+someone@users.noreply.github.com"));
        SetupChecks.ParseGitHubUser("not json").ShouldBeNull();
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
        (SetupChecks.ParseClaudeAuth(output)?.LoggedIn).ShouldBe(expected);
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
