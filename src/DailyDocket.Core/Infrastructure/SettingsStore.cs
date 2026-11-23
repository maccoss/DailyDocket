using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDocket.Core.Infrastructure;

/// <summary>Per-user settings. Nothing here is a secret; sign-ins belong to git, gh and Claude.</summary>
public sealed class AppSettings
{
    /// <summary>Folder holding the clone of the private to-do repository; null uses the default under the data folder.</summary>
    public string? TodoRepositoryPath { get; set; }

    /// <summary>The private data repository (to-dos, Handled marks, docket.yaml) to clone; empty means none yet.</summary>
    public string DataRepositoryUrl { get; set; } = "https://github.com/maccoss/DailyDocket-Data.git";

    /// <summary>Whether email replies end with the Gmail signature set up on the web, as replies written in Gmail do.</summary>
    public bool IncludeSignature { get; set; } = true;

    /// <summary>Mark a Gmail thread read when Mike replies to it or marks it handled in the app.</summary>
    public bool MarkReadInGmail { get; set; } = true;

    /// <summary>
    /// Claude Code session per key, so "continue" picks up a drafting conversation (keyed by
    /// <c>email:</c> plus the thread id, or <c>slack:</c> plus the conversation key).
    /// </summary>
    public Dictionary<string, string> ClaudeSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Model for the pull and send jobs (cheap, mechanical work).</summary>
    public string PullModel { get; set; } = "haiku";

    /// <summary>Model for triage, the nightly sweep, meeting prep and drafting.</summary>
    public string JudgmentModel { get; set; } = "sonnet";

    /// <summary>Minutes between pulls while online.</summary>
    public int PullIntervalMinutes { get; set; } = 15;

    /// <summary>Seconds a queued item waits before the Send job may take it (the undo window).</summary>
    public int UndoWindowSeconds { get; set; } = 60;

    /// <summary>The Foundry Local model alias for offline drafting, chosen at setup from <c>foundry model list</c>.</summary>
    public string LocalModelAlias { get; set; } = "phi-4-mini";

    /// <summary>Use the beta update channel (for trying a release before everyone gets it).</summary>
    public bool BetaUpdates { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, atomically.</summary>
/// <remarks>
/// Written to a temporary file and moved into place, so a crash mid-write leaves the previous
/// settings intact rather than a truncated file that would send the user back through setup.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger<SettingsStore> _log;
    private readonly Lock _gate = new();

    public SettingsStore(string path, ILogger<SettingsStore>? log = null)
    {
        _path = path;
        _log = log ?? NullLogger<SettingsStore>.Instance;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
            if (settings is null)
            {
                return new AppSettings();
            }

            // A deserialized dictionary loses its comparer; the keys are case-insensitive.
            settings.ClaudeSessions = new Dictionary<string, string>(
                settings.ClaudeSessions ?? [], StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Settings at {Path} could not be read; starting from defaults.", _path);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
