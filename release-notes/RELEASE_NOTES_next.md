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

## Bug Fixes

## Performance

## Breaking Changes
