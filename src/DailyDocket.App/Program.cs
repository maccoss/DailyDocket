using System.Windows;
using DailyDocket.App.Services;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Processes;
using DailyDocket.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace DailyDocket.App;

/// <summary>Explicit entry point, as in LabOps (see the csproj).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // DAILYDOCKET_DATA points the app at another data folder, for development.
        var paths = new AppPaths(Environment.GetEnvironmentVariable("DAILYDOCKET_DATA"));
        paths.EnsureCreated();

        // Programs the app opens (the browser, Claude's sign-in) inherit this folder rather than
        // the program folder, which an update must be able to replace.
        Environment.CurrentDirectory = paths.Root;

        using var instance = SingleInstance.Acquire(AppInfo.FolderName, paths.InstanceLockFile);
        if (!instance.IsFirst)
        {
            if (!instance.SignalExisting(TimeSpan.FromSeconds(5)))
            {
                MessageBox.Show(
                    $"{AppInfo.ProductName} is already running but is not responding. End DailyDocket.exe in Task Manager, then start it again.",
                    AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return 0;
        }

        Serilog.Log.Logger = LoggingSetup.Create(paths);
        try
        {
            Serilog.Log.Information("Starting {Product} {Version} ({Rid}) on {Machine}; data directory {Root}",
                AppInfo.ProductName, AppInfo.InformationalVersion, AppInfo.RuntimeIdentifier, AppInfo.MachineName, paths.Root);

            using var services = BuildServiceProvider(paths);
            var app = new App(services, instance);
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Startup failed.");
            MessageBox.Show(
                $"{AppInfo.ProductName} could not start.\n\n{ex.Message}\n\nThe log is in {paths.LogDirectory}",
                AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    /// <summary>The file holding the owner's details until the data repository is cloned (phase 3).</summary>
    internal static string ConfigFile(AppPaths paths) => Path.Combine(paths.Root, "docket.yaml");

    /// <summary>
    /// Wires everything the window needs. Internal so a test can build it: a missing registration
    /// is a window that will not open, and nothing else would catch it.
    /// </summary>
    internal static ServiceProvider BuildServiceProvider(AppPaths paths, string? databaseKey = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new SerilogLoggerProvider(dispose: false));
        });

        services.AddSingleton(provider => new SettingsStore(paths.SettingsFile, provider.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton(provider => provider.GetRequiredService<SettingsStore>().Load());
        services.AddSingleton(new ConfigSource(ConfigFile(paths)));
        services.AddSingleton<Func<DocketConfig>>(provider => provider.GetRequiredService<ConfigSource>().Get);
        services.AddSingleton<Func<AppSettings>>(provider => provider.GetRequiredService<AppSettings>);

        services.AddSingleton(_ => DocketDatabase.Open(paths.DatabaseFile, databaseKey ?? DatabaseKey.GetOrCreate()));
        services.AddSingleton<Store>();
        services.AddSingleton(provider =>
        {
            var config = provider.GetRequiredService<Func<DocketConfig>>();
            return new CaptureIngestor(provider.GetRequiredService<DocketDatabase>(), config, provider.GetRequiredService<ConfigSource>().Zone);
        });
        services.AddSingleton<JobStore>();
        services.AddSingleton<IJobStore>(provider => provider.GetRequiredService<JobStore>());

        services.AddSingleton<ToolLocator>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton(provider => new JobRunner(
            provider.GetRequiredService<ToolLocator>(),
            provider.GetRequiredService<ProcessRunner>(),
            paths,
            provider.GetRequiredService<IJobStore>(),
            provider.GetRequiredService<Func<DocketConfig>>(),
            HookLocator.Find(),
            provider.GetRequiredService<ILogger<JobRunner>>()));
        services.AddSingleton<SyncScheduler>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }
}
