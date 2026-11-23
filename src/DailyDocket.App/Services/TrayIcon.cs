using System.Drawing;
using System.Windows;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Notifications;
using Forms = System.Windows.Forms;

namespace DailyDocket.App.Services;

/// <summary>
/// DailyDocket in the notification area: where the window goes when minimized (it keeps syncing),
/// and where notifications come from.
/// </summary>
/// <remarks>
/// Windows Forms' NotifyIcon rather than a toast library: on Windows 10 and 11 its balloon tips are
/// shown as ordinary notifications, clicking one raises an event here, and it needs no package,
/// no Start-menu shortcut and no app identity, which unpackaged toasts do.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly Window _window;
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _notifications;
    private Notice? _shown;

    public TrayIcon(Window window, MainViewModel viewModel, AppSettings settings, SettingsStore settingsStore)
    {
        _window = window;
        _viewModel = viewModel;
        _settings = settings;
        _settingsStore = settingsStore;

        _notifications = new Forms.ToolStripMenuItem("Notifications") { Checked = settings.Notifications, CheckOnClick = true };
        _notifications.CheckedChanged += (_, _) =>
        {
            _settings.Notifications = _notifications.Checked;
            _settingsStore.Save(_settings);
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open DailyDocket", null, (_, _) => ShowWindow());
        menu.Items.Add(_notifications);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _window.Close());

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon(),
            Text = AppInfo.ProductName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowWindow();
            }
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            ShowWindow();
            if (_shown is { } notice)
            {
                _viewModel.OpenNotice(notice);
            }
        };

        _viewModel.NoticeRaised += Show;
    }

    /// <summary>Brings the window back from the notification area or the taskbar.</summary>
    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void Show(Notice notice)
    {
        // The last one shown is the one a click opens; Windows shows one at a time from an icon.
        _shown = notice;
        _icon.ShowBalloonTip(10_000, notice.Title, notice.Text.Length > 0 ? notice.Text : " ", Forms.ToolTipIcon.None);
    }

    /// <summary>The exe's own icon (once the app has one), or Windows' generic application icon.</summary>
    private static Icon AppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon)
            {
                return icon;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            // Fall back below.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _viewModel.NoticeRaised -= Show;

        // Hidden first: a disposed icon can otherwise linger in the notification area until hovered.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
