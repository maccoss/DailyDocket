# DailyDocket vNEXT Release Notes

Working draft for the next release. Append entries in the same commit as each user-visible change;
rename to `RELEASE_NOTES_v{version}.md` at release time. See `README.md` here: past tense, user
impact first, empty headings deleted at release.

## New Features

- To-dos are reordered by dragging: each card has a grip in place of the up and down arrows, a
  line shows where the dragged card will land, the gaps between cards take a drop too, and the
  list scrolls when you drag near its top or bottom. Alt+Up and Alt+Down still move a task.

## Bug Fixes

- A copy of DailyDocket older than its database no longer stops for good. It looks for the update
  it needs, shows the download, and installs it; offline, it says where to download the latest
  version. Before, it showed an error and could never update itself.
- When DailyDocket could not start for another reason, it no longer kept running without a window,
  which made later starts do nothing.

## Performance

- The To do list settles at once after a drop or a check mark: cards that did not change are kept
  instead of every card being drawn again.

## Breaking Changes
