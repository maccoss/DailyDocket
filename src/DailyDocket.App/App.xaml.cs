using System.Windows;
using System.Windows.Threading;
using DailyDocket.Core.Infrastructure;
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

        var window = Services.GetRequiredService<MainWindow>();
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

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "Unhandled exception.");
        MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}\n\nNothing was sent. Details are in the log.",
            AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
