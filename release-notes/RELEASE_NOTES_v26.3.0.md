# DailyDocket v26.3.0

Schedule and To do side by side on the first tab, and fixes to 26.2.0's Setup from a code review.

## New Features

- **Schedule and To do side by side.** The first tab, now **SCHEDULE & TO DO**, shows the week's
  schedule on the left and the to-do list on the right, as the dashboard artifact did, so both are
  in view at once. A window narrower than 900 pixels stacks them, the schedule above. **EMAIL** and
  **SLACK** keep their own tabs (`src/DailyDocket.App/MainWindow.xaml`).

- **Setup can stay closed at startup.** A check box in Setup, **Open Setup when DailyDocket starts
  and something is missing**, turns off its opening by itself, for a computer kept without one
  piece on purpose.

## Bug Fixes

- **Fixes to 26.2.0's Setup and data repository download**, from a code review
  (`src/DailyDocket.Core/Sync/DataSync.cs`, `src/DailyDocket.Core/Setup/SetupChecks.cs`):
  - Pressing **Download** while the first download was still running started a second one that
    deleted the first one's folder; the second now waits and finds the first one's copy.
  - Changing to another repository could move aside commits that had never reached GitHub when
    the old repository had never been pushed to; those now keep it in place.
  - After a change of repository, a to-do, verdict or brief written before the new download left
    the folder blocked ("already has files"). Such files are now moved aside, never deleted, and
    the download goes ahead.
  - A download that could not be moved into place (a file held by antivirus) stopped syncing
    without a word; it now shows why in the banner.
  - A data folder setting ending in a backslash put the download inside the folder itself.
  - A copy whose GitHub address could not be read was kept when changing repositories, so the
    app went on using it; it is now moved aside like any other.
  - An SSH address without an SSH key on GitHub showed as "could not be reached"; Setup now says
    to add a key or use the https address.
