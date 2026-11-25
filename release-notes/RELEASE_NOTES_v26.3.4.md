# DailyDocket v26.3.4

DailyDocket can no longer get stuck on a version older than its database, and notifications
in the corner of the screen are now only for replies that did not send, unless you choose otherwise.

## New Features

- **Notifications are for replies that were held or did not send, by default.** The Email and
  Slack tabs still list everything waiting on you, and most of it is old by the time it is listed.
  To choose, right-click DailyDocket's icon in the notification area and pick under
  Notifications: only replies that did not send, everything new that needs a reply (as before),
  or none. If you had turned notifications off, they stay off
  (`src/DailyDocket.Core/Infrastructure/SettingsStore.cs`, `src/DailyDocket.App/Services/TrayIcon.cs`).

## Bug Fixes

- **A copy older than its database could never update itself.** It showed "written by a newer
  version of DailyDocket" and stopped before its update check ran, so it never found the version
  it needed. It now looks for that update in a small window that shows the download, then installs
  it when you press OK. Offline, or with no update found, it gives the reason and the download link
  for the latest release; the database and the outbox are kept either way
  (`src/DailyDocket.App/App.xaml.cs`, `src/DailyDocket.Data/DatabaseTooNewException.cs`).

- **A failed start left DailyDocket running with no window**, so starting it again did nothing.
  It now says it could not start and exits (`src/DailyDocket.App/App.xaml.cs`).
