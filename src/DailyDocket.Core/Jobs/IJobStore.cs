namespace DailyDocket.Core.Jobs;

/// <summary>An item waiting for Claude's verdict.</summary>
/// <param name="Source">mail or slack.</param>
/// <param name="ForMessage">The newest message the verdict will be kept against.</param>
public sealed record TriageItem(string Source, string Key, string ForMessage, string Transcript);

/// <summary>What the jobs need from the cache. The Data project implements it.</summary>
public interface IJobStore
{
    /// <summary>Stores and parses captures not seen before; returns how many were new.</summary>
    int IngestFolder(string runId, string folder);

    /// <summary>Threads seen since <paramref name="seenSince"/> that need reading.</summary>
    IReadOnlyList<string> ThreadsToRead(DateTimeOffset seenSince, int max);

    IReadOnlyList<TriageItem> TriageBatch(DateTimeOffset now, int max);

    void RecordVerdict(string source, string key, string forMessage, bool needsReply, string why);

    string? Watermark(string source, string scope);

    void SetWatermark(string source, string scope, string value);

    void StartRun(string id, string job, DateTimeOffset at);

    void FinishRun(string id, string? sessionId, string outcome, string? detail, double? costUsd);

    int PurgeCaptures(DateTimeOffset now);
}
