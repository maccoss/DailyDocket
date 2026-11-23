using System.Text.Json;
using DailyDocket.Core.Processes;

namespace DailyDocket.Core.Setup;

/// <summary>
/// Looks for what DailyDocket needs from the computer (Claude Code, its sign-in, Git) and says what
/// to do about anything missing, for the window's banner.
/// </summary>
/// <remarks>
/// LabOps has a whole Setup page that installs these for its users; DailyDocket has one user who
/// can run a command, so it only names the command. The install commands are the official sources
/// LabOps uses: Anthropic's installer for Claude Code and winget for Git.
/// </remarks>
public sealed class SetupChecks
{
    internal const string ClaudeMissing =
        "Claude Code is not installed, so DailyDocket cannot sync. Install it in PowerShell with: irm https://claude.ai/install.ps1 | iex";

    // The same words JobRunner uses when a sync fails for this reason, so the banner reads the same
    // whichever noticed first.
    internal const string ClaudeSignedOut =
        "Claude Code is not signed in on this computer. Open a terminal and run: claude auth login --claudeai";

    internal const string GitMissing =
        "Git is not installed, so your to-do list cannot sync with your other computers. Install it in a terminal with: winget install --id Git.Git --exact --source winget";

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

    /// <summary>What is missing, most important first; empty when everything is in place.</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        if (_find(Tool.Claude) is not { } claude)
        {
            problems.Add(ClaudeMissing);
        }
        else if (await IsSignedInAsync(claude, cancellationToken).ConfigureAwait(false) == false)
        {
            problems.Add(ClaudeSignedOut);
        }

        if (_find(Tool.Git) is null)
        {
            problems.Add(GitMissing);
        }

        return problems;
    }

    /// <summary>The banner's text for <paramref name="problems"/>, or null when there are none.</summary>
    public static string? Describe(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? null : string.Join(" ", problems);

    /// <summary>
    /// True or false when <c>claude auth status</c> answered; null when it could not be asked (it
    /// timed out, or printed something other than its JSON). Unknown is not reported: a false
    /// "not signed in" would send Mike to sign in again for nothing, and a sync that fails for that
    /// reason says so anyway (JobRunner.Explain).
    /// </summary>
    private async Task<bool?> IsSignedInAsync(string claude, CancellationToken cancellationToken)
    {
        // It exits 1 when signed out, still printing the JSON, so the exit code is not checked.
        var result = await _runner.RunAsync(claude, ["auth", "status"], timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseLoggedIn(result.StandardOutput);
    }

    /// <summary>The <c>loggedIn</c> field of <c>claude auth status</c>, or null when it is not there.</summary>
    internal static bool? ParseLoggedIn(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("loggedIn", out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
