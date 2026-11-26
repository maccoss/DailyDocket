# DailyDocket vNEXT Release Notes

Working draft for the next release. Append entries in the same commit as each user-visible change;
rename to `RELEASE_NOTES_v{version}.md` at release time. See `README.md` here: past tense, user
impact first, empty headings deleted at release.

## New Features

- The Email and Slack lists can be sorted: oldest first (as before), newest first, by sender, or
  by subject (Email) or channel (Slack). Pick from Sort above each list; each tab remembers its
  choice. Unsent drafts stay at the top. With newest first, a new email no longer sits at the
  bottom of a long list.
- Reply on the Slack tab now opens the conversation in the Slack app, so the reply goes out as
  yours. Sending from DailyDocket added a "Sent using @Claude" line that could
  not be turned off. Without the Slack app, the message opens in the browser instead. Replies
  already written in DailyDocket and waiting in the outbox still send.

## Bug Fixes

## Performance

## Breaking Changes
