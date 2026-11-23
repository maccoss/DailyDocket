using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Prep;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Nightly;

/// <summary>Where the lease lives: the data repository, kept in step with the other machines.</summary>
public interface ILeaseStore
{
    /// <summary>Brings in the other machines' changes, so the lease read next is current.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    JobLease? Load();

    /// <summary>
    /// Writes the lease and sends it to the other machines. False when it could not be sent (the
    /// push failed), so a claim is not trusted; true when sent, or when there is no remote to send to.
    /// </summary>
    Task<bool> SaveAsync(JobLease lease, CancellationToken cancellationToken);

    /// <summary>Writes the lease on this machine only; the next sync sends it.</summary>
    void SaveLocally(JobLease lease);
}

/// <summary>What a run should do: a slot's jobs, or only a retry of the briefs in <paramref name="OnlyEvents"/>.</summary>
public sealed record NightlyRequest(NightlySlot Slot, IReadOnlyCollection<string>? OnlyEvents = null);

/// <summary>What a run did: the deep sweep's runs (none at the catch-up) and each brief.</summary>
public sealed record NightlyWork(IReadOnlyList<JobOutcome> Sweep, IReadOnlyList<PrepResult> Briefs)
{
    public IReadOnlyList<JobOutcome> Outcomes => [.. Sweep, .. Briefs.Select(b => b.Outcome)];
}

/// <summary>What one check of the nightly schedule did.</summary>
/// <param name="Problem">What went wrong, in words for Mike, when a run failed in whole or part; null when all went well.</param>
public sealed record NightlyTick(NightlySlot Slot, bool Ran, string Reason, IReadOnlyList<JobOutcome> Outcomes, string? Problem = null);

/// <summary>
/// Decides when the shared nightly jobs run on this machine and runs them: nightly, at the midday
/// catch-up, and (bypassing the lease) on demand.
/// </summary>
/// <remarks>
/// <para>
/// The clock is injected so the schedule can be tested without waiting for 2 a.m. The jobs
/// themselves are a delegate: this class decides when, the app decides what.
/// </para>
/// <para>
/// A run is claimed in the lease, and the claim pushed, before any work starts; the data sync
/// keeps the copy pushed first when two claims meet, so the machine that lost the race reads the
/// winner's claim back and stands down. Claiming only after the work (as at first) let two
/// machines both run the night's sweep and briefs.
/// </para>
/// </remarks>
public sealed class NightlyJobs
{
    /// <summary>How often the schedule is checked while the app runs.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    /// <summary>A failed run is retried after this long, not at every check: a sign-in problem would otherwise spend a run every ten minutes.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// The longest a run may take: the sweep, its triage and up to 20 briefs take well under an
    /// hour, so this only stops a run that hung, and bounds how long a claim can be live.
    /// </summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromHours(3);

    /// <summary>A claim older than this belongs to a run whose machine died or slept mid-run.</summary>
    public static readonly TimeSpan AbandonAfter = RunTimeout + TimeSpan.FromMinutes(30);

    /// <summary>Tries per slot for one brief, the first included: a brief that keeps failing waits for the next slot.</summary>
    public const int MaxBriefAttempts = 3;

    private readonly ILeaseStore _lease;
    private readonly Func<DocketConfig> _config;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _machine;
    private readonly Func<NightlyRequest, CancellationToken, Task<NightlyWork>> _work;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    // Briefs that failed in a run whose slot is otherwise done: retried alone, without the sweep.
    // Kept in memory: after a restart the next slot's run picks them up anyway, as they have no brief.
    private readonly Dictionary<string, int> _briefAttempts = new(StringComparer.Ordinal);
    private DateTimeOffset _briefSlot = DateTimeOffset.MinValue;
    private DateTimeOffset _briefRetryAfter = DateTimeOffset.MinValue;

    /// <summary>Whether the network is up; offline, nothing is due (and the lease is not renewed).</summary>
    public Func<bool> IsOnline { get; init; } = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;

    public NightlyJobs(
        ILeaseStore lease,
        Func<DocketConfig> config,
        Func<DateTimeOffset> clock,
        string machine,
        Func<NightlyRequest, CancellationToken, Task<NightlyWork>> work,
        ILogger<NightlyJobs>? log = null)
    {
        _lease = lease;
        _config = config;
        _clock = clock;
        _machine = machine;
        _work = work;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised after a run (a slot's or a retry of failed briefs), on a background thread.</summary>
    public event Action<NightlyTick>? Finished;

    /// <summary>The briefs waiting to be retried in this slot, for tests and the log.</summary>
    public IReadOnlyCollection<string> PendingBriefRetries => _briefAttempts.Keys;

    /// <summary>Runs the job that is due, if this machine may run it. One check at a time.</summary>
    public async Task<NightlyTick> TickAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new NightlyTick(NightlySlot.None, false, "a nightly run is already going", []);
        }

        try
        {
            var config = _config();
            var zone = Zone(config);
            if (string.IsNullOrWhiteSpace(config.Nightly.Machine) || (!config.Nightly.IsDesignated(_machine) && config.Nightly.FallbackRank(_machine) < 0))
            {
                return new NightlyTick(NightlySlot.None, false, "this machine does not run the nightly jobs", []);
            }

            // The local copy first: syncing the repository every ten minutes to learn that nothing
            // is due would be wasted git traffic. Only a due job is checked against the remote.
            var now = _clock();
            var local = _lease.Load();
            if (NightlyPlanner.Due(config.Nightly, local, now, zone) == NightlySlot.None)
            {
                return await RetryBriefsAsync(config, zone, local, now, cancellationToken).ConfigureAwait(false)
                    ?? new NightlyTick(NightlySlot.None, false, "nothing due", []);
            }

            await _lease.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var lease = _lease.Load();
            var slot = NightlyPlanner.Due(config.Nightly, lease, now, zone);
            if (slot == NightlySlot.None)
            {
                return new NightlyTick(slot, false, "another machine already ran it", []);
            }

            if (!IsOnline())
            {
                return new NightlyTick(slot, false, "offline", []);
            }

            if (now < _retryAfter)
            {
                return new NightlyTick(slot, false, "waiting to retry after a failed run", []);
            }

            var latest = NightlyPlanner.LatestSlot(config.Nightly, now, zone);
            var decision = LeaseRules.Decide(config.Nightly, lease, _machine, now, latest);
            if (!decision.MayRun)
            {
                _log.LogDebug("Nightly {Slot} due but not run here: {Reason}", slot, decision.Reason);
                return new NightlyTick(slot, false, decision.Reason, []);
            }

            var claim = LeaseRules.Claim(lease, _machine, now);
            if (!await _lease.SaveAsync(claim, cancellationToken).ConfigureAwait(false))
            {
                // Unsent, the claim protects nothing: another machine could be starting too.
                return new NightlyTick(slot, false, "this machine's claim could not be sent to the other computers; trying again at the next check", []);
            }

            var claimed = _lease.Load();
            if (!LeaseRules.IsClaim(claimed, claim))
            {
                var winner = claimed?.Holder ?? "another machine";
                _log.LogInformation("Nightly {Slot} not run here: {Winner} claimed it first", slot, winner);
                return new NightlyTick(slot, false, $"{winner} claimed this run first", []);
            }

            _log.LogInformation("Nightly {Slot} starting: {Reason}", slot, decision.Reason);
            return await RunSlotAsync(config, zone, claimed!, slot, latest, decision.Reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NightlyTick> RunSlotAsync(
        DocketConfig config, TimeZoneInfo zone, JobLease claimed, NightlySlot slot, DateTimeOffset latest, string reason, CancellationToken cancellationToken)
    {
        NightlyWork? work = null;
        string? problem = null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RunTimeout);
            try
            {
                work = await _work(new NightlyRequest(slot), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The app is closing. The claim is withdrawn on disk only: a push now would run git
                // while the process exits. The next start's sync sends it.
                _lease.SaveLocally(LeaseRules.Release(claimed));
                throw;
            }
            catch (OperationCanceledException)
            {
                problem = $"The nightly run took longer than {RunTimeout.TotalHours:0} hours and was stopped. It will try again in an hour.";
            }
            catch (Exception e)
            {
                _log.LogError(e, "Nightly {Slot} failed", slot);
                problem = $"The nightly run failed: {e.Message} It will try again in an hour.";
            }
        }

        var finished = _clock();
        var succeeded = work is not null && Succeeded(slot, work);
        _retryAfter = succeeded ? DateTimeOffset.MinValue : finished + RetryAfterFailure;

        // A new slot's run replaces the last one's retries; this run's failed briefs are retried alone.
        _briefAttempts.Clear();
        _briefSlot = latest;
        _briefRetryAfter = finished + RetryAfterFailure;
        var failedBriefs = work?.Briefs.Where(b => !b.Outcome.Ok).ToList() ?? [];
        if (succeeded)
        {
            foreach (var b in failedBriefs)
            {
                _briefAttempts[b.EventId] = 1;
            }
        }

        if (work is not null)
        {
            // Retried either way: alone when the slot is done, with the whole run when it is not.
            problem ??= Describe(work, retried: true);
        }

        var next = succeeded
            ? LeaseRules.Renew(claimed, _machine, finished, NightlyPlanner.NextSlot(config.Nightly, finished, zone), slot)
            : LeaseRules.Fail(claimed, _machine, finished, latest);
        await SaveAsync(next, cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "Nightly {Slot} {Result}; {Failed} of {Briefs} briefs failed; failures in a row {Failures}",
            slot, succeeded ? "done" : "failed", failedBriefs.Count, work?.Briefs.Count ?? 0, next.Failures);
        var tick = new NightlyTick(slot, true, reason, work?.Outcomes ?? [], problem);
        Finished?.Invoke(tick);
        return tick;
    }

    /// <summary>
    /// Retries the briefs that failed in this machine's last run, once the hour has passed, at most
    /// <see cref="MaxBriefAttempts"/> tries each per slot. Null when there is nothing to retry now.
    /// </summary>
    private async Task<NightlyTick?> RetryBriefsAsync(DocketConfig config, TimeZoneInfo zone, JobLease? lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_briefAttempts.Count == 0)
        {
            return null;
        }

        // A new slot runs everything that is due anyway; another holder means another machine took over.
        if (NightlyPlanner.LatestSlot(config.Nightly, now, zone) != _briefSlot || lease is null || !NightlyConfig.SameMachine(lease.Holder, _machine))
        {
            _briefAttempts.Clear();
            return null;
        }

        if (now < _briefRetryAfter || !IsOnline())
        {
            return null;
        }

        var ids = _briefAttempts.Keys.ToList();
        _log.LogInformation("Retrying {Count} meeting briefs that failed", ids.Count);
        NightlyWork work;
        string? problem = null;
        try
        {
            work = await _work(new NightlyRequest(NightlySlot.None, ids), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Retrying meeting briefs failed");
            work = new NightlyWork([], []);
            problem = $"Meeting briefs could not be retried: {e.Message}";
        }

        foreach (var id in ids)
        {
            var result = work.Briefs.FirstOrDefault(b => b.EventId == id);

            // No result: the event is no longer due (briefed elsewhere, moved or started).
            if (result is null || result.Outcome.Ok || ++_briefAttempts[id] >= MaxBriefAttempts)
            {
                _briefAttempts.Remove(id);
            }
        }

        _briefRetryAfter = _clock() + RetryAfterFailure;
        problem ??= Describe(work, retried: _briefAttempts.Count > 0);
        var tick = new NightlyTick(NightlySlot.None, true, $"retried {ids.Count} briefs that failed", work.Outcomes, problem);
        Finished?.Invoke(tick);
        return tick;
    }

    /// <summary>The slot is done once its sweep succeeded (nightly), or any brief was written or none was needed (catch-up).</summary>
    internal static bool Succeeded(NightlySlot slot, NightlyWork work) =>
        slot == NightlySlot.Nightly
            ? work.Sweep.All(o => o.Ok)
            : work.Briefs.Count == 0 || work.Briefs.Any(b => b.Outcome.Ok);

    /// <summary>What failed, for the window, in words Mike can act on; null when nothing did.</summary>
    internal static string? Describe(NightlyWork work, bool retried)
    {
        var parts = new List<string>();
        if (work.Sweep.FirstOrDefault(o => !o.Ok) is { } sweep)
        {
            parts.Add($"Last night's search of older mail failed: {sweep.Detail ?? "no reason was given."} It will try again in an hour.");
        }

        var failed = work.Briefs.Where(b => !b.Outcome.Ok).ToList();
        if (failed.Count > 0)
        {
            var what = failed.Count == 1 ? $"The meeting brief for \"{failed[0].Title}\" could not be written" : $"{failed.Count} meeting briefs could not be written";
            var reason = failed.Select(b => b.Outcome.Detail).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "no reason was given.";
            parts.Add($"{what}: {reason}" + (retried ? " DailyDocket will try again in an hour; Prep now on the event tries at once." : " Prep now on the event tries again."));
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>Saves and sends the lease; when the app is closing, only saves it, and the next start sends it.</summary>
    private async Task SaveAsync(JobLease lease, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _lease.SaveLocally(lease);
            return;
        }

        try
        {
            await _lease.SaveAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _lease.SaveLocally(lease);
        }
    }

    /// <summary>Checks the schedule now and every <see cref="CheckEvery"/> until canceled.</summary>
    public async Task RunPeriodicallyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One bad night must not stop the schedule.
                _log.LogError(e, "The nightly check failed");
            }

            await Task.Delay(CheckEvery, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeZoneInfo Zone(DocketConfig config)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(config.Schedule.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
