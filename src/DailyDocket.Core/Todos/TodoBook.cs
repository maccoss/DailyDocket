using DailyDocket.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Todos;

/// <summary>
/// The to-do list and Handled marks as the window works with them: each change is a file written at
/// once (so nothing is lost if the app closes) and pushed to the data repository shortly after.
/// </summary>
/// <remarks>
/// Syncing waits a few seconds after the last change, so ticking off several tasks makes one commit
/// rather than one each. Another machine's changes arrive with each sync and raise <see cref="Changed"/>.
/// </remarks>
public sealed class TodoBook : IDisposable
{
    /// <summary>How long after the last change the list is pushed.</summary>
    public static readonly TimeSpan SyncDelay = TimeSpan.FromSeconds(4);

    private readonly DataRepository _repo;
    private readonly DataSync _sync;
    private readonly string _machine;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly List<string> _pending = [];
    private Timer? _timer;

    public TodoBook(DataRepository repo, DataSync sync, string machine, ILogger<TodoBook>? log = null)
    {
        _repo = repo;
        _sync = sync;
        _machine = machine;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised when the list or the marks changed, here or on another machine, on any thread.</summary>
    public event Action? Changed;

    /// <summary>Raised after each sync with GitHub, on a background thread.</summary>
    public event Action<DataSyncResult>? Synced;

    public IReadOnlyList<TodoItem> Items { get; private set; } = [];

    public HandledSet Handled { get; private set; } = HandledSet.Empty;

    public DataSync Sync => _sync;

    public DataRepository Repository => _repo;

    /// <summary>Rereads the files: after a sync brought other machines' changes, or at startup.</summary>
    public void Reload()
    {
        Items = _repo.LoadTodos();
        Handled = new HandledSet(_repo.LoadHandled("email"), _repo.LoadHandled("slack"));
        Changed?.Invoke();
    }

    public TodoItem Add(string title, string notes, DateOnly? due, IReadOnlyList<string> tags, TodoSource? source = null)
    {
        var now = DateTimeOffset.Now;
        var item = new TodoItem(TodoRules.NewId(now), title.Trim(), notes.Trim(), due, tags, TodoRules.BottomRank(Items), false, null, now, source, now, _machine);
        Write(item, $"Add \"{Short(item.Title)}\"");
        return item;
    }

    public void Update(TodoItem item) => Write(item with { Updated = DateTimeOffset.Now, UpdatedOn = _machine }, $"Edit \"{Short(item.Title)}\"");

    public void SetDone(string id, bool done)
    {
        if (Find(id) is { } item)
        {
            var now = DateTimeOffset.Now;
            Write(item with { Done = done, DoneAt = done ? now : null, Updated = now, UpdatedOn = _machine }, $"{(done ? "Done" : "Reopen")} \"{Short(item.Title)}\"");
        }
    }

    public void Delete(string id)
    {
        if (Find(id) is not { } item)
        {
            return;
        }

        _repo.Delete(id);
        Items = [.. Items.Where(t => t.Id != id)];
        Changed?.Invoke();
        Schedule($"Remove \"{Short(item.Title)}\"");
    }

    /// <summary>Moves a task to just before <paramref name="beforeId"/> (or to the end): one file changes.</summary>
    public void Move(string id, string? beforeId)
    {
        if (Find(id) is not { } item)
        {
            return;
        }

        var order = TodoRules.OpenInOrder(Items).Where(t => t.Id != id).ToList();
        var at = beforeId is null ? order.Count : order.FindIndex(t => t.Id == beforeId);
        if (at < 0)
        {
            at = order.Count;
        }

        var rank = TodoRules.RankBetween(at > 0 ? order[at - 1] : null, at < order.Count ? order[at] : null);
        Write(item with { Rank = rank, Updated = DateTimeOffset.Now, UpdatedOn = _machine }, $"Move \"{Short(item.Title)}\"");
    }

    /// <summary>Moves a task one place up or down the open list.</summary>
    public void Nudge(string id, int direction)
    {
        var order = TodoRules.OpenInOrder(Items).ToList();
        var i = order.FindIndex(t => t.Id == id);
        var j = i + direction;
        if (i < 0 || j < 0 || j >= order.Count)
        {
            return;
        }

        Move(id, direction < 0 ? order[j].Id : j + 1 < order.Count ? order[j + 1].Id : null);
    }

    /// <summary>Marks a conversation handled up to its newest message; it returns if someone writes again.</summary>
    public void MarkHandled(string kind, string key, string upTo)
    {
        var mark = new HandledMark(kind, key, upTo, DateTimeOffset.Now, _machine);
        _repo.Save(mark);
        Handled = kind == "email"
            ? Handled with { Email = new Dictionary<string, HandledMark>(Handled.Email) { [key] = mark } }
            : Handled with { Slack = new Dictionary<string, HandledMark>(Handled.Slack) { [key] = mark } };
        Changed?.Invoke();
        Schedule($"Handled {kind} {key}");
    }

    public void Unhandle(string kind, string key)
    {
        _repo.RemoveHandled(kind, key);
        var email = new Dictionary<string, HandledMark>(Handled.Email);
        var slack = new Dictionary<string, HandledMark>(Handled.Slack);
        (kind == "email" ? email : slack).Remove(key);
        Handled = new HandledSet(email, slack);
        Changed?.Invoke();
        Schedule($"Unhandled {kind} {key}");
    }

    /// <summary>Pushes now (Refresh, startup), with anything still waiting.</summary>
    public async Task<DataSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        string summary;
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            summary = Summary();
        }

        var result = await _sync.SyncAsync(summary, cancellationToken).ConfigureAwait(false);
        if (result.Changed || result.SetAside is not null)
        {
            Reload();
        }

        if (!result.Ok)
        {
            _log.LogWarning("Data sync: {Problem}", result.Problem);
        }

        Synced?.Invoke(result);
        return result;
    }

    private void Write(TodoItem item, string summary)
    {
        _repo.Save(item);
        Items = [.. Items.Where(t => t.Id != item.Id), item];
        Changed?.Invoke();
        Schedule(summary);
    }

    private void Schedule(string summary)
    {
        lock (_lock)
        {
            _pending.Add(summary);
            _timer?.Dispose();
            _timer = new Timer(_ => _ = SyncNowAsync(), null, SyncDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The commit message: the one change, or how many.</summary>
    private string Summary()
    {
        var text = _pending.Count switch
        {
            0 => "Sync",
            1 => _pending[0],
            _ => $"{_pending[0]} and {_pending.Count - 1} more",
        };
        _pending.Clear();
        return text;
    }

    private TodoItem? Find(string id) => Items.FirstOrDefault(t => t.Id == id);

    private static string Short(string title) => title.Length <= 50 ? title : title[..50] + "...";

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
