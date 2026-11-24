# DailyDocket v26.3.4

DailyDocket can no longer get stuck on a version older than its database.

## Bug Fixes

- **A copy older than its database could never update itself.** It showed "written by a newer
  version of DailyDocket" and stopped before its update check ran, so it never found the version
  it needed. It now looks for that update in a small window that shows the download, then installs
  it when you press OK. Offline, or with no update found, it gives the reason and the download link
  for the latest release; the database and the outbox are kept either way
  (`src/DailyDocket.App/App.xaml.cs`, `src/DailyDocket.Data/DatabaseTooNewException.cs`).

- **A failed start left DailyDocket running with no window**, so starting it again did nothing.
  It now says it could not start and exits (`src/DailyDocket.App/App.xaml.cs`).
