using System.Globalization;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Todos;

namespace DailyDocket.Data;

/// <summary>The cache, as the jobs see it.</summary>
public sealed class JobStore : IJobStore
{
    private readonly Store _store;
    private readonly CaptureIngestor _ingestor;
    private readonly Func<DocketConfig> _config;
    private readonly Func<HandledSet> _handled;

    public JobStore(Store store, CaptureIngestor ingestor, Func<DocketConfig> config, Func<HandledSet>? handled = null)
    {
        _store = store;
        _ingestor = ingestor;
        _config = config;
        _handled = handled ?? (() => HandledSet.Empty);
    }

    public int IngestFolder(string runId, string folder) => _ingestor.IngestFolder(runId, folder);

    public IReadOnlyList<string> ThreadsToRead(DateTimeOffset seenSince, int max) => _store.ThreadsToRead(seenSince, max);

    /// <summary>Mail first (it is where live requests are), then Slack, newest first within each.</summary>
    public IReadOnlyList<TriageItem> TriageBatch(DateTimeOffset now, int max)
    {
        var config = _config();
        var handled = _handled();
        var mail = _store.MailToTriage(config, MailSince(now), max, handled, Deep(now));
        var slack = mail.Count < max ? _store.SlackToTriage(config, SlackSince(now), max - mail.Count, handled) : [];
        return [.. mail, .. slack];
    }

    /// <summary>
    /// Called after each verdict is cached, to share it through the data repository. Set once by
    /// the app; a test's JobStore leaves it null.
    /// </summary>
    public Action<string, string, string, bool, string>? VerdictRecorded { get; set; }

    public void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why)
    {
        _store.RecordVerdict(source, key, forMessage, needsReply, why);
        VerdictRecorded?.Invoke(source, key, forMessage, needsReply, why);
    }

    /// <summary>
    /// What the last nightly deep sweep reached: threads it saw since it began (a minute's slack, as
    /// for the full sweep) whose wait began before the live window. Null before any deep sweep.
    /// </summary>
    public DeepWindow? Deep(DateTimeOffset now) =>
        DateTimeOffset.TryParse(_store.Watermark("mail", "deep"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)
            ? new DeepWindow(start.AddMinutes(-1), now.AddDays(-_config().Mail.WindowDays))
            : null;

    public string? Watermark(string source, string scope) => _store.Watermark(source, scope);

    public void SetWatermark(string source, string scope, string value) => _store.SetWatermark(source, scope, value);

    public void StartRun(string id, string job, DateTimeOffset at) => _store.StartRun(id, job, at);

    public void FinishRun(string id, string? sessionId, string outcome, string? detail, double? costUsd) =>
        _store.FinishRun(id, sessionId, outcome, detail, costUsd);

    public int PurgeCaptures(DateTimeOffset now) => _store.PurgeCaptures(now);

    /// <summary>
    /// The Email tab shows threads seen since the last full sweep began: a thread archived since
    /// then is not in the inbox any more. Before any full sweep, the window.
    /// </summary>
    public DateTimeOffset MailSince(DateTimeOffset now)
    {
        var window = now.AddDays(-_config().Mail.WindowDays);
        return DateTimeOffset.TryParse(_store.Watermark("mail", "full"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var full)
            ? full.AddMinutes(-1)
            : window;
    }

    public DateTimeOffset SlackSince(DateTimeOffset now) => now.AddDays(-_config().Slack.WindowDays);
}
