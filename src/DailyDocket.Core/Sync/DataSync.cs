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
/// version, and the window says so (the design's "later wins, the other is kept"). Files the app
/// writes on its own (verdicts, briefs, the lease) never get that far: the remote's copy is taken
/// during the rebase (<see cref="ResolveSharedConflictsAsync"/>).
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
            var rebased = !remoteHasMain
                || (await _git.RunAsync(["rebase", "origin/main"], cancellationToken).ConfigureAwait(false)).Succeeded
                || await ResolveSharedConflictsAsync(cancellationToken).ConfigureAwait(false);
            if (!rebased)
            {
                await _git.RunAsync(["rebase", "--abort"], cancellationToken).ConfigureAwait(false);
                setAside = $"set-aside/{_machine}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
                await _git.RequireAsync(["branch", setAside], cancellationToken).ConfigureAwait(false);
                var kept = await KeepNonConflictingAsync(setAside, cancellationToken).ConfigureAwait(false);
                _log.LogWarning("Data sync conflict; this machine's version of the conflicting files kept on {Branch}, {Kept} other changes replayed", setAside, kept);
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

    /// <summary>
    /// Folders the app writes on its own (shared verdicts, meeting briefs, the nightly lease). Two
    /// machines often write the same file within one sync (both triage the same new email), and the
    /// files always differ (each names its machine and time), so these conflict routinely.
    /// </summary>
    internal static readonly string[] SharedFolders = ["verdicts/", "briefs/", "state/"];

    /// <summary>
    /// While a rebase is stopped on conflicts: when every conflicting file is in
    /// <see cref="SharedFolders"/>, takes the remote's copy and carries on, as often as the rebase
    /// stops. False (rebase still stopped) at the first conflict on any other file, a to-do, which
    /// the caller sets aside for Mike.
    /// </summary>
    /// <remarks>
    /// The remote's copy is the one already pushed, so taking it matches "the first judgment
    /// stands" for verdicts, keeps the brief other machines already show, and makes the machine
    /// that lost a race for the lease see the winner's claim. Showing these as to-do conflicts
    /// would put a banner up for something Mike cannot and need not decide.
    /// </remarks>
    private async Task<bool> ResolveSharedConflictsAsync(CancellationToken cancellationToken)
    {
        if (!_git.RebaseInProgress())
        {
            return false; // the rebase failed without stopping on a conflict: nothing to resolve
        }

        // A rebase stops at most once per replayed commit; the cap only guards against a loop.
        for (var round = 0; round < 200 && _git.RebaseInProgress(); round++)
        {
            var conflicted = (await _git.RequireAsync(["diff", "--name-only", "-z", "--diff-filter=U"], cancellationToken).ConfigureAwait(false))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (conflicted.Count == 0 || !conflicted.All(IsShared))
            {
                return false;
            }

            foreach (var file in conflicted)
            {
                // During a rebase "ours" is the branch being rebased onto: the remote.
                var taken = await _git.RunAsync(["checkout", "--ours", "--", file], cancellationToken).ConfigureAwait(false);
                if (taken.Succeeded)
                {
                    await _git.RequireAsync(["add", "--", file], cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // The remote deleted it (an old brief cleaned up, say): the deletion stands.
                    await _git.RequireAsync(["rm", "-q", "-f", "--ignore-unmatch", "--", file], cancellationToken).ConfigureAwait(false);
                }
            }

            _log.LogInformation("Data sync: took the other computer's copy of {Count} shared files changed on both", conflicted.Count);

            // A commit whose every change lost to the remote is empty now, and git refuses to
            // continue with an empty commit: skip it instead.
            var nothingLeft = (await _git.RunAsync(["diff", "--cached", "--quiet"], cancellationToken).ConfigureAwait(false)).Succeeded;
            var next = await _git.RunAsync(nothingLeft ? ["rebase", "--skip"] : ["rebase", "--continue"], cancellationToken).ConfigureAwait(false);
            if (next.Succeeded && !_git.RebaseInProgress())
            {
                return true;
            }
        }

        return !_git.RebaseInProgress();
    }

    internal static bool IsShared(string path)
    {
        var p = path.Replace('\\', '/');
        return SharedFolders.Any(f => p.StartsWith(f, StringComparison.Ordinal));
    }

    /// <summary>
    /// After a conflict: main takes the remote's version, then every file this machine changed that
    /// the remote did not touch is put back, so only the files changed on both sides are set aside.
    /// Taking the remote wholesale would drop unrelated tasks added here offline from the list.
    /// </summary>
    private async Task<int> KeepNonConflictingAsync(string setAside, CancellationToken cancellationToken)
    {
        var mergeBase = (await _git.RequireAsync(["merge-base", setAside, "origin/main"], cancellationToken).ConfigureAwait(false)).Trim();
        var remote = (await _git.RequireAsync(["diff", "--name-only", mergeBase, "origin/main"], cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var local = (await _git.RequireAsync(["diff", "--name-status", "--no-renames", mergeBase, setAside], cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Status: parts[0].Trim(), File: parts[1].Trim()))
            .Where(c => !remote.Contains(c.File))
            .ToList();

        await _git.RequireAsync(["reset", "--hard", "origin/main"], cancellationToken).ConfigureAwait(false);
        foreach (var (status, file) in local)
        {
            if (status.StartsWith('D'))
            {
                await _git.RequireAsync(["rm", "-q", "--ignore-unmatch", "--", file], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _git.RequireAsync(["checkout", setAside, "--", file], cancellationToken).ConfigureAwait(false);
            }
        }

        if (local.Count > 0)
        {
            await CommitAsync($"Keep this computer's other changes beside a conflict ({setAside})", cancellationToken).ConfigureAwait(false);
        }

        return local.Count;
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
