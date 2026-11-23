using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DailyDocket.App.Services;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Outbox;
using DailyDocket.Data;

namespace DailyDocket.App.ViewModels;

/// <summary>
/// The reply box under a thread or conversation: recipients, the text (in the view's rich text
/// box), and Send. Everything is saved to the outbox as a draft on every pause, so nothing typed is
/// lost if the app closes, and Send only queues it behind the undo window.
/// </summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    private readonly OutboxStore _outbox;
    private readonly AppSettings _settings;
    private readonly Drafter? _drafter;
    private OutboxItem? _item;
    private Func<string>? _conversation;

    public ComposeViewModel(OutboxStore outbox, AppSettings settings, OutboxKind kind, Drafter? drafter = null)
    {
        _outbox = outbox;
        _settings = settings;
        _drafter = drafter;
        Kind = kind;
    }

    /// <summary>
    /// What the box holds right now, supplied by the view. Saving on a typing pause alone lost the
    /// last second or so of typing when Mike switched threads or closed the app before it fired.
    /// </summary>
    public Func<ComposedText>? CurrentContent { get; set; }

    /// <summary>Saves what is in the box now, if a draft is open: before switching away or closing.</summary>
    public void SaveCurrent()
    {
        if (_item is not null && IsOpen && CurrentContent is { } current)
        {
            SaveDraft(current());
        }
    }

    /// <summary>Claude's draft, as light Markdown, for the view to put in the box.</summary>
    public event Action<string>? DraftReady;

    /// <summary>What Mike wants the reply to say, for Draft with Claude (optional).</summary>
    [ObservableProperty]
    public partial string Guidance { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DraftWithClaudeCommand))]
    public partial bool IsDrafting { get; set; }

    /// <summary>
    /// Asks Claude for a draft of this reply, from the conversation in the cache. The draft only
    /// fills the box: Mike edits it and presses Send himself, or not.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDraft))]
    private async Task DraftWithClaudeAsync()
    {
        if (_drafter is null || _conversation is null)
        {
            return;
        }

        IsDrafting = true;
        Note = "Claude is drafting...";
        try
        {
            var (draft, guide) = await _drafter.DraftAsync(IsEmail, _conversation(), Guidance, CancellationToken.None);
            DraftReady?.Invoke(draft);
            Note = guide is null
                ? "Claude's draft is in the box. Read and edit it before sending."
                : $"Claude's draft, following your guide \"{guide}\", is in the box. Read and edit it before sending.";
        }
        catch (InvalidOperationException e)
        {
            Note = e.Message;
        }
        finally
        {
            IsDrafting = false;
        }
    }

    private bool CanDraft() => !IsDrafting && _drafter is not null && _conversation is not null;

    /// <summary>Asks the view to show this document (a reopened draft), or an empty one.</summary>
    public event Action<string?>? LoadRequested;

    /// <summary>Raised when an item is queued, so the window can offer Undo.</summary>
    public event Action<OutboxItem>? Queued;

    public OutboxKind Kind { get; }

    public bool IsEmail => Kind == OutboxKind.EmailReply;

    public ObservableCollection<string> To { get; } = [];

    public ObservableCollection<string> Cc { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string NewTo { get; set; } = "";

    [ObservableProperty]
    public partial string NewCc { get; set; } = "";

    [ObservableProperty]
    public partial string Heading { get; set; } = "";

    /// <summary>"Draft saved", or why Send was refused.</summary>
    [ObservableProperty]
    public partial string? Note { get; set; }

    /// <summary>Opens a new reply, or this conversation's existing draft if there is one.</summary>
    public void Open(string target, string? replyTo, string? channel, string subject, string baseMessage, IReadOnlyList<string> to, IReadOnlyList<string> cc, string heading, Func<string>? conversation = null)
    {
        SaveCurrent();
        _conversation = conversation;
        Guidance = "";
        DraftWithClaudeCommand.NotifyCanExecuteChanged();
        var draft = _outbox.ForTarget(target).FirstOrDefault(i => i.Status == OutboxStatus.Draft && i.Kind == Kind);
        _item = draft ?? new OutboxItem(
            Guid.NewGuid().ToString("N"), Kind, OutboxStatus.Draft, target, replyTo, channel, to, cc, subject, baseMessage,
            "", "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, AppInfo.MachineName, 0, null, null);

        Reset(To, _item.To);
        Reset(Cc, _item.Cc);
        Heading = draft is null ? heading : "Your draft";
        Note = draft is null ? null : "Reopened your unsent draft.";
        IsOpen = true;
        LoadRequested?.Invoke(draft?.Document);
    }

    /// <summary>Reopens an outbox item (held, failed, or pulled back by Undo) for editing.</summary>
    public void Edit(OutboxItem item, Func<string>? conversation = null)
    {
        SaveCurrent();
        _conversation = conversation;
        DraftWithClaudeCommand.NotifyCanExecuteChanged();
        // A Gmail draft reopened here becomes a reply again: this box sends replies.
        _item = item with { Kind = Kind, Status = OutboxStatus.Draft, SendAfter = null, Problem = null };
        _outbox.Save(_item);
        Reset(To, _item.To);
        Reset(Cc, _item.Cc);
        Heading = "Your draft";
        Note = null;
        IsOpen = true;
        LoadRequested?.Invoke(_item.Document);
    }

    /// <summary>Whether this box is showing a draft for <paramref name="target"/>.</summary>
    public bool IsFor(string target) => IsOpen && _item?.Target == target;

    [RelayCommand]
    private void AddTo() => NewTo = Add(To, NewTo);

    [RelayCommand]
    private void AddCc() => NewCc = Add(Cc, NewCc);

    [RelayCommand]
    private void RemoveTo(string? address)
    {
        if (address is not null)
        {
            To.Remove(address);
        }
    }

    [RelayCommand]
    private void RemoveCc(string? address)
    {
        if (address is not null)
        {
            Cc.Remove(address);
        }
    }

    /// <summary>Saves what is in the box as a draft: called by the view on every typing pause.</summary>
    public void SaveDraft(ComposedText content)
    {
        if (_item is null || (content.IsEmpty && _item.Document.Length == 0))
        {
            return;
        }

        _item = Fill(_item, content) with { Status = OutboxStatus.Draft };
        _outbox.Save(_item);
        Note = $"Draft saved {DateTime.Now:h:mm tt}";
    }

    /// <summary>Queues the reply behind the undo window. Nothing leaves before it passes.</summary>
    public bool Send(ComposedText content)
    {
        if (_item is null)
        {
            return false;
        }

        // A typed but not yet added address still counts: people expect what they see in the box to be used.
        AddTo();
        AddCc();

        // What is left in a box was not an address. Sending without that person, silently, would
        // be worse than asking: the Note already says which text was not understood.
        if (NewTo.Trim().Length > 0 || NewCc.Trim().Length > 0)
        {
            return false;
        }

        if (content.IsEmpty)
        {
            Note = "Write something before sending.";
            return false;
        }

        if (IsEmail && To.Count == 0)
        {
            Note = "Add at least one recipient.";
            return false;
        }

        var seconds = Math.Max(0, _settings.UndoWindowSeconds);
        var queued = Fill(_item, content) with { Status = OutboxStatus.Queued, SendAfter = DateTimeOffset.UtcNow.AddSeconds(seconds), Problem = null };
        _outbox.Save(queued);
        _item = null;
        IsOpen = false;
        Note = null;
        Queued?.Invoke(queued);
        return true;
    }

    /// <summary>
    /// Puts the reply in Gmail's drafts instead of sending it, to finish on the phone or in Gmail
    /// (an attachment, for example). It goes through the outbox like a send, without the undo
    /// window, since nothing reaches anyone.
    /// </summary>
    public bool SaveToGmail(ComposedText content)
    {
        if (_item is null || !IsEmail)
        {
            return false;
        }

        AddTo();
        AddCc();
        if (NewTo.Trim().Length > 0 || NewCc.Trim().Length > 0)
        {
            return false;
        }

        if (content.IsEmpty)
        {
            Note = "Write something before saving it to Gmail.";
            return false;
        }

        var draft = Fill(_item, content) with { Kind = OutboxKind.EmailDraft, Status = OutboxStatus.Queued, SendAfter = DateTimeOffset.UtcNow, Problem = null };
        _outbox.Save(draft);
        _item = null;
        IsOpen = false;
        Note = null;
        Queued?.Invoke(draft);
        return true;
    }

    [RelayCommand]
    private void Discard()
    {
        if (_item is not null && _outbox.Get(_item.Id) is not null)
        {
            _outbox.Delete(_item.Id);
        }

        _item = null;
        IsOpen = false;
        Note = null;
    }

    /// <summary>Closes the box, keeping the draft (it reopens with the conversation).</summary>
    [RelayCommand]
    private void Close()
    {
        SaveCurrent();
        _item = null;
        IsOpen = false;
    }

    private OutboxItem Fill(OutboxItem item, ComposedText content) => item with
    {
        To = [.. To],
        Cc = [.. Cc],
        Document = content.Xaml,
        Html = IsEmail ? content.Html : "",
        Text = IsEmail ? content.Text : content.Markdown,
        Updated = DateTimeOffset.UtcNow,
    };

    private string Add(ObservableCollection<string> list, string typed)
    {
        var leftover = new List<string>();
        foreach (var part in typed.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var address = OutboxRules.Address(part);
            if (OutboxRules.IsAddress(address))
            {
                if (!list.Contains(address, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(address);
                }
            }
            else
            {
                leftover.Add(part);
            }
        }

        if (leftover.Count > 0)
        {
            Note = $"\"{string.Join(" ", leftover)}\" is not an email address.";
        }

        return string.Join(" ", leftover);
    }

    private static void Reset(ObservableCollection<string> list, IEnumerable<string> items)
    {
        list.Clear();
        foreach (var i in items)
        {
            list.Add(i);
        }
    }
}
