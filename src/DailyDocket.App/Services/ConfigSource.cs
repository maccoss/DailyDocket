using DailyDocket.Core.Configuration;

namespace DailyDocket.App.Services;

/// <summary>
/// The owner's docket.yaml: config/docket.yaml in the data repository once it is cloned, otherwise
/// the copy in the data folder. Reread when it changes, so an edit takes effect at the next sync.
/// </summary>
public sealed class ConfigSource
{
    private readonly object _lock = new();
    private readonly string _repositoryPath;
    private readonly string _localPath;
    private DocketConfig _config = new();
    private string _loadedFrom = "";
    private DateTime _loadedStamp = DateTime.MinValue;

    public ConfigSource(string repositoryPath, string localPath)
    {
        _repositoryPath = repositoryPath;
        _localPath = localPath;
    }

    /// <summary>The file in use: the repository's when it exists.</summary>
    public string Path => File.Exists(_repositoryPath) ? _repositoryPath : _localPath;

    public bool Exists => File.Exists(Path);

    /// <summary>The problem with the file, for the window: null when it is fine.</summary>
    public string? Problem { get; private set; }

    public TimeZoneInfo Zone
    {
        get
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(Get().Schedule.TimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.Local;
            }
        }
    }

    /// <summary>Whether the file names the owner, without which no rule can tell his messages apart.</summary>
    public bool IsComplete => Get() is { } c && c.Owner.Emails.Count > 0 && c.Owner.SlackUserId.Length > 0;

    /// <summary>
    /// Copies the data folder's docket.yaml into the repository the first time it is cloned, so
    /// every machine shares one configuration from then on. True when it copied.
    /// </summary>
    public bool AdoptIntoRepository()
    {
        if (File.Exists(_repositoryPath) || !File.Exists(_localPath) || !Directory.Exists(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(_repositoryPath))))
        {
            return false;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_repositoryPath)!);
        File.Copy(_localPath, _repositoryPath);
        return true;
    }

    public DocketConfig Get()
    {
        lock (_lock)
        {
            var path = Path;
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (stamp != _loadedStamp || path != _loadedFrom)
            {
                try
                {
                    _config = DocketConfig.Load(path);
                    Problem = null;
                }
                catch (InvalidDataException e)
                {
                    // Keep the last good configuration and say what is wrong.
                    Problem = e.Message;
                }

                _loadedStamp = stamp;
                _loadedFrom = path;
            }

            return _config;
        }
    }
}
