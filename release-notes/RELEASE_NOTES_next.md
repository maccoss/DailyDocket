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
  it off; edit or remove it; move it up or down (or Alt+Up and Alt+Down); filter by tag. Due dates
  read "today", "tomorrow" or "3d late", and each tag kept one color everywhere. The list lived in
  the private data repository (maccoss/DailyDocket-Data), one file per task, so every computer
  showed the same list, changes made offline were sent when the network returned, and a task
  changed on two computers at once kept the other version on a set-aside branch.
- **To do and Handled on Email and Slack.** **To do** turned the thread being read into a task
  (linked to it) and marked the thread handled; **Handled** hid it until someone wrote again, with
  **Undo** right after. Handled threads were never sent to Claude for judging.
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
- **Encrypted local copy.** The local copy of mail, calendar and Slack was encrypted with
  SQLCipher, with its key kept in Windows Credential Manager.

## Bug Fixes

- A reply that may have gone out but could not be confirmed was shown as **Not sent**, where
  **Send again** could deliver it twice. It is now held with a note to check the conversation
  first; only a send the guard refused shows as not sent.
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

- Closing DailyDocket no longer showed "DailyDocket could not start: The CancellationTokenSource
  has been disposed." The window's cleanup ran twice on exit (`src/DailyDocket.App/ViewModels/MainViewModel.cs`),
  and a problem while cleaning up is now logged rather than reported as a failed start.

## Performance

## Breaking Changes
