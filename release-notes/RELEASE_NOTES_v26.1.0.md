# DailyDocket v26.1.0

The first release: the Schedule, To do, Email and Slack tabs from a local copy that works offline, replies sent through a guarded outbox, Claude drafting and judging, and meeting briefs and an older-mail sweep run each night by one computer.

## New Features

- **Schedule, Email and Slack tabs, read-only.** DailyDocket opened with the dashboard's Schedule,
  Email and Slack, read from a local copy, so they showed the last sync with no network. The
  Schedule listed the next seven days across the calendars in `docket.yaml`, flagged clashes
  between primary calendars and invitations not yet answered, and showed department events
  quietly. Email listed unsent drafts first, then threads waiting on a reply with Claude's
  one-line reason; machine mail, invitations, threads where Mike had the last word, and
  conversations he was only copied on were set aside. Slack listed DMs and mentions waiting on
  him, the same way. Selecting a thread or conversation showed it in full beside the list, newest
  message first, with each reply's quoted history folded away and Slack's markup shown as names;
  it was read from the local copy, so it read offline.
  (`src/DailyDocket.App`, `src/DailyDocket.Core/Jobs`)
- **To do.** The To do tab held the task list: add a task with details, tags and a due date; tick
  it off; edit or remove it; drag it to a new place or move it up or down (or Alt+Up and Alt+Down); filter by tag. Due dates
  read "today", "tomorrow" or "3d late", and each tag kept one color everywhere. The list lived in
  the private data repository (maccoss/DailyDocket-Data), one file per task, so every computer
  showed the same list, changes made offline were sent when the network returned, and a task
  changed on two computers at once kept the other version on a set-aside branch.
- **To do and Handled on Email and Slack.** **To do** turned the thread being read into a task
  (linked to it) and marked the thread handled; **Handled** hid it until someone wrote again, with
  **Undo** right after. Handled threads were never sent to Claude for judging.
- **Claude drafts the task.** The To do form on an email or Slack conversation gained **Draft with
  Claude**, which filled in a title, a line of notes, one of Mike's existing tags, and a due date
  only when the message implied one.
- **The dashboard artifact's to-dos came across.** `DailyDocket.exe --import-artifact <folder>`
  brought the artifact's 22 to-dos and 238 Handled marks into the data repository, in the order
  the artifact showed them, and moved `docket.yaml` there so every computer shares it.
- **Reply in the app.** **Reply** and **Reply all** on an email, and **Reply** on a Slack
  conversation, opened a reply box under it: recipients as chips that could be removed or added
  (email), and bold, italic, underline, bulleted and numbered lists and links. Email went as
  formatted HTML with a plain-text copy; Slack as Markdown. The reply was saved as a draft on every
  pause, offline included, and reopened with its conversation.
- **The outbox.** **Send** queued a reply for 60 seconds (`"UndoWindowSeconds"`) with **Undo**,
  then sent it once the computer was online. If anyone had written in the conversation since the
  reply was started, it was held for a look instead, with **Send again**, **Edit** and **Discard**;
  a reply refused by Gmail or Slack said why. A send could only deliver exactly what was written,
  to exactly the recipients shown, once: the run that sends never reads mail, and a hook refuses
  any other call. Unsent replies were also kept in `outbox.json`.
- **Accept, Maybe and Decline on the Schedule.** An invitation could be answered from its row; the
  response waited out the undo window like a reply, showed as "You: accepted (sending)", and went
  through the same guarded outbox.
- **Threads marked read in Gmail.** Replying to a thread or marking it **Handled** also marked it
  read in Gmail (`"MarkReadInGmail"`, on by default), so Gmail no longer showed handled mail as new.
- **Save to Gmail drafts.** The reply box could put a reply in Gmail's drafts instead of sending it,
  to finish on the phone or in Gmail.
- **Draft with Claude.** The reply box's **Draft with Claude** wrote a draft from the conversation,
  in Mike's voice from his recent emails, for him to edit; an optional **Say** line told it what
  the reply should say. It never sent anything and never decided for him: a reply that turned on
  his decision came back with the choice in brackets. Drafts never contained em dashes.
- **Reply guides.** A guide in the data repository's `config/replies` folder (one Markdown file per
  kind of email, with the keywords that identify it) told Draft with Claude how Mike answers that
  kind of email; the first, for prospective graduate students, came from his own replies and
  corrections. The note under the box said when a guide was used.
- **Syncing.** DailyDocket synced on start, every 15 minutes (`"PullIntervalMinutes"`) and on
  **Refresh**, through Claude Code with read-only tools: it could not send, reply or change
  anything. Mail threads were read only when they changed, and Claude judged each conversation
  once, until someone wrote in it again. The status line showed when each source last synced.
- **Setup.** Personal details (addresses, Slack id, calendars, extra machine senders) were read
  from `%LOCALAPPDATA%\DailyDocket\docket.yaml`; `docs/docket.example.yaml` shows the format. A
  banner said what was missing, including when Claude Code needed `claude auth login --claudeai`.
- **Installer and updates.** DailyDocket installed from `MacCoss.DailyDocket-win-x64-Setup.exe` or
  `MacCoss.DailyDocket-win-arm64-Setup.exe` (for ARM64 computers such as the Surface) on the GitHub
  Release, with no administrator rights, and kept
  itself up to date: it looked for a new release at startup and every four hours, downloaded it in
  the background, and showed "Update ready: restart to install" with **Restart** above the status
  line. It never restarted by itself, waited for a reply that was mid-send, and stayed quiet when
  offline. Each computer took only builds for its own processor, so the ARM64 Surface never ran an
  x64 build under emulation; `"BetaUpdates": true` in `settings.json` took beta releases too.
  (`src/DailyDocket.App/Services/UpdateService.cs`, `.github/workflows/release.yml`)
- **Setup checks.** At startup the banner said when Claude Code was not installed, when it was not
  signed in (`claude auth login --claudeai`), or when Git was missing, each with the command that
  fixes it, and cleared by itself on returning to the window once fixed.
  (`src/DailyDocket.Core/Setup/SetupChecks.cs`)
- **An app icon.** A teal square with a white D, on the window, the taskbar and the Start menu.
- **Meeting briefs.** Every meeting in the next two working days got a one-screen brief from
  Claude: who is coming and their role, what the meeting is for, the email threads and documents
  that led to it, what Mike owes those people, and what is still unclear. Briefs were written each
  night and at a midday catch-up by one computer (`nightly.machine` in `docket.yaml`; a fallback
  took over when it was off for more than two hours), saved in the data repository, and shown under
  each event on the Schedule as **Brief**, offline too. A brief was remade when its event changed,
  and said "may be out of date" until then. **Prep now** on any event wrote one at once. Declined,
  all-day and home-calendar events, meetings with no one else invited, and department events Mike
  had not accepted were skipped (`prep` in `docket.yaml`). Prep runs could only read Calendar, Gmail
  and Drive, and could save only the brief for their own meeting.
- **Verdicts shared between computers.** Claude's "needs a reply" verdicts were saved in the data
  repository (`verdicts/email`, `verdicts/slack`), so a conversation judged on one computer was
  not judged, or paid for, again on the others. A verdict for an older message never replaced one
  for a newer message.
- **Old unanswered mail surfaced.** Each night the designated computer also read the inbox older
  than the 30-day window, back 90 days (`mail.deepDays`), and Claude judged what it found, so old
  threads still waiting on a reply appeared on the Email tab.
- **Notification area and notifications.** Minimizing put DailyDocket in the notification area
  (`"MinimizeToTray"`), where it kept syncing; clicking the icon brought it back. A Windows
  notification said when something new needed Mike after a sync: an email or Slack conversation
  newly waiting on a reply, or a reply that was held or not sent. Clicking it opened the window on
  that tab and conversation. Notifications could be turned off from the icon's menu
  (`"Notifications"`).
- **Encrypted local copy.** The local copy of mail, calendar and Slack was encrypted with
  SQLCipher, with its key kept in Windows Credential Manager. `outbox.json` was encrypted for the
  Windows account, and no plain-text copies of mail were kept: each sync's working folder was
  deleted after two hours, and Claude Code kept no transcript of the mail a sync read.
- **Safer drafts.** A Claude draft could not contain a link or web address that was not already in
  the conversation (it was replaced by "[link removed: not in the conversation]"), hidden
  characters, or text pushed far down by blank lines, so an email written to trick Claude could
  not hide anything in a reply that Mike might miss before pressing Send.

