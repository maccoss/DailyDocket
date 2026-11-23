using DailyDocket.Core.Calendar;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Prep;

/// <summary>How one event's brief went.</summary>
public sealed record PrepResult(string EventId, string Title, JobOutcome Outcome);

/// <summary>
/// Makes meeting briefs: picks the events that need one, runs one read-only prep run per event, and
/// saves each brief in the data repository, where the existing sync shares it with every machine.
/// </summary>
/// <remarks>
/// One run per event rather than one for the night: each brief's cost is visible, a confusing event
/// cannot spoil the others, and write_brief can only ever write the one meeting its run was given.
/// </remarks>
public sealed class MeetingPrep
{
    private readonly Func<JobPlan, CancellationToken, Task<JobOutcome>> _run;
    private readonly Func<string> _repositoryRoot;
    private readonly Func<DocketConfig> _config;
    private readonly Func<string> _model;
    private readonly Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<CalendarEvent>> _events;
    private readonly Func<IReadOnlyList<string>> _openItems;
    private readonly Action<string> _changed;
    private readonly string _machine;
    private readonly ILogger _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    public MeetingPrep(
        Func<JobPlan, CancellationToken, Task<JobOutcome>> run,
        Func<string> repositoryRoot,
        Func<DocketConfig> config,
        Func<string> model,
        Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<CalendarEvent>> events,
        Func<IReadOnlyList<string>> openItems,
        Action<string> changed,
        string machine,
        ILogger<MeetingPrep>? log = null)
    {
        _run = run;
        _repositoryRoot = repositoryRoot;
        _config = config;
        _model = model;
        _events = events;
        _openItems = openItems;
        _changed = changed;
        _machine = machine;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised when a brief was saved, on a background thread.</summary>
    public event Action<MeetingBrief>? BriefSaved;

    /// <summary>The events in the next working days with no brief, or a stale one, capped per run.</summary>
    public IReadOnlyList<PrepTarget> Due(DateTimeOffset now)
    {
        var config = _config();
        var zone = Zone(config);
        var targets = PrepRules.Select(_events(now, now.AddDays(config.Prep.WorkingDays + 4)), config, now, zone);
        var briefs = BriefFile.LoadDays(_repositoryRoot(), targets.Select(t => t.Date).Distinct());
        return [.. targets.Where(t => PrepRules.IsStale(briefs.GetValueOrDefault(t.Event.EventId), t.Event)).Take(Math.Max(0, config.Prep.MaxPerRun))];
    }

    /// <summary>
    /// Preps everything due now (the nightly run and the midday catch-up), or only the events in
    /// <paramref name="only"/> that are still due (a retry of the briefs that failed). An event
    /// being prepared right now (Prep now) is left to that run.
    /// </summary>
    public async Task<IReadOnlyList<PrepResult>> RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken, IReadOnlyCollection<string>? only = null)
    {
        var results = new List<PrepResult>();
        foreach (var target in Due(now).Where(t => (only is null || only.Contains(t.Event.EventId)) && !IsPreparing(t.Event.EventId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await PrepAsync(target, cancellationToken).ConfigureAwait(false) is { } outcome)
            {
                results.Add(new PrepResult(target.Event.EventId, target.Event.Title, outcome));
            }
        }

        _log.LogInformation("Meeting prep: {Count} briefs attempted, {Ok} ok, ${Cost:0.00}", results.Count, results.Count(r => r.Outcome.Ok), results.Sum(r => r.Outcome.CostUsd ?? 0));
        return results;
    }

    /// <summary>True while a brief for <paramref name="eventId"/> is being written on this machine.</summary>
    public bool IsPreparing(string eventId) => _inFlight.ContainsKey(eventId);

    /// <summary>
    /// Preps one event on demand ("Prep now"), whatever the selection rules say: Mike asked for it.
    /// Null when the event is not in the local calendar.
    /// </summary>
    public async Task<JobOutcome?> PrepNowAsync(string eventId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var config = _config();
        var zone = Zone(config);
        var e = _events(now.AddDays(-1), now.AddDays(config.Schedule.Days + 2)).FirstOrDefault(x => x.EventId == eventId);
        if (e is null)
        {
            return null;
        }

        var target = new PrepTarget(e, config.Calendars.FirstOrDefault(c => c.Key == e.CalendarKey)?.Id, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Start, zone).DateTime));
        return await PrepAsync(target, cancellationToken).ConfigureAwait(false)
            ?? new JobOutcome("prep", false, "Claude is already preparing this brief (the nightly run got to it first). It will appear here when it is done.", 0, null);
    }

    /// <summary>The briefs for the given days, by event id, for the Schedule tab.</summary>
    public IReadOnlyDictionary<string, MeetingBrief> Briefs(IEnumerable<DateOnly> days) => BriefFile.LoadDays(_repositoryRoot(), days);

    /// <summary>One prep run, or null when this event's brief is already being written here.</summary>
    /// <remarks>
    /// Prep now and the nightly run share this, so the same meeting is never prepared twice at
    /// once (two runs paid for, and two writes of one file racing).
    /// </remarks>
    private async Task<JobOutcome?> PrepAsync(PrepTarget target, CancellationToken cancellationToken)
    {
        if (!_inFlight.TryAdd(target.Event.EventId, 0))
        {
            return null;
        }

        try
        {
            return await PrepOnceAsync(target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(target.Event.EventId, out _);
        }
    }

    private async Task<JobOutcome> PrepOnceAsync(PrepTarget target, CancellationToken cancellationToken)
    {
        var config = _config();
        var model = _model();
        var saved = false;
        string Save(PrepTarget t, MeetingBrief brief)
        {
            var complete = BriefFile.Clean(brief with { Written = DateTimeOffset.Now, WrittenOn = _machine, Model = model });
            BriefFile.Save(_repositoryRoot(), t.Date, complete);
            saved = true;
            _changed($"Brief for \"{Short(t.Event.Title)}\" on {t.Date:yyyy-MM-dd}");
            BriefSaved?.Invoke(complete);
            return $"Saved the brief for {t.Event.Title}.";
        }

        var plan = JobPlans.PrepRun(target, config, Zone(config), _openItems(), model, Save);
        var outcome = await _run(plan, cancellationToken).ConfigureAwait(false);
        if (outcome.Ok && !saved)
        {
            // The run ended well by its own account but never saved: count it as failed so the
            // event is tried again rather than left without a brief.
            outcome = outcome with { Ok = false, Detail = $"Meeting prep for \"{target.Event.Title}\" finished without writing a brief. It will try again at the next run." };
        }

        _log.LogInformation("Prep {EventId}: {Outcome}, ${Cost:0.000}", target.Event.EventId, outcome.Ok ? "ok" : "failed", outcome.CostUsd ?? 0);
        return outcome;
    }

    private static string Short(string title) => title.Length <= 50 ? title : title[..50] + "...";

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
