using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DailyDocket.App.Services;
using DailyDocket.Core.Outbox;
using DailyDocket.Core.Updates;
using DailyDocket.Data;

namespace DailyDocket.App.ViewModels;

/// <summary>
/// The bar above the status line that says an update is downloaded, with Restart. It has its own
/// view model, rather than properties on <see cref="MainViewModel"/>, to keep the main window's
/// logic about mail, Slack and the schedule.
/// </summary>
public sealed partial class UpdateBarViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly OutboxStore _outbox;

    public UpdateBarViewModel(UpdateService updates, OutboxStore outbox)
    {
        _updates = updates;
        _outbox = outbox;
        _updates.StatusChanged += status => OnUi(() => Show(status));
        Show(_updates.Status);
    }

    /// <summary>Called just before the restart, to save what is on screen (the reply boxes).</summary>
    /// <remarks>
    /// Velopack ends the process to install, so the window's closing handler never runs.
    /// </remarks>
    public Action? BeforeRestart { get; set; }

    [ObservableProperty]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    public partial string? Text { get; set; }

    /// <summary>Starts the checks: one now, then every four hours.</summary>
    public void Start() => _updates.Start();

    [RelayCommand]
    private void Restart()
    {
        // A reply mid-send would come back after the restart as uncertain (it may or may not have
        // gone), so the restart waits for it. Queued replies are safe: they are in the database and
        // the countdown resumes after the restart.
        if (_outbox.WithStatus(OutboxStatus.Sending).Count > 0)
        {
            Text = "A reply is being sent. Press Restart again once it has gone.";
            return;
        }

        BeforeRestart?.Invoke();
        _updates.ApplyAndRestart();
    }

    private void Show(UpdateStatus status)
    {
        IsReady = status.Stage == UpdateStage.ReadyToApply;
        Text = UpdatePolicy.TextFor(status);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}
