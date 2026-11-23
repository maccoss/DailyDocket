# DailyDocket vNEXT Release Notes

Working draft for the next release. Append entries in the same commit as each user-visible change;
rename to `RELEASE_NOTES_v{version}.md` at release time. See `README.md` here: past tense, user
impact first, empty headings deleted at release.

## New Features

- **Meeting links open the meeting.** **Join** and the meeting's link under its title now start the
  Zoom or Teams app directly when it is installed, rather than a browser page that asks to; Meet,
  Webex, and computers without the app open the link in the browser as before. A meeting whose link
  is only in the invitation's text now shows it too (short, without its password; a Teams link as
  "Microsoft Teams meeting"). (`src/DailyDocket.Core/Calendar/MeetingLinks.cs`)

- **Links in To do notes open.** A web address in a task's notes, such as a Gmail link back to the
  email, is a link that opens in the browser. (`src/DailyDocket.App/Views/LinkedText.cs`)

## Bug Fixes

- After 26.1.2 began grouping a Slack thread with the message that started it, a DM conversation
  marked **Handled** could come back, and an earlier message in a DM answered with **Reply** in a
  thread could show as waiting. A Handled mark on the DM now covers its threads, and a reply
  anywhere in a DM answers what came before it. (`src/DailyDocket.Core/Slack/SlackRules.cs`)
- **Join** could fail with an error where a meeting app's link type was registered but no longer
  worked (a removed Teams); it now falls back to the browser.

## Performance

## Breaking Changes
