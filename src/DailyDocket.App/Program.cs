using System.Windows;
using DailyDocket.App.Services;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Configuration;
using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Jobs;
using DailyDocket.Core.Outbox;
using DailyDocket.Core.Processes;
using DailyDocket.Core.Sync;
using DailyDocket.Core.Todos;
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

            var services = BuildServiceProvider(paths);
            int code;

            // One-time move from the dashboard artifact: DailyDocket.exe --import-artifact <folder>,
            // where the folder holds its database exported as todos/, handled/ and slackHandled/.
            if (args.Length == 2 && args[0] == "--import-artifact")
            {
                code = ImportArtifact(services, args[1]);
            }
            else
            {
                var app = new App(services, instance);
                app.InitializeComponent();
                code = app.Run();
            }

            // The window is gone: a problem while cleaning up is logged, never reported as a failed start.
            try
            {
                services.Dispose();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Cleanup after closing failed.");
            }

            return code;
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

    /// <summary>The file holding the owner's details until the data repository is cloned.</summary>
    internal static string ConfigFile(AppPaths paths) => Path.Combine(paths.Root, "docket.yaml");

    internal static string RepositoryPath(AppPaths paths, AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.TodoRepositoryPath) ? paths.TodoRepositoryDirectory : settings.TodoRepositoryPath;

    private static int ImportArtifact(ServiceProvider services, string folder)
    {
        var book = services.GetRequiredService<TodoBook>();
        var settings = services.GetRequiredService<AppSettings>();
        string message;
        try
        {
            var problem = book.Sync.EnsureClonedAsync(settings.DataRepositoryUrl).GetAwaiter().GetResult();
            if (problem is not null)
            {
                message = problem;
            }
            else
            {
                var result = ArtifactImport.Import(folder, book.Repository, AppInfo.MachineName, DateTimeOffset.Now);
                var adopted = services.GetRequiredService<ConfigSource>().AdoptIntoRepository();
                var sync = book.Sync.SyncAsync($"Import the dashboard artifact's to-dos ({result.Todos}) and Handled marks ({result.EmailHandled + result.SlackHandled})").GetAwaiter().GetResult();
                message = $"Imported {result.Todos} to-dos, {result.EmailHandled} email and {result.SlackHandled} Slack Handled marks ({result.Skipped} skipped)"
                    + (adopted ? ", and moved docket.yaml into the data repository" : "") + ". "
                    + (sync.Ok && !sync.Offline ? "Pushed to GitHub." : sync.Problem ?? "Saved on this computer; it will push at the next sync.");
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            message = $"The import failed: {e.Message}";
        }

        Serilog.Log.Information("Artifact import: {Message}", message);
        MessageBox.Show(message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }

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
        services.AddSingleton(provider =>
        {
            var repo = RepositoryPath(paths, provider.GetRequiredService<AppSettings>());
            return new ConfigSource(Path.Combine(repo, "config", "docket.yaml"), ConfigFile(paths));
        });
        services.AddSingleton(provider => new DataRepository(RepositoryPath(paths, provider.GetRequiredService<AppSettings>())));
        services.AddSingleton(provider => new DataSync(
            provider.GetRequiredService<ProcessRunner>(),
            provider.GetRequiredService<ToolLocator>(),
            RepositoryPath(paths, provider.GetRequiredService<AppSettings>()),
            AppInfo.MachineName,
            provider.GetRequiredService<ILogger<DataSync>>()));
        services.AddSingleton(provider => new TodoBook(
            provider.GetRequiredService<DataRepository>(),
            provider.GetRequiredService<DataSync>(),
            AppInfo.MachineName,
            provider.GetRequiredService<ILogger<TodoBook>>()));
        services.AddSingleton<Func<DocketConfig>>(provider => provider.GetRequiredService<ConfigSource>().Get);
        services.AddSingleton<Func<AppSettings>>(provider => provider.GetRequiredService<AppSettings>);

        services.AddSingleton(_ => DocketDatabase.Open(paths.DatabaseFile, databaseKey ?? DatabaseKey.GetOrCreate()));
        services.AddSingleton<Store>();
        services.AddSingleton(provider =>
        {
            var config = provider.GetRequiredService<Func<DocketConfig>>();
            return new CaptureIngestor(provider.GetRequiredService<DocketDatabase>(), config, provider.GetRequiredService<ConfigSource>().Zone);
        });
        services.AddSingleton(provider => new JobStore(
            provider.GetRequiredService<Store>(),
            provider.GetRequiredService<CaptureIngestor>(),
            provider.GetRequiredService<Func<DocketConfig>>(),
            () => provider.GetRequiredService<TodoBook>().Handled));
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
        services.AddSingleton(provider => new OutboxStore(provider.GetRequiredService<DocketDatabase>(), paths.OutboxExportFile));
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return new OutboxWorker(
                provider.GetRequiredService<OutboxStore>(),
                provider.GetRequiredService<JobRunner>(),
                () => (settings.PullModel, settings.IncludeSignature, AppInfo.MachineName),
                provider.GetRequiredService<ILogger<OutboxWorker>>());
        });

        services.AddSingleton<Drafter>();
        services.AddSingleton<TodoViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }
}
