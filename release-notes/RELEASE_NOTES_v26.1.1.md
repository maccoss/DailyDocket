# DailyDocket v26.1.1

Email older than a day or two is no longer missed, answered conversations leave the list at once, group DMs show who is in them, and meetings get a Join button.

## New Features

- **Join on the Schedule.** A meeting with a Zoom, Teams, Google Meet or Webex link (in Google's
  conference details, the location or the invitation's text) shows **Join**, which opens it. Only
  links to those services become a button, and only the link is kept, not the invitation's text.
  (`src/DailyDocket.Core/Calendar/CalendarParser.cs`)

- **Group DMs name their members.** A group DM on the Slack tab reads "Group DM · " and the
  others' names, as Slack lists it, instead of only "Group DM".
  (`src/DailyDocket.Core/Slack/SlackRules.cs`)

## Bug Fixes

- **Email could be missed entirely.** Gmail's search returns at most 50 threads and no next page,
  so a sync over 30 days saw 50 of several hundred threads, and an important email from four days
  earlier never reached the Email tab. Each day is now searched on its own (31 searches, none near
  the limit; a day that reaches it is noted in the log), the nightly sweep of older mail the same
  way, and each sync reads up to 120 changed threads instead of 40, so the first syncs after
  updating catch up within about an hour. (`src/DailyDocket.Core/Jobs/JobPlans.cs`)
- **A reply already sent stayed on the list.** After a reply to an email or Slack conversation went
  out, the conversation was still listed as needing a reply until the next sync read the reply
  back (up to 15 minutes), and on other computers until their own next sync. A sent reply now
  marks its conversation handled at once, on every computer; it returns if someone writes again.

- An open **Brief** closed by itself within seconds, whenever the window refreshed after a sync
  step. It now stays open until it is closed. (`src/DailyDocket.App/ViewModels/Items.cs`)
- After **Accept**, **Maybe** or **Decline**, the meeting still said "not answered" until the next
  calendar sync, up to 15 minutes later. It now clears as soon as the response is made.
