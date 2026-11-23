# DailyDocket vNEXT Release Notes

Working draft for the next release. Append entries in the same commit as each user-visible change;
rename to `RELEASE_NOTES_v{version}.md` at release time. See `README.md` here: past tense, user
impact first, empty headings deleted at release.

## New Features

- **Schedule, Email and Slack tabs, read-only.** DailyDocket opened with the dashboard's Schedule,
  Email and Slack, read from a local copy, so they showed the last sync with no network. The
  Schedule listed the next seven days across the calendars in `docket.yaml`, flagged clashes
  between primary calendars and invitations not yet answered, and showed department events
  quietly. Email listed unsent drafts first, then threads waiting on a reply with Claude's
  one-line reason; machine mail, invitations, threads where Mike had the last word, and
  conversations he was only copied on were set aside. Slack listed DMs and mentions waiting on
  him, the same way. Each row opened in Gmail, Google Calendar or Slack.
  (`src/DailyDocket.App`, `src/DailyDocket.Core/Jobs`)
- **Syncing.** DailyDocket synced on start, every 15 minutes (`"PullIntervalMinutes"`) and on
  **Refresh**, through Claude Code with read-only tools: it could not send, reply or change
  anything. Mail threads were read only when they changed, and Claude judged each conversation
  once, until someone wrote in it again. The status line showed when each source last synced.
- **Setup.** Personal details (addresses, Slack id, calendars, extra machine senders) were read
  from `%LOCALAPPDATA%\DailyDocket\docket.yaml`; `docs/docket.example.yaml` shows the format. A
  banner said what was missing, including when Claude Code needed `claude auth login --claudeai`.
- **Encrypted local copy.** The local copy of mail, calendar and Slack was encrypted with
  SQLCipher, with its key kept in Windows Credential Manager.

## Bug Fixes

## Performance

## Breaking Changes
