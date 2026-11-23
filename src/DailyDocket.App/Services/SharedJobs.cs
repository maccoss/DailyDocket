using DailyDocket.Core.Configuration;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Nightly;
using DailyDocket.Core.Prep;
using DailyDocket.Core.Todos;
using DailyDocket.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DailyDocket.App.Services;

/// <summary>
/// The phase 6 jobs the window starts and shows: the nightly schedule (deep sweep and meeting prep
/// on the designated machine) and "Prep now".
/// </summary>
/// <remarks>
/// One object for the window to take, so the phase's wiring lives here rather than spread through
/// <see cref="Program"/> and the view model.
/// </remarks>
public sealed class SharedJobs
{
    private readonly ILogger<SharedJobs> _log;

    public SharedJobs(MeetingPrep prep, NightlyJobs nightly, VerdictSync verdicts, JobStore jobs, SyncScheduler scheduler, TodoBook book, ILogger<SharedJobs> log)
    {
        Prep = prep;
        Nightly = nightly;
        Verdicts = verdicts;
        _log = log;

        // Every verdict judged here is shared; verdicts judged elsewhere are taken in before this
        // machine decides what to judge, and whenever a data sync brings new ones.
        jobs.VerdictRecorded = (source, key, forMessage, needsReply, why) => Guard("Sharing a verdict", () => verdicts.Export(source, key, forMessage, needsReply, why));
        scheduler.BeforeTriage = () => ImportVerdicts();
        book.Synced += result =>
        {
            if (result.Changed && ImportVerdicts() > 0)
            {
                CacheChanged?.Invoke();
            }
        };
    }

    /// <summary>Raised when verdicts from another machine changed what the tabs show, on a background thread.</summary>
    public event Action? CacheChanged;

    public VerdictSync Verdicts { get; }

    /// <summary>Takes in other machines' verdicts; a problem is logged, never allowed to stop a sync.</summary>
    public int ImportVerdicts()
    {
        var count = 0;
        Guard("Reading shared verdicts", () => count = Verdicts.Import());
        if (count > 0)
        {
            _log.LogInformation("Took in {Count} verdicts from other machines", count);
        }

        return count;
    }

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning(e, "{What} failed", what);
        }
    }

    public MeetingPrep Prep { get; }

    public NightlyJobs Nightly { get; }

    /// <summary>Starts checking the nightly schedule; runs until <paramref name="stop"/> is cancelled.</summary>
    public void Start(CancellationToken stop) => _ = RunAsync(stop);

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await Nightly.RunPeriodicallyAsync(stop);
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception e)
        {
            _log.LogError(e, "The nightly schedule stopped");
        }
    }

    /// <summary>Registers the phase 6 services; called once from <see cref="Program.BuildServiceProvider"/>.</summary>
    internal static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new MeetingPrep(
            (plan, ct) => provider.GetRequiredService<JobRunner>().RunAsync(plan, ct),
            () => provider.GetRequiredService<DataRepository>().Root,
            provider.GetRequiredService<Func<DocketConfig>>(),
            () => provider.GetRequiredService<AppSettings>().JudgmentModel,
            (from, to) => provider.GetRequiredService<Store>().Events(from, to),
            () => OpenItems(provider),
            summary => provider.GetRequiredService<TodoBook>().NoteChange(summary),
            AppInfo.MachineName,
            provider.GetRequiredService<ILogger<MeetingPrep>>()));
        services.AddSingleton(provider => new VerdictSync(
            provider.GetRequiredService<Store>(),
            () => provider.GetRequiredService<DataRepository>().Root,
            summary => provider.GetRequiredService<TodoBook>().NoteChange(summary),
            AppInfo.MachineName));
        services.AddSingleton<ILeaseStore>(provider => new RepoLeaseStore(provider.GetRequiredService<TodoBook>()));
        services.AddSingleton(provider => new NightlyJobs(
            provider.GetRequiredService<ILeaseStore>(),
            provider.GetRequiredService<Func<DocketConfig>>(),
            () => DateTimeOffset.Now,
            AppInfo.MachineName,
            (request, ct) => Work(provider, request, ct),
            provider.GetRequiredService<ILogger<NightlyJobs>>()));
        services.AddSingleton<SharedJobs>();
    }

    /// <summary>
    /// The nightly run is the deep sweep then prep; the midday catch-up is prep alone; a retry is
    /// prep for the briefs that failed, alone.
    /// </summary>
    private static async Task<NightlyWork> Work(IServiceProvider provider, NightlyRequest request, CancellationToken cancellationToken)
    {
        var prep = provider.GetRequiredService<MeetingPrep>();
        if (request.OnlyEvents is { } only)
        {
            return new NightlyWork([], await prep.RunDueAsync(DateTimeOffset.Now, cancellationToken, only));
        }

        IReadOnlyList<JobOutcome> sweep = [];
        if (request.Slot == NightlySlot.Nightly)
        {
            // Under the sync's lock: the deep sweep's triage must not race a regular sync's.
            var scheduler = provider.GetRequiredService<SyncScheduler>();
            sweep = await scheduler.ExclusiveAsync(ct => DeepSweepAsync(provider, ct), cancellationToken);
        }

        return new NightlyWork(sweep, await prep.RunDueAsync(DateTimeOffset.Now, cancellationToken));
    }

    /// <summary>The nightly deep sweep: read older mail, then judge what it found.</summary>
    private static async Task<IReadOnlyList<JobOutcome>> DeepSweepAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var runner = provider.GetRequiredService<JobRunner>();
        var jobs = provider.GetRequiredService<JobStore>();
        var settings = provider.GetRequiredService<AppSettings>();
        var config = provider.GetRequiredService<Func<DocketConfig>>()();
        var now = DateTimeOffset.UtcNow;

        var outcomes = new List<JobOutcome> { await runner.RunAsync(JobPlans.DeepSweepRun(config, now, settings.PullModel), cancellationToken) };
        provider.GetRequiredService<SharedJobs>().ImportVerdicts();
        if (jobs.TriageBatch(now, 1).Count > 0)
        {
            outcomes.Add(await runner.RunAsync(JobPlans.TriageRun(settings.JudgmentModel), cancellationToken));
        }

        return outcomes;
    }

    private static IReadOnlyList<string> OpenItems(IServiceProvider provider)
    {
        var store = provider.GetRequiredService<Store>();
        var jobs = provider.GetRequiredService<JobStore>();
        var book = provider.GetRequiredService<TodoBook>();
        var now = DateTimeOffset.UtcNow;
        var (rows, _) = store.MailRows(jobs.MailSince(now), book.Handled, jobs.Deep(now));
        return PrepContext.OpenItems(book.Items, rows.Where(r => r.State == "Owed").Select(r => (r.WaitingOn, r.Subject, r.Why)));
    }

    /// <summary>The lease in the data repository, synced through the to-do list's git path.</summary>
    private sealed class RepoLeaseStore(TodoBook book) : ILeaseStore
    {
        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            if (book.Sync.IsCloned)
            {
                await book.SyncNowAsync(cancellationToken);
            }
        }

        public JobLease? Load() => LeaseFile.Load(book.Repository.Root);

        public async Task<bool> SaveAsync(JobLease lease, CancellationToken cancellationToken)
        {
            LeaseFile.Save(book.Repository.Root, lease);
            book.NoteChange(lease.Running is null ? $"Nightly jobs lease renewed by {lease.Holder}" : $"Nightly run claimed by {lease.Holder}");
            if (!book.Sync.IsCloned)
            {
                return true; // one machine, nobody to tell
            }

            // Pushed at once: another machine deciding whether to take over must see it.
            var result = await book.SyncNowAsync(cancellationToken);
            return result.Ok && !result.Offline;
        }

        // Written only; the commit is made by the next sync (it adds everything that changed).
        public void SaveLocally(JobLease lease) => LeaseFile.Save(book.Repository.Root, lease);
    }
}
