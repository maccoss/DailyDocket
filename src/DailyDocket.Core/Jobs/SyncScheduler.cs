using System.Net.NetworkInformation;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Infrastructure;

namespace DailyDocket.Core.Jobs;

/// <summary>The result of one sync: each job's outcome, or offline.</summary>
public sealed record SyncResult(bool Offline, IReadOnlyList<JobOutcome> Outcomes, DateTimeOffset FinishedAt)
{
    public bool Ok => !Offline && Outcomes.All(o => o.Ok);
}

/// <summary>
/// Runs the pulls (calendar, mail and Slack together), then triage, on a timer and on Refresh.
/// </summary>
/// <remarks>
/// One sync at a time: a Refresh during a sync waits for it rather than starting a second set of
/// runs over the same cache. Offline is normal, not an error: the sync is skipped, the tabs keep
/// showing the cache, and the next tick tries again.
/// </remarks>
public sealed class SyncScheduler : IDisposable
{
    private readonly JobRunner _runner;
    private readonly IJobStore _store;
    private readonly Func<DocketConfig> _config;
    private readonly Func<AppSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncScheduler(JobRunner runner, IJobStore store, Func<DocketConfig> config, Func<AppSettings> settings)
    {
        _runner = runner;
        _store = store;
        _config = config;
        _settings = settings;
    }

    /// <summary>Raised when a sync starts, on a background thread.</summary>
    public event Action? Started;

    /// <summary>Raised after each job's results are in the cache, so a tab can refresh early.</summary>
    public event Action<JobOutcome>? JobFinished;

    /// <summary>Raised when a sync ends, on a background thread.</summary>
    public event Action<SyncResult>? Finished;

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Syncs now, or waits for the sync already running and returns its successor's result.</summary>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Started?.Invoke();
            var result = await RunAsync(cancellationToken).ConfigureAwait(false);
            Finished?.Invoke(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Called before each sync decides whether triage is needed: the app takes in verdicts other
    /// machines shared, so it does not pay to judge them again.
    /// </summary>
    public Action? BeforeTriage { get; set; }

    /// <summary>
    /// Runs other work over the cache (the nightly deep sweep) when no sync is running, and keeps a
    /// sync from starting until it is done: two triage runs at once would judge the same items twice.
    /// </summary>
    public async Task<T> ExclusiveAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Syncs now and then every <see cref="AppSettings.PullIntervalMinutes"/> until canceled.</summary>
    public async Task RunPeriodicallyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsRunning)
            {
                await SyncAsync(cancellationToken).ConfigureAwait(false);
            }

            var minutes = Math.Max(5, _settings().PullIntervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(minutes), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SyncResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return new SyncResult(true, [], DateTimeOffset.UtcNow);
        }

        var config = _config();
        var settings = _settings();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.Schedule.TimeZone);
        var now = DateTimeOffset.UtcNow;

        async Task<JobOutcome> Run(JobPlan plan)
        {
            var outcome = await _runner.RunAsync(plan, cancellationToken).ConfigureAwait(false);
            JobFinished?.Invoke(outcome);
            return outcome;
        }

        var pulls = await Task.WhenAll(
            Run(JobPlans.CalendarPull(config, now, zone, settings.PullModel)),
            Run(JobPlans.MailPull(config, now, _store.Watermark("mail", "full"), settings.PullModel)),
            Run(JobPlans.SlackPull(config, now, zone, _store.Watermark("slack", "full"), settings.PullModel))).ConfigureAwait(false);

        var outcomes = pulls.ToList();
        BeforeTriage?.Invoke();
        outcomes.AddRange(await JobPlans.TriageAllAsync(_store, Run, settings.JudgmentModel, JobPlans.TriageRunsPerSync).ConfigureAwait(false));

        _store.PurgeCaptures(now);
        return new SyncResult(false, outcomes, DateTimeOffset.UtcNow);
    }

    public void Dispose() => _gate.Dispose();
}
