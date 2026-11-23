using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Nightly;

/// <summary>Where the lease lives: the data repository, kept in step with the other machines.</summary>
public interface ILeaseStore
{
    /// <summary>Brings in the other machines' changes, so the lease read next is current.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    JobLease? Load();

    /// <summary>Writes the lease and sends it to the other machines.</summary>
    Task SaveAsync(JobLease lease, CancellationToken cancellationToken);
}

/// <summary>What one check of the nightly schedule did.</summary>
public sealed record NightlyTick(NightlySlot Slot, bool Ran, string Reason, IReadOnlyList<JobOutcome> Outcomes);

/// <summary>
/// Decides when the shared nightly jobs run on this machine and runs them: nightly, at the midday
/// catch-up, and (bypassing the lease) on demand.
/// </summary>
/// <remarks>
/// The clock is injected so the schedule can be tested without waiting for 2 a.m. The jobs
/// themselves are a delegate: this class decides when, the app decides what.
/// </remarks>
public sealed class NightlyJobs
{
    /// <summary>How often the schedule is checked while the app runs.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    private readonly ILeaseStore _lease;
    private readonly Func<DocketConfig> _config;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _machine;
    private readonly Func<NightlySlot, CancellationToken, Task<IReadOnlyList<JobOutcome>>> _work;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    /// <summary>A failed run is retried after this long, not at every check: a sign-in problem would otherwise spend a run every ten minutes.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>Whether the network is up; offline, nothing is due (and the lease is not renewed).</summary>
    public Func<bool> IsOnline { get; init; } = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;

    public NightlyJobs(
        ILeaseStore lease,
        Func<DocketConfig> config,
        Func<DateTimeOffset> clock,
        string machine,
        Func<NightlySlot, CancellationToken, Task<IReadOnlyList<JobOutcome>>> work,
        ILogger<NightlyJobs>? log = null)
    {
        _lease = lease;
        _config = config;
        _clock = clock;
        _machine = machine;
        _work = work;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised after a run, on a background thread.</summary>
    public event Action<NightlyTick>? Finished;

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
            if (NightlyPlanner.Due(config.Nightly, _lease.Load(), now, zone) == NightlySlot.None)
            {
                return new NightlyTick(NightlySlot.None, false, "nothing due", []);
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

            var decision = LeaseRules.Decide(config.Nightly, lease, _machine, now);
            if (!decision.MayRun)
            {
                _log.LogDebug("Nightly {Slot} due but not run here: {Reason}", slot, decision.Reason);
                return new NightlyTick(slot, false, decision.Reason, []);
            }

            _log.LogInformation("Nightly {Slot} starting: {Reason}", slot, decision.Reason);
            IReadOnlyList<JobOutcome> outcomes = [];
            var completed = false;
            try
            {
                outcomes = await _work(slot, cancellationToken).ConfigureAwait(false);
                completed = outcomes.All(o => o.Ok);
            }
            finally
            {
                // Renewed whatever happened: the lease says this machine is alive and on duty, so a
                // failure is retried here rather than handed to a fallback. Only a run that finished
                // well counts as done; any other is tried again after RetryAfterFailure.
                var finished = _clock();
                _retryAfter = completed ? DateTimeOffset.MinValue : finished + RetryAfterFailure;
                await _lease.SaveAsync(LeaseRules.Renew(lease, _machine, finished, NightlyPlanner.NextSlot(config.Nightly, finished, zone), completed ? slot : NightlySlot.None), CancellationToken.None).ConfigureAwait(false);
            }

            var tick = new NightlyTick(slot, true, decision.Reason, outcomes);
            Finished?.Invoke(tick);
            return tick;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks the schedule now and every <see cref="CheckEvery"/> until cancelled.</summary>
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
