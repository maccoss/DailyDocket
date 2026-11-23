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
using DailyDocket.Core.Outbox;
using DailyDocket.Core.Todos;
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
    private readonly TodoBook _book;
    private readonly AppSettings _settings;
    private readonly ILogger<MainViewModel> _log;
    private readonly CancellationTokenSource _stop = new();
    private SyncResult? _lastSync;
    private string? _dataProblem;
    private (string Kind, string Key, string UpTo, string Subject)? _capture;
    private (string Kind, string Key)? _lastHandled;

    private readonly OutboxStore _outbox;
    private readonly OutboxWorker _worker;
    private readonly System.Windows.Threading.DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainViewModel(Store store, JobStore jobs, SyncScheduler scheduler, ConfigSource config, TodoBook book, TodoViewModel todos, AppSettings settings,
        OutboxStore outbox, OutboxWorker worker, Drafter drafter, ILogger<MainViewModel> log)
    {
        _outbox = outbox;
        _worker = worker;
        MailCompose = new ComposeViewModel(outbox, settings, OutboxKind.EmailReply, drafter);
        SlackCompose = new ComposeViewModel(outbox, settings, OutboxKind.SlackReply, drafter);
        _store = store;
        _jobs = jobs;
        _scheduler = scheduler;
        _config = config;
        _book = book;
        _settings = settings;
        _log = log;
        Todos = todos;

        MailCompose.Queued += _ => ReloadOutbox();
        SlackCompose.Queued += _ => ReloadOutbox();
        _worker.Changed += _ => OnUi(() =>
        {
            ReloadOutbox();
            Status = StatusLine(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _config.Zone).Offset);
        });
        _countdown.Tick += (_, _) => ReloadOutbox();

        // A Handled mark, here or from another machine, changes what the tabs show.
        _book.Changed += () => OnUi(Reload);
        _book.Synced += result => OnUi(() =>
        {
            _dataProblem = result.Problem ?? (result.SetAside is { } branch
                ? $"A to-do was changed on two computers at once. This computer's version is kept on the {branch} branch of your data repository; the list shows the other."
                : null);
            Banner = SetupProblem() ?? FailureBanner();
        });

        _scheduler.Started += () => OnUi(() =>
        {
            IsSyncing = true;
            Status = StatusLine(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _config.Zone).Offset);
        });
        _scheduler.JobFinished += _ => OnUi(Reload);
        _scheduler.Finished += result => OnUi(() =>
        {
            _lastSync = result;
            IsSyncing = false;
            Reload();
        });
    }

    public TodoViewModel Todos { get; }

    public ComposeViewModel MailCompose { get; }

    public ComposeViewModel SlackCompose { get; }

    /// <summary>Replies on their way out for the selected thread: queued (with Undo), held or failed.</summary>
    public ObservableCollection<OutboxView> MailOutbox { get; } = [];

    public ObservableCollection<OutboxView> SlackOutbox { get; } = [];

    public ObservableCollection<DayItem> Days { get; } = [];

    /// <summary>The task form shown over the reader after To do is pressed.</summary>
    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    [ObservableProperty]
    public partial string CaptureTitle { get; set; } = "";

    [ObservableProperty]
    public partial string CaptureNotes { get; set; } = "";

    [ObservableProperty]
    public partial string CaptureTags { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? CaptureDue { get; set; }

    /// <summary>"Marked handled" after a Handled press, with Undo; null otherwise.</summary>
    [ObservableProperty]
    public partial string? UndoText { get; set; }

    public ObservableCollection<MailItem> Mail { get; } = [];

    public ObservableCollection<SlackItem> Slack { get; } = [];

    /// <summary>The selected thread's messages, read from the cache: no network needed.</summary>
    public ObservableCollection<MessageView> MailReader { get; } = [];

    public ObservableCollection<MessageView> SlackReader { get; } = [];

    [ObservableProperty]
    public partial MailItem? SelectedMail { get; set; }

    [ObservableProperty]
    public partial SlackItem? SelectedSlack { get; set; }

    [ObservableProperty]
    public partial string MailReaderTitle { get; set; } = "";

    [ObservableProperty]
    public partial string SlackReaderTitle { get; set; } = "";

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
        if (_book.Sync.IsCloned)
        {
            _book.Reload();
        }

        Todos.Refresh();
        Reload();
        _ = _worker.RunAsync(_stop.Token);
        _ = StartAsync();
    }

    /// <summary>Clones the data repository the first time, brings its changes in, then starts the timer.</summary>
    private async Task StartAsync()
    {
        try
        {
            if (!_book.Sync.IsCloned && _settings.DataRepositoryUrl.Length > 0)
            {
                _dataProblem = await _book.Sync.EnsureClonedAsync(_settings.DataRepositoryUrl, _stop.Token);
                if (_config.AdoptIntoRepository())
                {
                    _log.LogInformation("Moved docket.yaml into the data repository");
                }
            }

            if (_book.Sync.IsCloned)
            {
                await _book.SyncNowAsync(_stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        OnUi(Reload);
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

        var data = _book.Sync.IsCloned ? _book.SyncNowAsync(_stop.Token) : Task.CompletedTask;
        await _scheduler.SyncAsync(_stop.Token);
        await data;
    }

    private bool CanRefresh() => !IsSyncing;

    [RelayCommand]
    private void CaptureMail()
    {
        if (SelectedMail is { } m)
        {
            OpenCapture("email", m.Key, m.UpTo, m.Subject, $"{m.From}: {m.Subject}");
        }
    }

    [RelayCommand]
    private void CaptureSlack()
    {
        if (SelectedSlack is { } s)
        {
            var firstLine = s.Text.Split('\n')[0];
            OpenCapture("slack", s.Key, s.LastTs, s.Where, firstLine.Length > 80 ? firstLine[..80] : firstLine);
        }
    }

    private void OpenCapture(string kind, string key, string upTo, string subject, string title)
    {
        _capture = (kind, key, upTo, subject);
        CaptureTitle = title;
        CaptureNotes = "";
        CaptureTags = "";
        CaptureDue = null;
        IsCapturing = true;
    }

    /// <summary>Saving a task marks its source handled: it is tracked on the list instead (the artifact's rule).</summary>
    [RelayCommand]
    private void SaveCapture()
    {
        if (_capture is not { } c || string.IsNullOrWhiteSpace(CaptureTitle))
        {
            return;
        }

        _book.Add(CaptureTitle, CaptureNotes, CaptureDue is { } d ? DateOnly.FromDateTime(d) : null, Core.Todos.TodoRules.ParseTags(CaptureTags), new Core.Todos.TodoSource(c.Kind, c.Key, c.Subject));
        IsCapturing = false;
        _capture = null;
        MarkHandled(c.Kind, c.Key, c.UpTo, "Added to To do and marked handled.");
    }

    [RelayCommand]
    private void CancelCapture()
    {
        IsCapturing = false;
        _capture = null;
    }

    [RelayCommand]
    private void HandleMail()
    {
        if (SelectedMail is { } m)
        {
            MarkHandled("email", m.Key, m.UpTo, "Marked handled.");
        }
    }

    [RelayCommand]
    private void HandleSlack()
    {
        if (SelectedSlack is { } s)
        {
            MarkHandled("slack", s.Key, s.LastTs, "Marked handled.");
        }
    }

    private void MarkHandled(string kind, string key, string upTo, string message)
    {
        _book.MarkHandled(kind, key, upTo);
        _lastHandled = (kind, key);
        UndoText = message;
    }

    [RelayCommand]
    private void Undo()
    {
        if (_lastHandled is { } h)
        {
            _book.Unhandle(h.Kind, h.Key);
        }

        _lastHandled = null;
        UndoText = null;
    }

    [RelayCommand]
    private static void Open(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    partial void OnSelectedMailChanged(MailItem? value)
    {
        LoadMailReader(value);
        OpenDraftOrClose(MailCompose, value?.Key, () => ReplyMail());
        ReloadOutbox();
    }

    partial void OnSelectedSlackChanged(SlackItem? value)
    {
        LoadSlackReader(value);
        OpenDraftOrClose(SlackCompose, value?.Key, () => ReplySlack());
        ReloadOutbox();
    }

    /// <summary>A conversation with an unsent draft opens with it; any other closes the box.</summary>
    private void OpenDraftOrClose(ComposeViewModel compose, string? target, Action open)
    {
        if (target is not null && compose.IsFor(target))
        {
            return;
        }

        if (target is not null && _outbox.ForTarget(target).Any(i => i.Status == OutboxStatus.Draft && i.Kind == compose.Kind))
        {
            open();
        }
        else if (compose.IsOpen)
        {
            compose.CloseCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void ReplyMail() => OpenMailReply(replyAll: false);

    [RelayCommand]
    private void ReplyAllMail() => OpenMailReply(replyAll: true);

    private void OpenMailReply(bool replyAll)
    {
        if (SelectedMail is not { } m)
        {
            return;
        }

        var thread = _store.ThreadMessages(m.Key);
        if (thread.Count == 0)
        {
            return;
        }

        var newest = thread.OrderBy(x => x.Date ?? DateTimeOffset.MinValue).Last();
        var (to, cc) = OutboxRules.Recipients(thread, replyAll, _config.Get());
        MailCompose.Open(m.Key, newest.Id, null, m.Subject, newest.Id, to, cc, replyAll ? "Reply all" : $"Reply to {m.From}", () => MailConversation(m.Key));
    }

    [RelayCommand]
    private void ReplySlack()
    {
        if (SelectedSlack is not { } s || _store.Conversation(s.Key) is not { } conv)
        {
            return;
        }

        SlackCompose.Open(s.Key, conv.ThreadTs, conv.ChannelId, s.Where, conv.Last.Ts, [], [], conv.ThreadTs is null ? $"Reply in {s.Where}" : "Reply in thread", () => SlackConversation(s.Key));
    }

    /// <summary>Undo inside the window: back to a draft, open for editing. Nothing was sent.</summary>
    [RelayCommand]
    private void UndoSend(OutboxView? view)
    {
        if (view is null || _outbox.Get(view.Item.Id) is not { Status: OutboxStatus.Queued } item)
        {
            return;
        }

        Compose(item).Edit(item, Conversation(item));
        ReloadOutbox();
    }

    [RelayCommand]
    private void EditOutbox(OutboxView? view)
    {
        if (view is not null && _outbox.Get(view.Item.Id) is { Status: OutboxStatus.Held or OutboxStatus.Failed } item)
        {
            Compose(item).Edit(item, Conversation(item));
            ReloadOutbox();
        }
    }

    /// <summary>
    /// Sends a held or failed item again. For a held one Mike has now seen what arrived, so its base
    /// moves to the conversation's newest message (and an email answers that message).
    /// </summary>
    [RelayCommand]
    private void SendAgain(OutboxView? view)
    {
        if (view is null || _outbox.Get(view.Item.Id) is not { Status: OutboxStatus.Held or OutboxStatus.Failed } item)
        {
            return;
        }

        if (item.Kind == OutboxKind.EmailReply && _store.ThreadMessages(item.Target).OrderBy(x => x.Date ?? DateTimeOffset.MinValue).LastOrDefault() is { } newest)
        {
            item = item with { Base = newest.Id, ReplyTo = newest.Id };
        }
        else if (item.Kind == OutboxKind.SlackReply && _store.Conversation(item.Target) is { } conv)
        {
            item = item with { Base = conv.Last.Ts };
        }

        _outbox.Save(item with { Status = OutboxStatus.Queued, SendAfter = DateTimeOffset.UtcNow, Problem = null, Updated = DateTimeOffset.UtcNow });
        ReloadOutbox();
    }

    [RelayCommand]
    private void DiscardOutbox(OutboxView? view)
    {
        if (view is not null && _outbox.Get(view.Item.Id) is { Status: not OutboxStatus.Sending })
        {
            _outbox.Delete(view.Item.Id);
            ReloadOutbox();
        }
    }

    private ComposeViewModel Compose(OutboxItem item) => item.Kind == OutboxKind.EmailReply ? MailCompose : SlackCompose;

    private Func<string> Conversation(OutboxItem item) =>
        item.Kind == OutboxKind.EmailReply ? () => MailConversation(item.Target) : () => SlackConversation(item.Target);

    /// <summary>The thread for a draft, oldest first: each message's new text, without the quoted history it repeats.</summary>
    private string MailConversation(string threadId)
    {
        var zone = _config.Zone;
        var sb = new System.Text.StringBuilder();
        foreach (var m in _store.ThreadMessages(threadId).TakeLast(8))
        {
            var text = Core.Mail.MailText.SplitQuoted(m.Body ?? m.Snippet).Message;
            sb.Append("--- From: ").Append(m.Sender).Append(" (")
              .Append(m.Date is { } d ? TimeZoneInfo.ConvertTime(d, zone).ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture) : "").Append(")\n")
              .Append("To: ").Append(string.Join(", ", m.To)).Append('\n')
              .Append(text.Length > 2000 ? text[..2000] : text).Append("\n\n");
        }

        return sb.ToString();
    }

    private string SlackConversation(string key)
    {
        if (_store.Conversation(key) is not { } conv)
        {
            return "";
        }

        var me = _config.Get().Owner.SlackUserId;
        var zone = _config.Zone;
        return string.Join('\n', conv.Messages.TakeLast(12).Select(m =>
            $"{(m.FromId == me ? "Mike" : m.FromName)} ({TimeZoneInfo.ConvertTime(m.When, zone).ToString("ddd h:mm tt", CultureInfo.InvariantCulture)}): {Core.Slack.SlackRules.Readable(m.Text)}"));
    }

    private void ReloadOutbox()
    {
        Fill(MailOutbox, SelectedMail?.Key);
        Fill(SlackOutbox, SelectedSlack?.Key);
        var anyQueued = _outbox.WithStatus(OutboxStatus.Queued).Count > 0;
        if (anyQueued && !_countdown.IsEnabled)
        {
            _countdown.Start();
        }
        else if (!anyQueued && _countdown.IsEnabled)
        {
            _countdown.Stop();
        }

        void Fill(ObservableCollection<OutboxView> list, string? target)
        {
            list.Clear();
            if (target is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var i in _outbox.ForTarget(target).Where(i => i.Status != OutboxStatus.Draft))
            {
                var status = i.Status switch
                {
                    OutboxStatus.Queued when i.SendAfter > now => $"Sending in {Math.Ceiling((i.SendAfter.Value - now).TotalSeconds):0} s",
                    OutboxStatus.Queued => "Waiting to send" + (NetworkAvailable() ? "" : " (offline; sends when you are back online)"),
                    OutboxStatus.Sending => "Sending...",
                    OutboxStatus.Held => "Held",
                    OutboxStatus.Failed => "Not sent",
                    _ => i.Status.ToString(),
                };
                list.Add(new OutboxView(i, status, i.Problem, i.Status == OutboxStatus.Queued, i.Status is OutboxStatus.Held or OutboxStatus.Failed));
            }
        }
    }

    private static bool NetworkAvailable() => System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

    private void LoadMailReader(MailItem? item)
    {
        MailReader.Clear();
        MailReaderTitle = item?.Subject ?? "";
        if (item is null)
        {
            return;
        }

        var config = _config.Get();
        var zone = _config.Zone;

        // Newest first: a thread is opened to answer what came last.
        foreach (var m in _store.ThreadMessages(item.Key).Reverse())
        {
            var (text, quoted) = Core.Mail.MailText.SplitQuoted(m.Body ?? m.Snippet);
            MailReader.Add(new MessageView(
                Core.Mail.MailRules.DisplayName(m.Sender),
                m.Date is { } d ? TimeZoneInfo.ConvertTime(d, zone).ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture) : "",
                Recipients(m.To, m.Cc),
                text.Length > 0 ? text : "(no text)",
                quoted,
                config.Owner.IsMe(m.Sender)));
        }
    }

    private void LoadSlackReader(SlackItem? item)
    {
        SlackReader.Clear();
        SlackReaderTitle = item?.Where ?? "";
        if (item is null || _store.Conversation(item.Key) is not { } conversation)
        {
            return;
        }

        var me = _config.Get().Owner.SlackUserId;
        var zone = _config.Zone;
        foreach (var m in conversation.Messages.Reverse())
        {
            SlackReader.Add(new MessageView(
                m.FromId == me ? "You" : m.FromName,
                TimeZoneInfo.ConvertTime(m.When, zone).ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture),
                "",
                m.Text.Length > 0 ? Core.Slack.SlackRules.Readable(m.Text) : "(a file or attachment)",
                null,
                m.FromId == me));
        }
    }

    internal static string Recipients(IReadOnlyList<string> to, IReadOnlyList<string> cc)
    {
        static string Names(IEnumerable<string> list) => string.Join(", ", list.Select(Core.Mail.MailRules.DisplayName));
        var parts = new List<string>();
        if (to.Count > 0)
        {
            parts.Add("to " + Names(to));
        }

        if (cc.Count > 0)
        {
            parts.Add("cc " + Names(cc));
        }

        return string.Join("; ", parts);
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
        var (rows, counts) = _store.MailRows(_jobs.MailSince(now), _book.Handled);
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
                r.WaitingSince?.ToString("O", CultureInfo.InvariantCulture) ?? now.ToString("O", CultureInfo.InvariantCulture));
        }).ToList();
        var selected = SelectedMail?.Key;
        Swap(Mail, items, m => m);
        Reselect(selected, Mail, m => m.Key, SelectedMail, m => SelectedMail = m);

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
        if (counts.Handled > 0)
        {
            parts.Add($"{counts.Handled} handled");
        }

        MailSummary = string.Join(" · ", parts);
    }

    private void ReloadSlack(Core.Configuration.DocketConfig config, DateTimeOffset now)
    {
        var rows = _store.SlackRows(config, _jobs.SlackSince(now), _book.Handled);
        var owed = rows.Where(r => r.State == "Owed").Select(r => new SlackItem(
            r.Key,
            r.From,
            RelativeAge(now - r.When),
            now - r.When > TimeSpan.FromDays(3),
            r.IsDm ? $"DM · {r.Where}" : r.Where,
            Preview(Core.Slack.SlackRules.Readable(r.Text)),
            r.Why,
            r.LastTs)).ToList();
        var selected = SelectedSlack?.Key;
        Swap(Slack, owed, s => s);
        Reselect(selected, Slack, s => s.Key, SelectedSlack, s => SelectedSlack = s);
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

        return $"{connection} · calendar {When(JobPlans.Calendar)} · mail {When(JobPlans.Mail)} · Slack {When(JobPlans.Slack)} · {OutboxSummary()} · local model not set up";
    }

    private string OutboxSummary()
    {
        var waiting = _outbox.WithStatus(OutboxStatus.Queued, OutboxStatus.Sending).Count;
        var attention = _outbox.WithStatus(OutboxStatus.Held, OutboxStatus.Failed).Count;
        return (waiting, attention) switch
        {
            (0, 0) => "outbox empty",
            (_, 0) => $"outbox {waiting} sending",
            (0, _) => $"outbox {attention} need you",
            _ => $"outbox {waiting} sending, {attention} need you",
        };
    }

    /// <summary>
    /// Shows what the computer is missing (Claude Code, its sign-in, Git), from the window's
    /// <see cref="Core.Setup.SetupChecks"/>, or clears it with null.
    /// </summary>
    public void ShowComputerProblem(string? problem)
    {
        _computerProblem = problem;
        Banner = SetupProblem() ?? FailureBanner();
    }

    private string? _computerProblem;

    private string? SetupProblem()
    {
        // First: until Claude Code works, nothing else in the banner can be fixed by syncing.
        if (_computerProblem is { } computer)
        {
            return computer;
        }

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
        var failed = _lastSync?.Outcomes.Where(o => !o.Ok).Select(o => o.Detail).Distinct().ToList() ?? [];
        if (_dataProblem is { } data)
        {
            failed.Add(data);
        }

        return failed.Count > 0 ? string.Join(" ", failed) : null;
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

    private static string Preview(string text) => text.Length > 300 ? text[..300] + "…" : text;

    /// <summary>
    /// Keeps the conversation being read selected across a sync: the list's items are new objects,
    /// and a list that clears takes its selection with it. A conversation that left the list (he
    /// answered it elsewhere) closes.
    /// </summary>
    private static void Reselect<T>(string? key, IEnumerable<T> items, Func<T, string> keyOf, T? current, Action<T> select)
        where T : class
    {
        if (key is null)
        {
            return;
        }

        if (items.FirstOrDefault(i => keyOf(i) == key) is { } again && !ReferenceEquals(again, current))
        {
            select(again);
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

    /// <summary>
    /// Stops the timers and background work. Called twice on exit (the window's closing, then the
    /// service container), so it must be safe to repeat: a second Cancel on a disposed source threw
    /// and was reported as a failed start.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _countdown.Stop();
        _stop.Cancel();
        _stop.Dispose();
    }

    private bool _disposed;
}
