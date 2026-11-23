using System.Text.RegularExpressions;
using DailyDocket.Core.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DailyDocket.App.Services;

/// <summary>Builds the Serilog pipeline: rolling files under the data folder's logs. From LabOps.</summary>
/// <remarks>Message bodies are never logged at the normal level: runs log ids, counts and outcomes only.</remarks>
public static class LoggingSetup
{
    public static Serilog.ILogger Create(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.With<SecretRedactingEnricher>()
            .WriteTo.File(
                path: paths.LogFileTemplate,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 16L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}

/// <summary>
/// Scrubs anything that looks like a credential out of log properties. The code never logs a token
/// on purpose (the docket tool server holds one), but a secret in a log file cannot be taken back.
/// </summary>
public sealed partial class SecretRedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var name in logEvent.Properties.Keys.ToArray())
        {
            if (SensitiveName().IsMatch(name))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, "[redacted]"));
            }
        }
    }

    [GeneratedRegex("password|secret|token|authorization|credential|key", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();
}
