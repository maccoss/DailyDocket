using System.Globalization;
using DailyDocket.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Sync;

/// <summary>How a sync of the data repository ended.</summary>
/// <param name="SetAside">A local branch holding this machine's version when it could not be replayed on top of the others'.</param>
public sealed record DataSyncResult(bool Ok, bool Offline, bool Changed, string? SetAside, string? Problem);

/// <summary>
/// Keeps the data repository (to-dos, Handled marks, config) in step across machines.
/// </summary>
/// <remarks>
/// <para>
/// LabOps' rule, kept small: commit to main, rebase onto the remote before pushing, never merge
/// and never force-push. Git works offline, so a change is committed at once and pushed whenever
/// the network allows; nothing typed is ever lost to a failed sync.
/// </para>
/// <para>
/// Files are one per item, so two machines conflict only when both changed the same task offline.
/// Then this machine's commits go to a local set-aside/... branch, main takes the remote's
/// version, and the window says so (the design's "later wins, the other is kept").
/// </para>
/// </remarks>
public sealed class DataSync
{
    private readonly GitClient _git;
    private readonly string _machine;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DataSync(IProcessRunner runner, ToolLocator tools, string repositoryPath, string machine, ILogger<DataSync>? log = null)
    {
        _git = new GitClient(runner, tools) { RepositoryPath = repositoryPath };
        _machine = machine;
        _log = log ?? (ILogger)NullLogger.Instance;
        RepositoryPath = repositoryPath;
    }

    public string RepositoryPath { get; }

    public bool IsCloned => Directory.Exists(Path.Combine(RepositoryPath, ".git"));

    /// <summary>Clones <paramref name="url"/> into the repository folder if it is not there yet.</summary>
    public async Task<string?> EnsureClonedAsync(string url, CancellationToken cancellationToken = default)
    {
        if (IsCloned)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(RepositoryPath)!;
        Directory.CreateDirectory(parent);
        if (Directory.Exists(RepositoryPath) && Directory.EnumerateFileSystemEntries(RepositoryPath).Any())
        {
            return $"{RepositoryPath} already has files but is not a git clone. Move them aside so DailyDocket can clone your data repository there.";
        }

        var result = await new GitClientAt(_git, parent).RunAsync(["clone", url, RepositoryPath], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return $"DailyDocket could not clone {url}: {FirstLine(result.ErrorText)} Check that you are signed in to GitHub (gh auth login) and can open the repository.";
        }

        await _git.RunAsync(["config", "pull.rebase", "true"], cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Commits whatever changed, then rebases onto the remote and pushes.</summary>
    public async Task<DataSyncResult> SyncAsync(string summary, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCloned)
            {
                return new DataSyncResult(false, false, false, null, "The data repository is not set up yet.");
            }

            if (_git.RebaseInProgress())
            {
                await _git.RunAsync(["rebase", "--abort"], cancellationToken).ConfigureAwait(false);
            }

            var committed = await CommitAsync(summary, cancellationToken).ConfigureAwait(false);

            var fetch = await _git.RunAsync(["fetch", "origin"], cancellationToken).ConfigureAwait(false);
            if (!fetch.Succeeded)
            {
                // Offline is normal: the commit waits for the next sync.
                _log.LogInformation("Data sync could not reach GitHub; changes stay committed locally");
                return new DataSyncResult(true, true, committed, null, null);
            }

            var before = (await _git.RunAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
            string? setAside = null;

            // A brand-new remote has no main yet: the first push creates it.
            var remoteHasMain = (await _git.RunAsync(["rev-parse", "--verify", "--quiet", "origin/main"], cancellationToken).ConfigureAwait(false)).Succeeded;
            var rebase = remoteHasMain
                ? await _git.RunAsync(["rebase", "origin/main"], cancellationToken).ConfigureAwait(false)
                : null;
            if (rebase is { Succeeded: false })
            {
                await _git.RunAsync(["rebase", "--abort"], cancellationToken).ConfigureAwait(false);
                setAside = $"set-aside/{_machine}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
                await _git.RequireAsync(["branch", setAside], cancellationToken).ConfigureAwait(false);
                await _git.RequireAsync(["reset", "--hard", "origin/main"], cancellationToken).ConfigureAwait(false);
                _log.LogWarning("Data sync conflict; this machine's version kept on {Branch}", setAside);
            }

            var push = await _git.RunAsync(["push", "origin", "HEAD:main"], cancellationToken).ConfigureAwait(false);
            if (!push.Succeeded)
            {
                return new DataSyncResult(false, false, true, setAside, $"Your to-do changes are saved on this computer but could not be sent to GitHub: {FirstLine(push.ErrorText)}");
            }

            var after = (await _git.RequireAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).Trim();
            return new DataSyncResult(true, false, committed || before != after, setAside, null);
        }
        catch (GitException e)
        {
            return new DataSyncResult(false, false, false, null, $"Syncing the to-do list failed: {FirstLine(e.Message)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> CommitAsync(string summary, CancellationToken cancellationToken)
    {
        await _git.RequireAsync(["add", "-A"], cancellationToken).ConfigureAwait(false);
        var status = await _git.RequireAsync(["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (status.Trim().Length == 0)
        {
            return false;
        }

        await _git.RequireAsync(["commit", "-m", $"{summary} ({_machine})"], cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string FirstLine(string text) => text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";

    /// <summary>The same git, run in another folder (for clone, before the repository exists).</summary>
    private sealed class GitClientAt(GitClient inner, string folder)
    {
        public async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            var saved = inner.RepositoryPath;
            inner.RepositoryPath = folder;
            try
            {
                return await inner.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                inner.RepositoryPath = saved;
            }
        }
    }
}
