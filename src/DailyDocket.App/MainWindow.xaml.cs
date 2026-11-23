using System.ComponentModel;
using System.Windows;
using DailyDocket.App.Services;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Infrastructure;

namespace DailyDocket.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly TrayIcon _tray;

    public MainWindow(MainViewModel viewModel, SettingsStore settingsStore, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        _settings = settings;
        DataContext = viewModel;

        if (settings.WindowWidth is > 400 and var width && settings.WindowHeight is > 300 and var height)
        {
            Width = width;
            Height = height;
        }

        _tray = new TrayIcon(this, viewModel, settings, settingsStore);
        Loaded += (_, _) => _viewModel.Start();
    }

    /// <summary>The window, brought back from the notification area: a second launch, or the tray icon.</summary>
    public void Reveal() => _tray.ShowWindow();

    /// <summary>Minimized goes to the notification area (a setting); the view model, and syncing, carry on.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settingsStore.Save(_settings);
        }

        // Whatever is in a reply box is saved before the app goes.
        _viewModel.MailCompose.SaveCurrent();
        _viewModel.SlackCompose.SaveCurrent();
        _viewModel.Dispose();
        _tray.Dispose();
        base.OnClosing(e);
    }
}
