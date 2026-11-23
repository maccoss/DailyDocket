namespace DailyDocket.Core.Infrastructure;

/// <summary>
/// Every on-disk location the application uses, resolved in one place.
/// </summary>
/// <remarks>
/// Everything lives under <c>%LOCALAPPDATA%\DailyDocket</c>: local, not roaming, because the
/// cache, the outbox and Claude's per-run files belong to one machine. Gmail, Calendar and Slack
/// are the sources of truth across machines, and the to-do repository is a git clone.
/// <c>DAILYDOCKET_DATA</c> moves the whole folder, for trying a build without touching the real one.
/// </remarks>
public sealed class AppPaths
{
    public const string DataOverrideVariable = "DAILYDOCKET_DATA";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride
            ?? NullIfEmpty(Environment.GetEnvironmentVariable(DataOverrideVariable))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName);

        LogDirectory = Path.Combine(Root, "logs");
        SettingsFile = Path.Combine(Root, "settings.json");
        DatabaseFile = Path.Combine(Root, "docket.db");
        OutboxExportFile = Path.Combine(Root, "outbox.json");
        RunDirectory = Path.Combine(Root, "runs");
        CaptureDirectory = Path.Combine(Root, "captures");
        TodoRepositoryDirectory = Path.Combine(Root, "repo");
    }

    /// <summary>The application data root.</summary>
    public string Root { get; }

    /// <summary>Directory holding rolling log files.</summary>
    public string LogDirectory { get; }

    /// <summary>Path template passed to the rolling file sink.</summary>
    public string LogFileTemplate => Path.Combine(LogDirectory, "dailydocket-.log");

    /// <summary>User settings. Never contains credentials.</summary>
    public string SettingsFile { get; }

    /// <summary>The SQLite cache and outbox (encrypted).</summary>
    public string DatabaseFile { get; }

    /// <summary>The outbox and unsent drafts, rewritten on every change: the only irreplaceable rows.</summary>
    public string OutboxExportFile { get; }

    /// <summary>Per-run files handed to Claude Code: the MCP config, the hook settings, the run log.</summary>
    public string RunDirectory { get; }

    /// <summary>Raw hook captures waiting to be posted to the app (when it was not running).</summary>
    public string CaptureDirectory { get; }

    /// <summary>The default clone of the private to-do repository.</summary>
    public string TodoRepositoryDirectory { get; }

    /// <summary>Held open while the application runs, so a second copy can tell there is one.</summary>
    public string InstanceLockFile => Path.Combine(Root, "instance.lock");

    /// <summary>Creates the directories that must exist before anything else runs.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RunDirectory);
        Directory.CreateDirectory(CaptureDirectory);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
