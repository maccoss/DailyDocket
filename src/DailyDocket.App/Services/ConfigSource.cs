using DailyDocket.Core.Configuration;

namespace DailyDocket.App.Services;

/// <summary>The owner's docket.yaml, reread when it changes so an edit takes effect at the next sync.</summary>
public sealed class ConfigSource
{
    private readonly object _lock = new();
    private DocketConfig _config = new();
    private DateTime _loadedStamp = DateTime.MinValue;

    public ConfigSource(string path) => Path = path;

    public string Path { get; }

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

    public DocketConfig Get()
    {
        lock (_lock)
        {
            var stamp = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
            if (stamp != _loadedStamp)
            {
                try
                {
                    _config = DocketConfig.Load(Path);
                    Problem = null;
                }
                catch (InvalidDataException e)
                {
                    // Keep the last good configuration and say what is wrong.
                    Problem = e.Message;
                }

                _loadedStamp = stamp;
            }

            return _config;
        }
    }
}
