using System.ComponentModel;
using System.Windows;
using DailyDocket.App.Services;
using DailyDocket.App.ViewModels;
using DailyDocket.App.Views;
using DailyDocket.Core.Claude;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Todos;
using DailyDocket.Core.Setup;
using Microsoft.Extensions.Logging;

namespace DailyDocket.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly SetupChecks _setup;
    private readonly ClaudeLogin _claudeLogin;
    private readonly TodoBook _book;
    private readonly ILogger<MainWindow> _log;
    private bool _setupProblemShown;
    private DateTimeOffset _lastSetupCheck;
    private readonly TrayIcon _tray;

    public MainWindow(MainViewModel viewModel, UpdateBarViewModel updates, SetupChecks setup, ClaudeLogin claudeLogin, TodoBook book,
        SettingsStore settingsStore, AppSettings settings, ILogger<MainWindow> log)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _claudeLogin = claudeLogin;
        _book = book;
        _settingsStore = settingsStore;
        _settings = settings;
        _setup = setup;
        _log = log;
        DataContext = viewModel;
        UpdateBar.DataContext = updates;
        updates.BeforeRestart = SaveReplies;

        if (settings.WindowWidth is > 400 and var width && settings.WindowHeight is > 300 and var height)
        {
            Width = width;
            Height = height;
        }

        _tray = new TrayIcon(this, viewModel, settings, settingsStore);
        Loaded += (_, _) =>
        {
            _viewModel.Start();
            updates.Start();
            _ = CheckSetupAsync(openIfNeeded: settings.OpenSetupAtStartup);
        };

        // Mike fixes a setup problem in a terminal and comes back: look again, so the banner clears
        // without a restart. Only while one is showing, and not more than every few seconds.
        Activated += (_, _) =>
        {
            if (_setupProblemShown && DateTimeOffset.Now - _lastSetupCheck > TimeSpan.FromSeconds(5))
            {
                _ = CheckSetupAsync();
            }
        };
    }

    /// <summary>
    /// Looks at what the computer is missing and shows it in the banner. The first time, at
    /// startup, opens Setup when something is missing for certain, as LabOps does on a new
    /// computer; a step that could only not be checked (offline) does not open it.
    /// </summary>
    private async Task CheckSetupAsync(bool openIfNeeded = false)
    {
        _lastSetupCheck = DateTimeOffset.Now;
        try
        {
            var items = await _setup.CheckAsync(_settings.DataRepositoryUrl, _book.Sync.RepositoryPath);
            var problem = SetupChecks.Describe(items);
            _setupProblemShown = problem is not null;
            _viewModel.ShowComputerProblem(problem);
            if (openIfNeeded && problem is not null)
            {
                OpenSetup();
            }
        }
        catch (Exception e)
        {
            // A check that cannot run must not stop the window; a sync that fails says why anyway.
            _log.LogWarning(e, "The setup checks could not run");
        }
    }

    private void OnSetup(object sender, RoutedEventArgs e) => OpenSetup();

    private bool _setupOpen;

    private void OpenSetup()
    {
        if (_setupOpen)
        {
            return;
        }

        _setupOpen = true;
        try
        {
            SetupWindow.Show(this, new SetupViewModel(_setup, _claudeLogin, _settings, _settingsStore, _book.Sync,
                _viewModel.ConnectDataAsync, _log));
        }
        finally
        {
            _setupOpen = false;
        }

        _ = CheckSetupAsync();
    }

    private void SaveReplies()
    {
        _viewModel.MailCompose.SaveCurrent();
        _viewModel.SlackCompose.SaveCurrent();
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
        SaveReplies();
        _viewModel.Dispose();
        _tray.Dispose();
        base.OnClosing(e);
    }
}
