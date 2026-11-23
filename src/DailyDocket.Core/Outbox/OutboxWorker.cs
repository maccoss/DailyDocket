using System.Net.NetworkInformation;
using DailyDocket.Core.Capture;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Outbox;

/// <summary>Where the outbox is kept. The Data project implements it.</summary>
public interface IOutboxStore
{
    void Save(OutboxItem item);

    OutboxItem? Get(string id);

    IReadOnlyList<OutboxItem> WithStatus(params OutboxStatus[] statuses);

    void Delete(string id);
}

/// <summary>
/// Sends queued items, one at a time, once their undo window has passed and the network is up.
/// </summary>
/// <remarks>
/// Nothing reaches here that Mike did not send: an item enters Queued only from his Send button.
/// Each send is a fresh read (Held if the conversation moved on) and then one guarded call. An item
/// found in Sending at startup was interrupted (the machine slept, the app closed): it is never
/// resent blindly, but held for Mike to check.
/// </remarks>
public sealed class OutboxWorker
{
    private readonly IOutboxStore _store;
    private readonly JobRunner _runner;
    private readonly Func<(string Model, bool IncludeSignature, string Machine)> _settings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OutboxWorker(IOutboxStore store, JobRunner runner, Func<(string Model, bool IncludeSignature, string Machine)> settings, ILogger<OutboxWorker>? log = null)
    {
        _store = store;
        _runner = runner;
        _settings = settings;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised when any item changes status, on a background thread.</summary>
    public event Action<OutboxItem>? Changed;

    /// <summary>Items interrupted mid-send go to Held, never back to the queue.</summary>
    public void RecoverInterrupted()
    {
        foreach (var item in _store.WithStatus(OutboxStatus.Sending))
        {
            // Marking a thread read can safely happen twice, so it simply goes again.
            if (OutboxRules.IsHousekeeping(item.Kind))
            {
                Update(item with { Status = OutboxStatus.Queued, SendAfter = DateTimeOffset.UtcNow });
                continue;
            }

            Update(item with
            {
                Status = OutboxStatus.Held,
                Problem = "DailyDocket stopped while sending this. Check the conversation for it before sending again, so it does not go twice.",
            });
        }
    }

    /// <summary>Checks the queue every few seconds until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        RecoverInterrupted();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await SendDueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "The outbox worker failed");
            }
        }
    }

    /// <summary>Sends every item whose undo window has passed.</summary>
    public async Task SendDueAsync(CancellationToken cancellationToken = default)
    {
        if (!NetworkInterface.GetIsNetworkAvailable() || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in _store.WithStatus(OutboxStatus.Queued).Where(i => i.SendAfter <= now).OrderBy(i => i.SendAfter))
            {
                // Reread: Undo may have pulled it back since the list was taken.
                if (_store.Get(item.Id) is { Status: OutboxStatus.Queued } current)
                {
                    await SendAsync(current, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task SendAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        var (model, signature, machine) = _settings();
        item = Update(item with { Status = OutboxStatus.Sending, ClaimedAt = DateTimeOffset.UtcNow, Machine = machine, Attempts = item.Attempts + 1, Problem = null });
        _log.LogInformation("Sending outbox item {Id} ({Kind})", item.Id, item.Kind);

        if (OutboxRules.NeedsPreCheck(item.Kind))
        {
            if (!await StillCurrentAsync(item, model, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await DeliverAsync(item, signature, model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A fresh read of the conversation: false (and the item requeued or held) when it cannot be
    /// read or someone wrote since he started.
    /// </summary>
    private async Task<bool> StillCurrentAsync(OutboxItem item, string model, CancellationToken cancellationToken)
    {
        var check = await _runner.RunAsync(SendPlans.PreCheck(item, model), cancellationToken).ConfigureAwait(false);
        var fresh = check.Ok ? Captured(check.CaptureFolder, SendPlans.PreCheckCall(item).Tool) : null;
        if (fresh is null)
        {
            // Offline or the connector failed: back in the queue, a little later.
            Update(item with { Status = OutboxStatus.Queued, SendAfter = DateTimeOffset.UtcNow.AddMinutes(2), Problem = check.Detail ?? "The pre-send check could not read the conversation; trying again shortly." });
            return false;
        }

        var movedOn = item.Kind == OutboxKind.EmailReply
            ? OutboxRules.EmailMovedOn(GmailParser.ParseThread(fresh).Messages, item.Base)
            : OutboxRules.SlackMovedOn(fresh, item.Base);
        if (movedOn)
        {
            Update(item with { Status = OutboxStatus.Held, Problem = "Someone wrote in this conversation after you started your reply. Read it, then send again, edit, or discard." });
            return false;
        }

        return true;
    }

    /// <summary>The one guarded call.</summary>
    private async Task DeliverAsync(OutboxItem item, bool signature, string model, CancellationToken cancellationToken)
    {
        var call = OutboxRules.Call(item, signature);
        var sent = await _runner.RunAsync(SendPlans.Send(item, call, model), cancellationToken).ConfigureAwait(false);
        var result = Captured(sent.CaptureFolder, call.ToolName);
        if (result is not null)
        {
            Update(item with { Status = OutboxStatus.Sent, RemoteId = OutboxRules.RemoteId(result) ?? "sent", Problem = null });
            _log.LogInformation("Outbox item {Id} sent", item.Id);
            return;
        }

        // Housekeeping Mike never sees (marking read) is dropped rather than held where nothing
        // shows it: the next reply or Handled marks the thread read again.
        if (OutboxRules.IsHousekeeping(item.Kind))
        {
            _store.Delete(item.Id);
            _log.LogInformation("Outbox item {Id} ({Kind}) not confirmed; dropped", item.Id, item.Kind);
            return;
        }

        var (status, problem) = OutboxRules.AfterUnconfirmedSend(sent.Detail, sent.FinalText, sent.CallLetThrough);
        Update(item with { Status = status, Problem = problem });
        _log.LogWarning("Outbox item {Id} not confirmed: {Status}", item.Id, status);
    }

    /// <summary>The response text of the first capture of <paramref name="tool"/> in a run's folder.</summary>
    private static string? Captured(string? folder, string tool)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var call = CapturedCall.Parse(File.ReadAllText(file), DateTimeOffset.UtcNow);
            if (call.ToolName == tool)
            {
                return call.ResponseText;
            }
        }

        return null;
    }

    private OutboxItem Update(OutboxItem item)
    {
        var updated = item with { Updated = DateTimeOffset.UtcNow };
        _store.Save(updated);
        Changed?.Invoke(updated);
        return updated;
    }
}
