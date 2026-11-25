# DailyDocket vNEXT Release Notes

Working draft for the next release. Append entries in the same commit as each user-visible change;
rename to `RELEASE_NOTES_v{version}.md` at release time. See `README.md` here: past tense, user
impact first, empty headings deleted at release.

## New Features

- Notifications in the corner of the screen are now only for replies that were held or did not
  send, by default; the Email and Slack tabs still list everything waiting on you. To choose,
  right-click DailyDocket's icon in the notification area and pick under Notifications: only
  replies that did not send, everything new that needs a reply (as before), or none. If you had
  turned notifications off, they stay off.
- To-dos are reordered by dragging: each card has a grip in place of the up and down arrows, a
  line shows where the dragged card will land, the gaps between cards take a drop too, and the
  list scrolls when you drag near its top or bottom. Alt+Up and Alt+Down still move a task.

## Bug Fixes

## Performance

- The To do list settles at once after a drop or a check mark: cards that did not change are kept
  instead of every card being drawn again.

## Breaking Changes
