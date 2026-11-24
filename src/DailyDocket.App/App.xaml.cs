using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DailyDocket.App.Services;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Updates;
using DailyDocket.Data;
using Microsoft.Extensions.DependencyInjection;

namespace DailyDocket.App;

public partial class App : Application
{
    private readonly SingleInstance _instance;

    public App(ServiceProvider services, SingleInstance instance)
    {
        Services = services;
        _instance = instance;
    }

    /// <summary>The service container, for the few windows created outside it.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        MainWindow window;
        try
        {
            // Building the window opens the database, which is where a too-new schema shows up.
            window = Services.GetRequiredService<MainWindow>();
        }
        catch (Exception ex) when (TooNew(ex) is { } tooNew)
        {
            // Stopping here would leave a copy that can never fetch the version it needs (it never
            // reaches the update check), so it looks for that update now.
            Serilog.Log.Warning("The database is schema {Found}; this copy knows {Known}. Looking for an update.", tooNew.Found, tooNew.Known);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = UpdateForNewerDatabaseAsync();
            return;
        }
        catch (Exception ex)
        {
            // Left to the unhandled-exception box, the app would stay running with no window,
            // holding the single-instance lock so the next start only signals it.
            Serilog.Log.Fatal(ex, "Startup failed.");
            MessageBox.Show(
                $"{AppInfo.ProductName} could not start.\n\n{ex.Message}\n\nDetails are in the log.",
                AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = window;
        _instance.ListenForSecondLaunch(() => Dispatcher.InvokeAsync(() =>
        {
            // Shown, restored and activated: it may be hidden in the notification area.
            window.Reveal();

            // Answered from the UI thread, so a copy whose UI is stuck does not seem alive.
            _instance.Acknowledge();
        }));

        window.Show();
    }

    private static DatabaseTooNewException? TooNew(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is DatabaseTooNewException tooNew)
            {
                return tooNew;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks for the update a too-new database needs, showing the progress in a small window,
    /// then installs it or says how to get it. The update service needs no database, so it can run.
    /// </summary>
    private async Task UpdateForNewerDatabaseAsync()
    {
        var updates = Services.GetRequiredService<UpdateService>();
        var text = new TextBlock { Text = UpdatePolicy.TooNewProgress(updates.Status), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) };
        var progress = new Window
        {
            Title = AppInfo.ProductName,
            Content = text,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        updates.StatusChanged += status => Dispatcher.InvokeAsync(() => text.Text = UpdatePolicy.TooNewProgress(status));
        progress.Show();

        var outcome = await updates.CheckAsync();
        progress.Close();

        var ready = outcome.Stage == UpdateStage.ReadyToApply;
        MessageBox.Show(UpdatePolicy.TooNewOutcome(outcome, $"{AppInfo.RepositoryUrl}/releases/latest"), AppInfo.ProductName,
            MessageBoxButton.OK, ready ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (!ready || !updates.ApplyAndRestart())
        {
            Shutdown(1);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "Unhandled exception.");
        MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}\n\nNothing was sent. Details are in the log.",
            AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
