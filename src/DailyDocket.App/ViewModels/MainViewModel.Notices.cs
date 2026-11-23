using CommunityToolkit.Mvvm.ComponentModel;
using DailyDocket.Core.Notifications;
using DailyDocket.Core.Outbox;

namespace DailyDocket.App.ViewModels;

/// <summary>Notifications: what is new since the last look, and opening the window on it.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The tabs in the window's order, for <see cref="SelectedTab"/>.</summary>
    private const int ScheduleTab = 0, EmailTab = 2, SlackTab = 3;

    private IReadOnlySet<string>? _noticed;

    /// <summary>Raised on the UI thread for each notification to show.</summary>
    public event Action<Notice>? NoticeRaised;

    /// <summary>The tab showing, so a clicked notification can open the right one.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>
    /// Compares what needs Mike now with the last look and raises a notice for anything new. The
    /// first look (at start, from the cache) only sets the baseline.
    /// </summary>
    internal void CheckNotices()
    {
        var items = new List<AttentionItem>();
        items.AddRange(Mail.Where(m => !m.IsDraft).Select(m => new AttentionItem(
            NoticeTab.Email, m.Key, m.Key, $"{m.From} is waiting on a reply", m.Why is { Length: > 0 } why ? $"\"{m.Subject}\": {why}" : $"\"{m.Subject}\"")));
        items.AddRange(Slack.Select(s => new AttentionItem(
            NoticeTab.Slack, s.Key, s.Key, $"{s.From} is waiting in Slack", s.Why is { Length: > 0 } why ? $"{s.Where}: {why}" : s.Where)));
        items.AddRange(_outbox.WithStatus(OutboxStatus.Held, OutboxStatus.Failed).Select(NoticeRules.FromOutbox).OfType<AttentionItem>());

        var notices = NoticeRules.Diff(_noticed, items);
        _noticed = NoticeRules.Keys(items);
        if (!_settings.Notifications)
        {
            return;
        }

        foreach (var notice in notices)
        {
            NoticeRaised?.Invoke(notice);
        }
    }

    /// <summary>Opens the notice's tab and, when it is still listed, its conversation.</summary>
    public void OpenNotice(Notice notice)
    {
        switch (notice.Tab)
        {
            case NoticeTab.Email:
                SelectedTab = EmailTab;
                if (Mail.FirstOrDefault(m => m.Key == notice.Select) is { } mail)
                {
                    SelectedMail = mail;
                }

                break;
            case NoticeTab.Slack:
                SelectedTab = SlackTab;
                if (Slack.FirstOrDefault(s => s.Key == notice.Select) is { } slack)
                {
                    SelectedSlack = slack;
                }

                break;
            default:
                SelectedTab = ScheduleTab;
                break;
        }
    }
}
