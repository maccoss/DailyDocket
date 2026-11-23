using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DailyDocket.App.Services;
using DailyDocket.Core.Calendar;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Data;
using Microsoft.Extensions.Logging;

namespace DailyDocket.App.ViewModels;

/// <summary>The window: three tabs read from the cache, a status line, and Refresh.</summary>
/// <remarks>
/// Everything shown comes from the local cache, so the window opens with the last good data, offline
/// included, and a sync only ever swaps in a newer answer: a column is never emptied while it
/// refreshes (the artifact's rule 9).
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Store _store;
    private readonly JobStore _jobs;
    private readonly SyncScheduler _scheduler;
    private readonly ConfigSource _config;
    private readonly ILogger<MainViewModel> _log;
    private readonly CancellationTokenSource _stop = new();
    private SyncResult? _lastSync;

    public MainViewModel(Store store, JobStore jobs, SyncScheduler scheduler, ConfigSource config, ILogger<MainViewModel> log)
    {
        _store = store;
        _jobs = jobs;
        _scheduler = scheduler;
        _config = config;
        _log = log;

        _scheduler.Started += () => OnUi(() => IsSyncing = true);
        _scheduler.JobFinished += _ => OnUi(Reload);
        _scheduler.Finished += result => OnUi(() =>
        {
            _lastSync = result;
            IsSyncing = false;
            Reload();
        });
    }

    public ObservableCollection<DayItem> Days { get; } = [];

    public ObservableCollection<MailItem> Mail { get; } = [];

    public ObservableCollection<SlackItem> Slack { get; } = [];

    [ObservableProperty]
    public partial string Today { get; set; } = "";

    [ObservableProperty]
    public partial string MailSummary { get; set; } = "";

    [ObservableProperty]
    public partial string SlackSummary { get; set; } = "";

    [ObservableProperty]
    public partial string ScheduleSummary { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>Something the owner must act on (setup, sign-in), or null.</summary>
    [ObservableProperty]
    public partial string? Banner { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsSyncing { get; set; }

    public string Version => AppInfo.InformationalVersion;

    /// <summary>Shows the cache, then syncs now and on the timer.</summary>
    public void Start()
    {
        Reload();
        if (_config.IsComplete)
        {
            _ = RunTimerAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!_config.IsComplete)
        {
            Reload();
            return;
        }

        await _scheduler.SyncAsync(_stop.Token);
    }

    private bool CanRefresh() => !IsSyncing;

    [RelayCommand]
    private static void Open(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private async Task RunTimerAsync()
    {
        try
        {
            await _scheduler.RunPeriodicallyAsync(_stop.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception e)
        {
            _log.LogError(e, "The sync timer stopped");
            OnUi(() => Banner = $"Syncing stopped after an error: {e.Message} Restart DailyDocket to resume; the log has details.");
        }
    }

    internal void Reload()
    {
        var config = _config.Get();
        var zone = _config.Zone;
        var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        Today = local.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);

        try
        {
            ReloadSchedule(config, now, zone);
            ReloadMail(config, now);
            ReloadSlack(config, now);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _log.LogError(e, "Reading the cache failed");
            Banner = $"DailyDocket could not read its cache: {e.Message} Refresh to rebuild it.";
            return;
        }

        Status = StatusLine(local.Offset);
        Banner = SetupProblem() ?? FailureBanner();
    }

    private void ReloadSchedule(Core.Configuration.DocketConfig config, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = _store.Schedule(config, now, zone).Select(d => new DayItem(
            d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.Date.ToString("dddd", CultureInfo.InvariantCulture) + (d.Date == today ? " · today" : ""),
            d.Date.ToString("MMM d", CultureInfo.InvariantCulture),
            d.Date == today,
            [.. d.Background.Select(b => new BannerItem(b.Event.Title, b.Calendar.Tag.Length > 0 ? b.Calendar.Tag : "All day", b.Event.HtmlLink))],
            [.. d.Slots.Select(s => new EventItem(
                TimeRange(s.Event, zone),
                s.Event.Title,
                s.Calendar.Tag,
                s.Event.Location,
                s.Clash,
                s.Unanswered,
                !s.Calendar.Primary,
                s.Event.HtmlLink))])).ToList();
        Swap(Days, days, d => d);
        var meetings = days.Sum(d => d.Events.Count);
        ScheduleSummary = days.Count == 0 ? "Nothing on the calendar for the next week." : $"{meetings} meetings over the next {config.Schedule.Days} days";
    }

    private void ReloadMail(Core.Configuration.DocketConfig config, DateTimeOffset now)
    {
        var account = config.Owner.Account;
        var (rows, counts) = _store.MailRows(_jobs.MailSince(now));
        var items = rows.Where(r => r.State is "Owed" or "Draft").Select(r =>
        {
            var waited = now - (r.WaitingSince ?? now);
            return new MailItem(
                r.ThreadId,
                r.WaitingOn,
                MailAge(waited),
                waited > TimeSpan.FromDays(90) ? 2 : waited > TimeSpan.FromDays(30) ? 1 : 0,
                r.Subject,
                r.Snippet,
                r.Why,
                r.State == "Draft",
                r.Unread,
                GmailLink(account, r.ThreadId));
        }).ToList();
        Swap(Mail, items, m => m);

        var drafts = items.Count(i => i.IsDraft);
        var parts = new List<string> { $"{counts.Owed} waiting on you" };
        if (drafts > 0)
        {
            parts.Add($"{drafts} unsent {(drafts == 1 ? "draft" : "drafts")}");
        }

        if (counts.Reading > 0)
        {
            parts.Add($"{counts.Reading} being read");
        }

        parts.Add($"{counts.Quiet} set aside");
        MailSummary = string.Join(" · ", parts);
    }

    private void ReloadSlack(Core.Configuration.DocketConfig config, DateTimeOffset now)
    {
        var rows = _store.SlackRows(config, _jobs.SlackSince(now));
        var owed = rows.Where(r => r.State == "Owed").Select(r => new SlackItem(
            r.Key,
            r.From,
            RelativeAge(now - r.When),
            now - r.When > TimeSpan.FromDays(3),
            r.IsDm ? $"DM · {r.Where}" : r.Where,
            r.Text.Length > 300 ? r.Text[..300] + "…" : r.Text,
            r.Why,
            r.Permalink)).ToList();
        Swap(Slack, owed, s => s);
        var reading = rows.Count(r => r.State == "Reading");
        SlackSummary = owed.Count == 0 && reading == 0
            ? "Nothing in Slack is waiting on you."
            : $"{owed.Count} waiting on you" + (reading > 0 ? $" · {reading} being read" : "");
    }

    private string StatusLine(TimeSpan offset)
    {
        string When(string job) => _store.LastSuccess(job) is { } t ? t.ToOffset(offset).ToString("h:mmt", CultureInfo.InvariantCulture).ToLowerInvariant() : "never";
        var connection = _lastSync?.Offline == true ? "Offline" : "Online";
        if (IsSyncing)
        {
            connection = "Syncing";
        }

        return $"{connection} · calendar {When(JobPlans.Calendar)} · mail {When(JobPlans.Mail)} · Slack {When(JobPlans.Slack)} · outbox empty · local model not set up";
    }

    private string? SetupProblem()
    {
        if (_config.Problem is { } problem)
        {
            return problem;
        }

        return _config.IsComplete
            ? null
            : $"DailyDocket needs your details before it can sync. Copy docs/docket.example.yaml from the DailyDocket repository to {_config.Path}, fill in owner and calendars, then press Refresh.";
    }

    private string? FailureBanner()
    {
        var failed = _lastSync?.Outcomes.Where(o => !o.Ok).Select(o => o.Detail).Distinct().ToList();
        return failed is { Count: > 0 } ? string.Join(" ", failed) : null;
    }

    /// <summary>Replaces a list's items only when they changed, so a sync that found nothing new leaves the scroll position alone.</summary>
    private static void Swap<T>(ObservableCollection<T> target, IReadOnlyList<T> items, Func<T, object> key)
        where T : notnull
    {
        if (target.Count == items.Count && target.Zip(items).All(p => Equals(key(p.First), key(p.Second))))
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    internal static string TimeRange(CalendarEvent e, TimeZoneInfo zone)
    {
        static string Short(DateTimeOffset t) =>
            t.ToString(t.Minute == 0 ? "%h" : "h:mm", CultureInfo.InvariantCulture) + (t.Hour < 12 ? "a" : "p");
        return $"{Short(TimeZoneInfo.ConvertTime(e.Start, zone))}–{Short(TimeZoneInfo.ConvertTime(e.End, zone))}";
    }

    /// <summary>The artifact's age words: minutes, hours, days, months, years.</summary>
    internal static string MailAge(TimeSpan waited)
    {
        var days = (int)waited.TotalDays;
        return days < 1 ? RelativeAge(waited) : days < 45 ? $"{days}d" : days < 365 ? $"{Math.Round(days / 30.0, MidpointRounding.AwayFromZero)}mo" : $"{days / 365.0:0.0}y";
    }

    internal static string RelativeAge(TimeSpan age)
    {
        // Rounded half up, as the artifact's Math.round did; .NET's default rounds 2.5 to 2.
        static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        var minutes = Round(age.TotalMinutes);
        if (minutes < 60)
        {
            return $"{Math.Max(0, minutes)}m";
        }

        var hours = Round(minutes / 60.0);
        return hours < 36 ? $"{hours}h" : $"{Round(hours / 24.0)}d";
    }

    /// <summary>
    /// #all rather than #inbox so the thread still opens after it is archived; authuser picks the
    /// right mailbox when more than one account is signed in.
    /// </summary>
    internal static string GmailLink(string account, string threadId) =>
        account.Contains('@', StringComparison.Ordinal) && account.Contains('.', StringComparison.Ordinal)
            ? $"https://mail.google.com/mail/?authuser={Uri.EscapeDataString(account)}#all/{Uri.EscapeDataString(threadId)}"
            : $"https://mail.google.com/mail/u/0/#all/{Uri.EscapeDataString(threadId)}";

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
