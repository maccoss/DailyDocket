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

    public SharedJobs(MeetingPrep prep, NightlyJobs nightly, ILogger<SharedJobs> log)
    {
        Prep = prep;
        Nightly = nightly;
        _log = log;
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
        services.AddSingleton<ILeaseStore>(provider => new RepoLeaseStore(provider.GetRequiredService<TodoBook>()));
        services.AddSingleton(provider => new NightlyJobs(
            provider.GetRequiredService<ILeaseStore>(),
            provider.GetRequiredService<Func<DocketConfig>>(),
            () => DateTimeOffset.Now,
            AppInfo.MachineName,
            (slot, ct) => Work(provider, slot, ct),
            provider.GetRequiredService<ILogger<NightlyJobs>>()));
        services.AddSingleton<SharedJobs>();
    }

    /// <summary>The nightly run is the deep sweep then prep; the midday catch-up is prep alone.</summary>
    private static async Task<IReadOnlyList<JobOutcome>> Work(IServiceProvider provider, NightlySlot slot, CancellationToken cancellationToken)
    {
        var outcomes = new List<JobOutcome>();
        outcomes.AddRange(await provider.GetRequiredService<MeetingPrep>().RunDueAsync(DateTimeOffset.Now, cancellationToken));
        _ = slot;
        return outcomes;
    }

    private static IReadOnlyList<string> OpenItems(IServiceProvider provider)
    {
        var store = provider.GetRequiredService<Store>();
        var jobs = provider.GetRequiredService<JobStore>();
        var book = provider.GetRequiredService<TodoBook>();
        var (rows, _) = store.MailRows(jobs.MailSince(DateTimeOffset.UtcNow), book.Handled);
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

        public async Task SaveAsync(JobLease lease, CancellationToken cancellationToken)
        {
            LeaseFile.Save(book.Repository.Root, lease);
            book.NoteChange($"Nightly jobs lease renewed by {lease.Holder}");
            if (book.Sync.IsCloned)
            {
                // Pushed at once: another machine deciding whether to take over must see it.
                await book.SyncNowAsync(cancellationToken);
            }
        }
    }
}
