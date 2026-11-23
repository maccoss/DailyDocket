namespace DailyDocket.Core.Updates;

/// <summary>Where the update workflow stands.</summary>
public enum UpdateStage
{
    Idle,
    NotInstalled,
    Checking,
    UpToDate,
    Downloading,
    ReadyToApply,
    Failed,
}

/// <summary>One moment of the update workflow, as the window shows it.</summary>
public sealed record UpdateStatus(UpdateStage Stage, string? AvailableVersion = null, int DownloadPercent = 0, string? Error = null);

/// <summary>
/// The rules of the update check that need neither Velopack nor WPF, so they can be tested here.
/// Adapted from LabOps' UpdateService.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>How often a running copy looks for a new release, as in LabOps and PanoramaBridge.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    /// <summary>
    /// Whether a periodic check should run now. Never while one is running or downloading, never
    /// once an update is staged (it waits for Mike's restart, and checking again would only
    /// download it a second time), and never for a copy Velopack does not manage, such as a
    /// development build.
    /// </summary>
    public static bool IsCheckDue(UpdateStatus status, DateTimeOffset? lastChecked, DateTimeOffset now, TimeSpan interval) =>
        status.Stage is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.ReadyToApply or UpdateStage.NotInstalled)
        && (lastChecked is null || now - lastChecked.Value >= interval);

    /// <summary>
    /// Started again by the installer with the downloaded update still waiting: the restart did not
    /// install it. The installer then starts the old version, so without this the app would offer
    /// the same restart again and again.
    /// </summary>
    public static bool InstallFailed(bool restartedByInstaller, bool updateStillWaiting) => restartedByInstaller && updateStillWaiting;

    /// <summary>What to tell Mike when an update did not install.</summary>
    public static string InstallFailedMessage(string version) =>
        $"Update {version} could not be installed: another program was still using DailyDocket's folder. That is usually "
        + "something opened from DailyDocket, such as a link in your browser or Claude's sign-in window. Close those "
        + "(or restart Windows), then press Restart again.";

    /// <summary>
    /// The update bar's text, or null when there is nothing to show. A failed check says nothing:
    /// offline is normal for this app, and the log has the reason.
    /// </summary>
    public static string? TextFor(UpdateStatus status) => status.Stage switch
    {
        UpdateStage.ReadyToApply when status.Error is not null => status.Error,
        UpdateStage.ReadyToApply => $"Update {status.AvailableVersion} ready: restart to install",
        _ => null,
    };

    /// <summary>The green button's words at the upper right: the version it will install.</summary>
    public static string ButtonFor(UpdateStatus status) => $"Restart to update to {status.AvailableVersion}";

    /// <summary>
    /// The line beside the button: only when something needs saying beyond the button (the last
    /// install failed); null otherwise, since the button already names the version.
    /// </summary>
    public static string? NoteFor(UpdateStatus status) =>
        status.Stage == UpdateStage.ReadyToApply ? status.Error : null;
}
