using DailyDocket.Core.Infrastructure;
using DailyDocket.Core.Updates;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace DailyDocket.App.Services;

/// <summary>
/// Checks GitHub Releases for a newer DailyDocket at startup and every four hours, downloads it in
/// the background, and says when it is ready. Adapted from LabOps' UpdateService.
/// </summary>
/// <remarks>
/// It never restarts the app on its own (Mike may be partway through a reply) and never throws out
/// of a check. Offline is normal for this app, so a failed check is logged and nothing is shown.
/// Unlike LabOps it passes no GitHub token: the repository is public, and DailyDocket does not use
/// gh. The rules that need neither Velopack nor WPF are in <see cref="UpdatePolicy"/>.
/// </remarks>
public sealed class UpdateService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ILogger<UpdateService> _log;
    private readonly OneAtATime<UpdateStatus> _check = new();
    private readonly CancellationTokenSource _stop = new();
    private UpdateManager? _manager;
    private UpdateInfo? _staged;

    // The last restart to update did not install it (see UpdatePolicy.InstallFailed).
    private bool _installFailed;

    public UpdateService(AppSettings settings, ILogger<UpdateService> log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>Set at startup when the installer started the app again after a restart to update.</summary>
    public static bool RestartedByInstaller { get; set; }

    /// <summary>Raised on whatever thread the check runs on; the window marshals it.</summary>
    public event Action<UpdateStatus>? StatusChanged;

    public UpdateStatus Status { get; private set; } = new(UpdateStage.Idle);

    /// <summary>When the last check started, or null before the first.</summary>
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>
    /// Checks now, then every <see cref="UpdatePolicy.CheckInterval"/> until the app closes. A
    /// timer of its own rather than riding on the sync timer (as LabOps does), so the update check
    /// does not depend on docket.yaml being filled in.
    /// </summary>
    public void Start() => _ = RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Checked every few minutes against the four-hour interval, rather than sleeping four
            // hours, so a laptop that slept through the interval checks soon after it wakes.
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            do
            {
                if (UpdatePolicy.IsCheckDue(Status, LastChecked, DateTimeOffset.Now, UpdatePolicy.CheckInterval))
                {
                    await CheckAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
    }

    /// <summary>
    /// Looks for a new release and downloads it. Asked again while a check runs, it hands back that
    /// check: a second one would download the same release again and wait on Velopack's lock.
    /// </summary>
    public Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default) =>
        _check.Run(() => CheckOnceAsync(cancellationToken));

    private async Task<UpdateStatus> CheckOnceAsync(CancellationToken cancellationToken)
    {
        if (Status.Stage == UpdateStage.ReadyToApply)
        {
            return Status;
        }

        LastChecked = DateTimeOffset.Now;
        var channel = AppInfo.UpdateChannel(_settings.BetaUpdates);
        try
        {
            // prerelease: true so the channel, not GitHub's prerelease flag, decides who gets a
            // build (a beta tag is a GitHub prerelease, and the beta channels must still see it).
            var source = new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: true);
            _manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = channel });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The update manager could not be set up; updates are off until the next start.");
            return Publish(new UpdateStatus(UpdateStage.Failed, Error: ex.Message));
        }

        if (!_manager.IsInstalled)
        {
            _log.LogInformation("Not an installed copy (a development build); updates are not checked.");
            return Publish(new UpdateStatus(UpdateStage.NotInstalled));
        }

        if (RestartedByInstaller)
        {
            RestartedByInstaller = false;
            _installFailed = UpdatePolicy.InstallFailed(true, _manager.UpdatePendingRestart is not null);
            if (_installFailed)
            {
                _log.LogWarning("The restart to update did not install {Version}; see the Velopack log beside the app.",
                    _manager.UpdatePendingRestart?.Version);
            }
        }

        Publish(new UpdateStatus(UpdateStage.Checking));
        UpdateInfo? update;
        try
        {
            update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Usually just offline. Logged without the stack at the normal level; the next check tries again.
            _log.LogInformation("The update check on channel {Channel} failed: {Message}", channel, ex.Message);
            return Publish(new UpdateStatus(UpdateStage.Failed, Error: ex.Message));
        }

        if (update is null)
        {
            _log.LogInformation("No update on channel {Channel}.", channel);
            return Publish(new UpdateStatus(UpdateStage.UpToDate));
        }

        var version = update.TargetFullRelease.Version.ToString();
        _log.LogInformation("Update {Version} available on channel {Channel}; downloading.", version, channel);
        try
        {
            await _manager.DownloadUpdatesAsync(update,
                percent => Publish(new UpdateStatus(UpdateStage.Downloading, version, percent)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Downloading update {Version} failed.", version);
            return Publish(new UpdateStatus(UpdateStage.Failed, version, Error: ex.Message));
        }

        _staged = update;
        return Publish(new UpdateStatus(UpdateStage.ReadyToApply, version, 100,
            Error: _installFailed ? UpdatePolicy.InstallFailedMessage(version) : null));
    }

    /// <summary>Applies the staged update and restarts. Only ever called from Mike's Restart button.</summary>
    public bool ApplyAndRestart()
    {
        if (_manager is null || _staged is null)
        {
            return false;
        }

        _log.LogInformation("Applying update {Version} and restarting.", _staged.TargetFullRelease.Version);
        _manager.ApplyUpdatesAndRestart(_staged.TargetFullRelease);
        return true;
    }

    // Cancelled but not disposed: a check still finishing holds its token, and registering on a
    // disposed source's token throws.
    public void Dispose() => _stop.Cancel();

    private UpdateStatus Publish(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
        return status;
    }
}
