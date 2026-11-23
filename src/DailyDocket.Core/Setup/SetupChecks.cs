using System.Text.Json;
using DailyDocket.Core.Processes;

namespace DailyDocket.Core.Setup;

/// <summary>One thing DailyDocket needs from the computer, in the order Setup lists them.</summary>
public enum SetupStep
{
    Claude,
    ClaudeSignIn,
    Git,
    GitHub,
    DataRepository,
}

/// <summary>What a Setup button does.</summary>
public enum SetupFix
{
    InstallClaude,
    SignInToClaude,
    InstallGit,
    InstallGitHubCli,
    SignInToGitHub,
    ConnectGit,
    DownloadData,
    SetGitName,
}

/// <summary>A command that needs a visible console, because it shows something or asks something.</summary>
/// <param name="Explanation">What Mike will see and do, shown before the console opens.</param>
public sealed record ConsoleCommand(string FileName, IReadOnlyList<string> Arguments, string Explanation);

/// <summary>The state of one setup step.</summary>
/// <param name="Fix">What the main button does, or null when there is nothing to press yet.</param>
/// <param name="Alternate">A second button, offered even once the step is done (sign in again).</param>
/// <param name="Unsure">It could not be checked (offline, a status that did not answer): not done, but not reported as missing either.</param>
public sealed record SetupItem(
    SetupStep Step, string Title, bool Done, string Detail,
    SetupFix? Fix = null, string? FixLabel = null, SetupFix? Alternate = null, string? AlternateLabel = null, bool Unsure = false)
{
    public bool CanFix => !Done && Fix is not null;

    public bool HasAlternate => Alternate is not null;

    /// <summary>Missing for certain: what the banner names and what opens Setup at startup.</summary>
    public bool NeedsAttention => !Done && !Unsure;
}

/// <summary>
/// Checks what DailyDocket needs from the computer (Claude Code and its sign-in, Git, a GitHub
/// sign-in git can use, the data repository) and offers a button for each thing missing, so a new
/// computer goes from the installer to a working app without a terminal.
/// </summary>
/// <remarks>
/// <para>
/// LabOps' Setup checklist, cut to what DailyDocket needs (November 2026). Installs use the
/// official sources only: Anthropic's installer for Claude Code, winget for Git and the GitHub
/// command-line tool. Claude's sign-in runs with no window (<see cref="Claude.ClaudeLogin"/>), since
/// its page sends the answer back by itself; GitHub's opens a console, because it shows a one-time
/// code to type in the browser. The app never handles a password.
/// </para>
/// <para>
/// The GitHub step is judged by whether git can read the data repository, the thing that matters,
/// rather than by whether gh is signed in: a computer whose git already has a credential (Git
/// Credential Manager, say) needs nothing more, and one where gh is signed in but git does not use
/// it still cannot sync.
/// </para>
/// </remarks>
public sealed class SetupChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly Func<Tool, string?> _find;

    public SetupChecks(IProcessRunner runner, ToolLocator tools)
        : this(runner, tools.Find)
    {
    }

    /// <summary>For tests: <paramref name="find"/> stands in for the file system search.</summary>
    internal SetupChecks(IProcessRunner runner, Func<Tool, string?> find)
    {
        _runner = runner;
        _find = find;
    }

    /// <param name="dataRepositoryUrl">The data repository to clone; empty when none is set.</param>
    /// <param name="repositoryPath">Where its clone lives.</param>
    public async Task<IReadOnlyList<SetupItem>> CheckAsync(string dataRepositoryUrl, string repositoryPath, CancellationToken cancellationToken = default)
    {
        var items = new List<SetupItem>();

        var claude = _find(Tool.Claude);
        items.Add(claude is not null
            ? new(SetupStep.Claude, "Claude Code", true, "Installed.")
            : new(SetupStep.Claude, "Claude Code", false, "DailyDocket reads your mail, calendar and Slack through Claude Code.",
                SetupFix.InstallClaude, "Install Claude Code"));
        items.Add(claude is null
            ? new(SetupStep.ClaudeSignIn, "Claude sign-in", false, "Install Claude Code first.", Unsure: true)
            : ClaudeSignInItem(await ClaudeStatusAsync(claude, cancellationToken).ConfigureAwait(false)));

        var git = _find(Tool.Git);
        items.Add(git is not null
            ? new(SetupStep.Git, "Git", true, "Installed.")
            : new(SetupStep.Git, "Git", false, "Git keeps your to-dos and Handled marks in step across your computers.",
                SetupFix.InstallGit, "Install Git"));

        var github = await GitHubItemAsync(git, dataRepositoryUrl, cancellationToken).ConfigureAwait(false);
        items.Add(github);
        items.Add(await DataItemAsync(git, github.Done, dataRepositoryUrl, repositoryPath, cancellationToken).ConfigureAwait(false));
        return items;
    }

    /// <summary>Everything needed is in place (steps that could not be checked do not count against it).</summary>
    public static bool AllDone(IEnumerable<SetupItem> items) => !items.Any(i => i.NeedsAttention);

    /// <summary>The banner's text, or null when nothing needs attention.</summary>
    public static string? Describe(IReadOnlyList<SetupItem> items)
    {
        var missing = items.Where(i => i.NeedsAttention).Select(i => i.Title).ToList();
        return missing.Count == 0 ? null : $"This computer is not fully set up ({string.Join(", ", missing)}). Press Setup to finish.";
    }

    /// <summary>The line under the checklist.</summary>
    public static string Summary(IReadOnlyList<SetupItem> items) =>
        !AllDone(items) ? "Work down the list; each button does one step."
        : items.All(i => i.Done) ? "Everything is ready."
        : "Everything is ready that could be checked. The rest is checked again when you open Setup.";

    internal static SetupItem ClaudeSignInItem(ClaudeAuth? auth)
    {
        if (auth is null)
        {
            // Not reported as signed out: that would send Mike to sign in again for nothing, and a
            // sync that fails for that reason says so anyway (JobRunner.Explain).
            return new(SetupStep.ClaudeSignIn, "Claude sign-in", false, "Claude Code did not say whether it is signed in.",
                SetupFix.SignInToClaude, "Sign in", Unsure: true);
        }

        // A session that expired still reports as signed in, so Sign in again stays on offer.
        return auth.LoggedIn
            ? new(SetupStep.ClaudeSignIn, "Claude sign-in", true,
                $"Signed in{(auth.Email is null ? "" : $" as {auth.Email}")}{(auth.OrgName is null ? "" : $" ({auth.OrgName})")}.",
                Alternate: SetupFix.SignInToClaude, AlternateLabel: "Sign in again")
            : new(SetupStep.ClaudeSignIn, "Claude sign-in", false,
                "Sign in with the Claude account whose Gmail, Google Calendar and Slack connectors DailyDocket uses.",
                SetupFix.SignInToClaude, "Sign in");
    }

    private async Task<SetupItem> GitHubItemAsync(string? git, string url, CancellationToken cancellationToken)
    {
        const string title = "GitHub sign-in";
        if (url.Length == 0)
        {
            return new(SetupStep.GitHub, title, false, "Choose a data repository first.", Unsure: true);
        }

        if (git is null)
        {
            return new(SetupStep.GitHub, title, false, "Install Git first.", Unsure: true);
        }

        var gh = _find(Tool.GitHubCli);
        var read = await _runner.RunAsync(git, ["ls-remote", "--quiet", url, "HEAD"], timeout: Timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (read.Succeeded)
        {
            return new(SetupStep.GitHub, title, true, $"Git can read {RepositoryName(url)}.",
                Alternate: gh is not null ? SetupFix.SignInToGitHub : null, AlternateLabel: gh is not null ? "Use a different account" : null);
        }

        var error = read.ErrorText;
        var refused = IsSignInRejected(error);
        var notFound = error.Contains("not found", StringComparison.OrdinalIgnoreCase);
        if (!refused && !notFound)
        {
            return new(SetupStep.GitHub, title, false, $"GitHub could not be reached to check: {FirstLine(error)}", Unsure: true);
        }

        if (gh is null)
        {
            return new(SetupStep.GitHub, title, false,
                "Sign in to GitHub so git can download your data repository. The GitHub command-line tool does the sign-in; install it first.",
                SetupFix.InstallGitHubCli, "Install GitHub CLI");
        }

        var signedIn = (await _runner.RunAsync(gh, ["auth", "status", "--hostname", "github.com"], timeout: Timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded;
        if (!signedIn)
        {
            return new(SetupStep.GitHub, title, false,
                "Sign in with the GitHub account that owns your data repository. A console shows a code to enter in the browser.",
                SetupFix.SignInToGitHub, "Sign in");
        }

        // gh is signed in, but git did not get in: either git is not using gh's sign-in, or the
        // account cannot see the repository. Connect fixes the first; the second needs another account.
        return new(SetupStep.GitHub, title, false,
            notFound
                ? $"GitHub did not let git see {RepositoryName(url)}. Connect makes git use your GitHub sign-in; if it still fails, sign in with the account that owns the repository."
                : "Git is not using your GitHub sign-in, so it cannot download your data repository. Connect fixes that.",
            SetupFix.ConnectGit, "Connect", SetupFix.SignInToGitHub, "Use a different account");
    }

    private async Task<SetupItem> DataItemAsync(string? git, bool canRead, string url, string repositoryPath, CancellationToken cancellationToken)
    {
        const string title = "Data repository";
        if (url.Length == 0)
        {
            return new(SetupStep.DataRepository, title, false,
                "Choose the private GitHub repository that holds your to-dos and settings. An empty one works; DailyDocket fills it.");
        }

        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
        {
            return new(SetupStep.DataRepository, title, false,
                $"{RepositoryName(url)} is not on this computer yet." + (canRead ? "" : " Complete the GitHub step first."),
                canRead ? SetupFix.DownloadData : null, canRead ? "Download" : null);
        }

        if (git is null)
        {
            return new(SetupStep.DataRepository, title, true, repositoryPath);
        }

        // A commit without a name fails ("Author identity unknown"), and a fresh Git has none.
        var name = await _runner.RunAsync(git, ["config", "user.name"], repositoryPath, timeout: Timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (name.Succeeded && name.StandardOutput.Trim().Length > 0)
        {
            return new(SetupStep.DataRepository, title, true, $"{repositoryPath}, saved as {name.StandardOutput.Trim()}.");
        }

        var gh = _find(Tool.GitHubCli);
        return new(SetupStep.DataRepository, title, false,
            gh is not null
                ? "Git has no name to save your changes under, so they cannot be saved. Use my GitHub name sets it for this repository."
                : "Git has no name to save your changes under. In a terminal run: git config --global user.name \"Your Name\"",
            gh is not null ? SetupFix.SetGitName : null, gh is not null ? "Use my GitHub name" : null);
    }

    /// <summary>The console command that does a step, or null for steps the app does itself.</summary>
    /// <remarks>
    /// Through PowerShell with single-quoted paths: cmd.exe strips the first and last quote of a
    /// /c command line, which breaks a path with a space in it, such as C:\Program Files\GitHub CLI\gh.exe.
    /// </remarks>
    public ConsoleCommand? ConsoleFix(SetupFix fix) => fix switch
    {
        SetupFix.InstallClaude => PowerShell(
            "irm https://claude.ai/install.ps1 | iex",
            "A console window runs Anthropic's Claude Code installer. Close it when it says the install finished."),
        SetupFix.SignInToClaude when _find(Tool.Claude) is { } claude => PowerShell(
            $"& {Quote(claude)} auth login --claudeai",
            "A console window opens and your browser opens Claude. Sign in, choose Authorize, then close the console window."),
        SetupFix.InstallGit => PowerShell(
            "winget install --id Git.Git --exact --source winget",
            "A console window installs Git with winget, Windows' own installer. Accept any prompts, then close it."),
        SetupFix.InstallGitHubCli => PowerShell(
            "winget install --id GitHub.cli --exact --source winget",
            "A console window installs the GitHub command-line tool with winget. Accept any prompts, then close it."),
        SetupFix.SignInToGitHub when _find(Tool.GitHubCli) is { } gh => PowerShell(
            $"& {Quote(gh)} auth login --hostname github.com --web --git-protocol https; if ($?) {{ & {Quote(gh)} auth setup-git --hostname github.com }}",
            "A console window shows a one-time code and your browser opens GitHub: enter the code and approve. "
            + "If GitHub shows an account that is not the one you want, sign out of GitHub in the browser first. Then close the console window."),
        _ => null,
    };

    /// <summary>Makes gh git's credential helper, so git uses the GitHub sign-in. It asks nothing, so it needs no console.</summary>
    public async Task ConnectGitAsync(CancellationToken cancellationToken = default)
    {
        var gh = _find(Tool.GitHubCli) ?? throw new ToolMissingException(Tool.GitHubCli);
        var result = await _runner.RunAsync(gh, ["auth", "setup-git", "--hostname", "github.com"], timeout: Timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Git could not be connected to your GitHub sign-in: {FirstLine(result.ErrorText)}");
        }
    }

    /// <summary>
    /// Sets the data repository's commit name from the GitHub account, with GitHub's private
    /// address for commits, so no personal address goes into the history.
    /// </summary>
    public async Task SetGitNameAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var gh = _find(Tool.GitHubCli) ?? throw new ToolMissingException(Tool.GitHubCli);
        var git = _find(Tool.Git) ?? throw new ToolMissingException(Tool.Git);
        var user = await _runner.RunAsync(gh, ["api", "user"], timeout: Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!user.Succeeded || ParseGitHubUser(user.StandardOutput) is not { } who)
        {
            throw new InvalidOperationException("Your GitHub account could not be read. Complete the GitHub step first.");
        }

        foreach (var (key, value) in new[] { ("user.name", who.Name), ("user.email", who.Email) })
        {
            var set = await _runner.RunAsync(git, ["config", "--local", key, value], repositoryPath, timeout: Timeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!set.Succeeded)
            {
                throw new InvalidOperationException($"Git could not be configured: {FirstLine(set.ErrorText)}");
            }
        }
    }

    /// <summary>A GitHub address in a form a person can check: owner/name.</summary>
    public static string RepositoryName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var parts = trimmed.Replace(':', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : url;
    }

    /// <summary>
    /// The address to clone for what Mike typed: a full https or ssh address as it is, or owner/name
    /// as a GitHub https address. Null when it is neither.
    /// </summary>
    public static string? NormalizeRepositoryUrl(string? typed)
    {
        var text = typed?.Trim() ?? "";
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var parts = text.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            ? $"https://github.com/{parts[0]}/{parts[1]}.git"
            : null;
    }

    /// <summary>True when git's error says GitHub wanted a sign-in it did not get.</summary>
    internal static bool IsSignInRejected(string error) =>
        error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
        || error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
        || error.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase);

    private async Task<ClaudeAuth?> ClaudeStatusAsync(string claude, CancellationToken cancellationToken)
    {
        // It exits 1 when signed out, still printing the JSON, so the exit code is not checked.
        var result = await _runner.RunAsync(claude, ["auth", "status"], timeout: Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseClaudeAuth(result.StandardOutput);
    }

    /// <summary>What <c>claude auth status</c> printed, or null when it is not its JSON.</summary>
    internal static ClaudeAuth? ParseClaudeAuth(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new ClaudeAuth(loggedIn.GetBoolean(), Text("email"), Text("orgName"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static (string Name, string Email)? ParseGitHubUser(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("login", out var login) || login.GetString() is not { Length: > 0 } l
                || !root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number))
            {
                return null;
            }

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s ? s : l;
            return (name, $"{number}+{l}@users.noreply.github.com");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ConsoleCommand PowerShell(string script, string explanation) => new(
        "powershell.exe",
        ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script + "; Read-Host 'Done. Press Enter to close this window'"],
        explanation);

    private static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    internal sealed record ClaudeAuth(bool LoggedIn, string? Email, string? OrgName);
}
