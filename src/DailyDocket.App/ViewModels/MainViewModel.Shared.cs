using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DailyDocket.App.Services;
using DailyDocket.Core.Calendar;
using DailyDocket.Core.Prep;
using Microsoft.Extensions.Logging;

namespace DailyDocket.App.ViewModels;

/// <summary>Phase 6 on the window: meeting briefs under their events, Prep now, and the nightly schedule.</summary>
/// <remarks>A separate part of the class so the phase's additions stay apart from the tabs' code.</remarks>
public sealed partial class MainViewModel
{
    private readonly SharedJobs _shared;
    private readonly HashSet<string> _prepping = [];
    private readonly Dictionary<string, string> _prepNotes = [];

    /// <summary>Called once from the constructor.</summary>
    private void AttachShared()
    {
        // A brief written here (Prep now, the nightly run) or arriving from another machine's sync.
        Action<MeetingBrief> briefSaved = _ => OnUi(Reload);
        _shared.Prep.BriefSaved += briefSaved;
        _detach.Add(() => _shared.Prep.BriefSaved -= briefSaved);

        // Verdicts another machine shared can take threads off the Email and Slack tabs.
        Action cacheChanged = () => OnUi(Reload);
        _shared.CacheChanged += cacheChanged;
        _detach.Add(() => _shared.CacheChanged -= cacheChanged);

        // After a sync's results are on the tabs (queued after the reload above it), and whenever
        // an outbox item changes: anything new that needs Mike becomes a notification.
        Action<Core.Jobs.SyncResult> notices = _ => OnUi(CheckNotices);
        _scheduler.Finished += notices;
        _detach.Add(() => _scheduler.Finished -= notices);
        Action<Core.Outbox.OutboxItem> outboxNotices = _ => OnUi(CheckNotices);
        _worker.Changed += outboxNotices;
        _detach.Add(() => _worker.Changed -= outboxNotices);

        // A nightly run's problem stays in the banner until a later run goes well: the sweep and
        // the briefs run unattended at 2 a.m., so this is the only place a failure can be seen.
        Action<Core.Nightly.NightlyTick> nightly = tick => OnUi(() =>
        {
            if (tick.Ran)
            {
                _nightlyProblem = tick.Problem;
                Banner = SetupProblem() ?? FailureBanner();
            }
        });
        _shared.Nightly.Finished += nightly;
        _detach.Add(() => _shared.Nightly.Finished -= nightly);
    }

    private string? _nightlyProblem;

    /// <summary>Starts the nightly schedule, once the configuration is complete, and takes the notifications' baseline.</summary>
    private void StartShared()
    {
        CheckNotices();
        _shared.Start(_stop.Token);
    }

    /// <summary>Every brief for the days the Schedule shows, by event id; read from disk, so offline too.</summary>
    private IReadOnlyDictionary<string, MeetingBrief> LoadBriefs(DateOnly today, int days)
    {
        try
        {
            return _shared.Prep.Briefs(Enumerable.Range(-1, days + 3).Select(today.AddDays));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Reading the meeting briefs failed");
            return new Dictionary<string, MeetingBrief>();
        }
    }

    private BriefView? BriefFor(IReadOnlyDictionary<string, MeetingBrief> briefs, CalendarEvent e)
    {
        if (!briefs.TryGetValue(e.EventId, out var b))
        {
            return null;
        }

        var zone = _config.Zone;
        var stale = PrepRules.IsStale(b, e);
        var written = TimeZoneInfo.ConvertTime(b.Written, zone).ToString("ddd h:mmt", CultureInfo.InvariantCulture).ToLowerInvariant();
        var note = $"Brief by Claude, {written}" + (b.WrittenOn.Length > 0 ? $" on {b.WrittenOn}" : "") + (stale ? " · may be out of date: the event changed since" : "");
        return new BriefView(
            [.. b.Attendees.Select(a => a.Name + (a.Org.Length > 0 ? $" ({a.Org})" : "") + (a.Role.Length > 0 ? $": {a.Role}" : "") + (a.Organizer ? ", organizer" : ""))],
            b.Agenda,
            b.Context,
            b.Owes,
            b.Gaps,
            note,
            stale);
    }

    /// <summary>
    /// Writes (or rewrites) the brief for one event now, whatever the nightly rules say: Mike asked.
    /// Online only; the note under the event says how it went.
    /// </summary>
    [RelayCommand]
    private async Task PrepNowAsync(EventItem? e)
    {
        if (e is null || e.EventId.Length == 0 || !_prepping.Add(e.EventId))
        {
            return;
        }

        if (!NetworkAvailable())
        {
            _prepping.Remove(e.EventId);
            _prepNotes[e.EventId] = "Prep now needs a connection. Try again when you are back online.";
            Reload();
            return;
        }

        _prepNotes[e.EventId] = "Claude is preparing a brief; this takes a minute or two...";
        Reload();
        try
        {
            var outcome = await _shared.Prep.PrepNowAsync(e.EventId, DateTimeOffset.UtcNow, _stop.Token);
            if (outcome is null)
            {
                _prepNotes[e.EventId] = "This event is not in the local calendar any more. Refresh, then try again.";
            }
            else if (!outcome.Ok)
            {
                _prepNotes[e.EventId] = outcome.Detail ?? "The brief could not be written. Try again later.";
            }
            else
            {
                _prepNotes.Remove(e.EventId);
            }
        }
        catch (OperationCanceledException)
        {
            return; // Closing.
        }
        finally
        {
            _prepping.Remove(e.EventId);
        }

        Reload();
    }
}
