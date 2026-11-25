# DailyDocket v26.3.4

To-dos are reordered by dragging, notifications in the corner of the screen are now only for
replies that did not send unless you choose otherwise, and DailyDocket can no longer get stuck on
a version older than its database.

## New Features

- **Notifications are for replies that were held or did not send, by default.** The Email and
  Slack tabs still list everything waiting on you, and most of it is old by the time it is listed.
  To choose, right-click DailyDocket's icon in the notification area and pick under
  Notifications: only replies that did not send, everything new that needs a reply (as before),
  or none. If you had turned notifications off, they stay off
  (`src/DailyDocket.Core/Infrastructure/SettingsStore.cs`, `src/DailyDocket.App/Services/TrayIcon.cs`).

- **To-dos are reordered by dragging.** Each card has a grip in place of the up and down arrows,
  a line shows where the dragged card will land, the gaps between cards take a drop too, and the
  list scrolls when you drag near its top or bottom. Alt+Up and Alt+Down still move a task
  (`src/DailyDocket.App/Views/TodoTab.xaml.cs`).

## Bug Fixes

- **A copy older than its database could never update itself.** It showed "written by a newer
  version of DailyDocket" and stopped before its update check ran, so it never found the version
  it needed. It now looks for that update in a small window that shows the download, then installs
  it when you press OK. Offline, or with no update found, it gives the reason and the download link
  for the latest release; the database and the outbox are kept either way
  (`src/DailyDocket.App/App.xaml.cs`, `src/DailyDocket.Data/DatabaseTooNewException.cs`).

- **A failed start left DailyDocket running with no window**, so starting it again did nothing.
  It now says it could not start and exits (`src/DailyDocket.App/App.xaml.cs`).

## Performance

- **The To do list settles at once after a drop or a check mark.** Cards that did not change are
  kept instead of every card being drawn again (`src/DailyDocket.App/ViewModels/TodoViewModel.cs`).
