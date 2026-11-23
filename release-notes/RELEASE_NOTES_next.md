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

## Bug Fixes

- A reply that may have gone out but could not be confirmed was shown as **Not sent**, where
  **Send again** could deliver it twice. It is now held with a note to check the conversation
  first; only a send the guard refused shows as not sent. A send that went out but whose retry was
  then refused is also held, not shown as not sent.
- If the send guard crashed, timed out or could not start, the send went ahead unchecked. A send
  now runs only when the guard has explicitly allowed it, and two identical calls made at the same
  moment cannot both get through.
- The last second or so of typing in a reply was lost when switching threads or closing the app.
  The reply box now saves before it switches, closes, or the app exits.
- When the local database could not be opened (for example after its key was lost), it was deleted
  along with unsent replies. It is now kept beside the new one, and unsent replies come back from
  `outbox.json`.
- When the same to-do was changed on two computers, the computer that synced second also lost its
  other offline changes from the list. Now only the conflicting task's version is set aside.
- An address typed incorrectly in the To or Cc box was silently left out of the reply. **Send** now
  stops and says which text is not an address.
- A to-do sync that failed in the background now says so in the window instead of failing silently,
  and a problem saving one sync's results no longer stops syncing until restart.

- When two computers judged the same new email, or wrote the same brief, within one sync, the
  window said "A to-do was changed on two computers at once" and kept a set-aside branch. Verdicts,
  briefs and the nightly lease now take the copy already sent from the other computer; the banner
  appears only when a to-do really conflicts.
- Every time someone answered a meeting invitation, its brief was marked out of date and written
  again at the next run. A brief is now remade only when the meeting's time, title, description,
  location or invited people change.
- Two computers could both run the night's sweep and briefs. A computer now claims the run in the
  data repository before starting, and one that finds another's claim stands down.
- One meeting brief that failed made the whole nightly run, older-mail search included, start
  again every hour. Once the search has gone through, the night counts as done, and only the
  failed briefs are tried again, up to three times.
- Nightly failures were never shown, and a designated computer that kept failing (for example,
  signed out of Claude) never handed the job to its fallback. The banner now says what went wrong
  and what to do, and a fallback takes over after two failed runs in a row.
- After the window closed, a sync or nightly run that was finishing could still update the window
  and push to GitHub while DailyDocket exited. Closing now stops both; an unfinished nightly run is
  noted on disk and sent at the next start.
- **Prep now** on a meeting the nightly run was preparing at that moment started a second,
  paid-for run. It now says the brief is already being prepared.
- A computer named, for example, `null`, `yes` or `no` could not hold the nightly lease: its name
  read back as something else. The name is now quoted in `state/lease.yaml`.

- Closing DailyDocket no longer showed "DailyDocket could not start: The CancellationTokenSource
  has been disposed." The window's cleanup ran twice on exit (`src/DailyDocket.App/ViewModels/MainViewModel.cs`),
  and a problem while cleaning up is now logged rather than reported as a failed start.

- A meeting whose invitation came from another calendar system could have an event id too long for Git on Windows to save as a file name, and once its brief was written every later to-do sync failed. Such a brief is now saved under a short name.

## Performance

## Breaking Changes
