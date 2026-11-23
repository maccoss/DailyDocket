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
        _shared.Prep.BriefSaved += _ => OnUi(Reload);
    }

    /// <summary>Starts the nightly schedule, once the configuration is complete.</summary>
    private void StartShared() => _shared.Start(_stop.Token);

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
