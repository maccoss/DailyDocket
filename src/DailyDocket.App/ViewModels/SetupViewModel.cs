using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DailyDocket.App.Services;
using DailyDocket.Core.Claude;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Processes;
using DailyDocket.Core.Setup;
using DailyDocket.Core.Sync;
using Microsoft.Extensions.Logging;

namespace DailyDocket.App.ViewModels;

/// <summary>
/// The Setup checklist (LabOps' Setup window, cut to what DailyDocket needs): each thing the
/// computer needs, with a button that does it.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly SetupChecks _checks;
    private readonly ClaudeLogin _claudeLogin;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DataSync _data;
    private readonly Func<Task<string?>> _connectData;
    private readonly ILogger _log;
    private CancellationTokenSource? _waiting;

    public SetupViewModel(SetupChecks checks, ClaudeLogin claudeLogin, AppSettings settings, SettingsStore settingsStore,
        DataSync data, Func<Task<string?>> connectData, ILogger log)
    {
        _checks = checks;
        _claudeLogin = claudeLogin;
        _settings = settings;
        _settingsStore = settingsStore;
        _data = data;
        _connectData = connectData;
        _log = log;
        RepositoryText = settings.DataRepositoryUrl;
        Status = "Checking...";
    }

    public ObservableCollection<SetupItem> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FixCommand), nameof(AlternateCommand), nameof(ChangeRepositoryCommand))]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    public partial bool IsWorking { get; set; }

    /// <summary>Done waits for the step under way; the window's close box still closes it.</summary>
    public bool CanClose => !IsWorking;

    /// <summary>Waiting for Mike to finish signing in to Claude in the browser.</summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; set; }

    [ObservableProperty]
    public partial bool Checked { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    /// <summary>The data repository's address, as typed: a GitHub address or owner/name.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeRepositoryCommand))]
    public partial string RepositoryText { get; set; }

    /// <summary>
    /// Open Setup by itself at startup while something is missing. A computer kept without one
    /// piece on purpose would otherwise get the window at every start (code review, November 2026).
    /// </summary>
    public bool OpenAtStartup
    {
        get => _settings.OpenSetupAtStartup;
        set
        {
            if (_settings.OpenSetupAtStartup == value)
            {
                return;
            }

            _settings.OpenSetupAtStartup = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public async Task RefreshAsync()
    {
        IsWorking = true;
        try
        {
            var items = await _checks.CheckAsync(_settings.DataRepositoryUrl, _data.RepositoryPath).ConfigureAwait(true);
            Rows.Clear();
            foreach (var item in items)
            {
                Rows.Add(item);
            }

            Status = SetupChecks.Summary(items);
            Checked = true;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void StopWaiting() => _waiting?.Cancel();

    [RelayCommand(CanExecute = nameof(CanFix))]
    private Task FixAsync(SetupItem item) => DoAsync(item.Fix!.Value);

    private bool CanFix(SetupItem? item) => !IsWorking && item is { CanFix: true };

    [RelayCommand(CanExecute = nameof(CanAlternate))]
    private Task AlternateAsync(SetupItem item) => DoAsync(item.Alternate!.Value);

    private bool CanAlternate(SetupItem? item) => !IsWorking && item is { HasAlternate: true };

    private async Task DoAsync(SetupFix fix)
    {
        IsWorking = true;
        try
        {
            switch (fix)
            {
                case SetupFix.SignInToClaude:
                    await SignInToClaudeAsync().ConfigureAwait(true);
                    break;
                case SetupFix.ConnectGit:
                    Status = "Connecting git to your GitHub sign-in...";
                    await _checks.ConnectGitAsync().ConfigureAwait(true);
                    break;
                case SetupFix.SetGitName:
                    Status = "Setting your name on saved changes...";
                    await _checks.SetGitNameAsync(_data.RepositoryPath).ConfigureAwait(true);
                    break;
                case SetupFix.DownloadData:
                    await DownloadAsync().ConfigureAwait(true);
                    break;
                default:
                    if (_checks.ConsoleFix(fix) is { } command)
                    {
                        Status = command.Explanation;
                        await SetupConsole.RunAsync(command).ConfigureAwait(true);
                    }

                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ToolMissingException)
        {
            _log.LogWarning(e, "Setup step {Fix} failed", fix);
            Warn(e.Message);
        }
        finally
        {
            IsWorking = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Signs in to Claude in the browser with no console window; if that does not finish, offers the
    /// console, where Claude Code can ask for a code to paste.
    /// </summary>
    private async Task SignInToClaudeAsync()
    {
        Status = "Finish in your browser: sign in to Claude and choose Authorize. If the page says the window is too small, make it larger.";
        using var waiting = new CancellationTokenSource();
        _waiting = waiting;
        IsWaiting = true;
        ClaudeLoginResult result;
        try
        {
            result = await _claudeLogin.SignInAsync(null, waiting.Token).ConfigureAwait(true);
        }
        finally
        {
            IsWaiting = false;
            _waiting = null;
        }

        if (result.Succeeded || result.Cancelled)
        {
            return;
        }

        _log.LogWarning("claude auth login did not finish: {Output}", result.Output);
        if (MessageBox.Show(
                "The sign-in did not finish. Sign in with a console window instead? It shows what Claude asks for, and you close it when it says you are signed in.",
                AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            && _checks.ConsoleFix(SetupFix.SignInToClaude) is { } command)
        {
            Status = command.Explanation;
            await SetupConsole.RunAsync(command).ConfigureAwait(true);
        }
    }

    private async Task DownloadAsync()
    {
        Status = $"Downloading {SetupChecks.RepositoryName(_settings.DataRepositoryUrl)}...";
        if (await _connectData().ConfigureAwait(true) is { } problem)
        {
            Warn(problem);
        }
    }

    /// <summary>
    /// Points DailyDocket at another data repository (someone setting up their own docket). The
    /// current clone, if any, is moved aside, never deleted, and the new one downloaded.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeRepository))]
    private async Task ChangeRepositoryAsync()
    {
        if (SetupChecks.NormalizeRepositoryUrl(RepositoryText) is not { } url)
        {
            Warn("Enter the repository as owner/name (for example someone/DailyDocket-Data) or its full GitHub address.");
            return;
        }

        IsWorking = true;
        try
        {
            var origin = await _data.OriginAsync().ConfigureAwait(true);
            if (SetupChecks.NeedsSetAside(_data.IsCloned, origin, url))
            {
                var current = origin is null ? "the current data repository" : SetupChecks.RepositoryName(origin);
                if (MessageBox.Show(
                        $"DailyDocket will use {SetupChecks.RepositoryName(url)} instead of {current}. The copy of "
                        + $"{current} on this computer is moved aside, not deleted. Continue?",
                        AppInfo.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }

                if (await _data.SetAsideAsync().ConfigureAwait(true) is { } problem)
                {
                    Warn(problem);
                    return;
                }
            }

            _settings.DataRepositoryUrl = url;
            _settingsStore.Save(_settings);
            RepositoryText = url;
            _log.LogInformation("Data repository changed to {Repository}", SetupChecks.RepositoryName(url));
        }
        finally
        {
            IsWorking = false;
        }

        await RefreshAsync().ConfigureAwait(true);
        if (Rows.FirstOrDefault(r => r.Step == SetupStep.DataRepository) is { Fix: SetupFix.DownloadData } row)
        {
            await DoAsync(row.Fix!.Value).ConfigureAwait(true);
        }
    }

    private bool CanChangeRepository() =>
        !IsWorking && !string.Equals(RepositoryText?.Trim(), _settings.DataRepositoryUrl, StringComparison.OrdinalIgnoreCase);

    private static void Warn(string message) =>
        MessageBox.Show(message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
}
