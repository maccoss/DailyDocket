using System.ComponentModel;
using System.Windows;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Setup;
using Microsoft.Extensions.Logging;

namespace DailyDocket.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly SetupChecks _setup;
    private readonly ILogger<MainWindow> _log;
    private bool _setupProblemShown;
    private DateTimeOffset _lastSetupCheck;

    public MainWindow(MainViewModel viewModel, UpdateBarViewModel updates, SetupChecks setup, SettingsStore settingsStore, AppSettings settings,
        ILogger<MainWindow> log)
    {
        InitializeComponent();
        _viewModel = viewModel;
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

        Loaded += (_, _) =>
        {
            _viewModel.Start();
            updates.Start();
            _ = CheckSetupAsync();
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

    private async Task CheckSetupAsync()
    {
        _lastSetupCheck = DateTimeOffset.Now;
        try
        {
            var problems = await _setup.CheckAsync();
            _setupProblemShown = problems.Count > 0;
            _viewModel.ShowComputerProblem(SetupChecks.Describe(problems));
        }
        catch (Exception e)
        {
            // A check that cannot run must not stop the window; a sync that fails says why anyway.
            _log.LogWarning(e, "The setup checks could not run");
        }
    }

    private void SaveReplies()
    {
        _viewModel.MailCompose.SaveCurrent();
        _viewModel.SlackCompose.SaveCurrent();
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
        base.OnClosing(e);
    }
}
