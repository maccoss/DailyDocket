using System.Reflection;
using System.Runtime.InteropServices;

namespace DailyDocket.Core.Infrastructure;

/// <summary>
/// Identity of the running build. The version is read from the assembly, which gets it from
/// Directory.Build.props, so the About text and the update check can never disagree.
/// </summary>
public static class AppInfo
{
    /// <summary>Product name shown in the UI and used for the app data folder.</summary>
    public const string ProductName = "DailyDocket";

    /// <summary>Folder name under %LOCALAPPDATA%. No space, so paths stay easy to type.</summary>
    public const string FolderName = "DailyDocket";

    /// <summary>Velopack package identifier. Must stay stable across releases.</summary>
    public const string PackageId = "MacCoss.DailyDocket";

    /// <summary>Repository the update feed is served from.</summary>
    public const string RepositoryUrl = "https://github.com/maccoss/DailyDocket";

    /// <summary>Informational version, e.g. <c>26.1.0</c>, without build metadata.</summary>
    public static string InformationalVersion { get; } = ResolveInformationalVersion();

    /// <summary>Parsed <see cref="InformationalVersion"/>.</summary>
    public static Version Version { get; } = ParseVersion(InformationalVersion);

    /// <summary>Runtime identifier of this build, <c>win-x64</c> or <c>win-arm64</c>.</summary>
    /// <remarks>
    /// The update channel is per runtime (one Velopack channel each), so an ARM64 install never
    /// takes an x64 package and falls back to emulation.
    /// </remarks>
    public static string RuntimeIdentifier { get; } =
        $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    /// <summary>The Velopack channel this copy reads: its own runtime's, or that runtime's beta.</summary>
    public static string UpdateChannel(bool beta) => UpdateChannelFor(RuntimeIdentifier, beta);

    /// <summary>
    /// The channel for <paramref name="runtimeIdentifier"/>: <c>win-x64</c>, <c>win-arm64</c>, or
    /// either with <c>-beta</c>. release.yml packs each runtime under the same names, so the two
    /// must change together.
    /// </summary>
    /// <remarks>
    /// LabOps uses <c>stable</c> and <c>beta</c>, but it ships one runtime. With two in one release,
    /// a shared channel would let Velopack hand an ARM64 computer whichever package it read first.
    /// </remarks>
    public static string UpdateChannelFor(string runtimeIdentifier, bool beta) =>
        beta ? $"{runtimeIdentifier}-beta" : runtimeIdentifier;

    /// <summary>The machine's name as the repository records it (updatedOn, the job lease).</summary>
    public static string MachineName { get; } = Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// Parses a CalVer string such as <c>26.1.0</c> or <c>26.2.0-beta.1</c>, ignoring any
    /// prerelease suffix. Anything unparseable becomes 0.0.0.
    /// </summary>
    public static Version ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Version(0, 0, 0);
        }

        text = text.Trim();
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash];
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return new Version(0, 0, 0);
        }

        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
    }

    private static string ResolveInformationalVersion()
    {
        var raw = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
