# DailyDocket vNEXT Release Notes

Working draft for the next release. Append entries in the same commit as each user-visible change;
rename to `RELEASE_NOTES_v{version}.md` at release time. See `README.md` here: past tense, user
impact first, empty headings deleted at release.

## New Features

- **Updates at the upper right.** When a new version is downloaded, a green **Restart to update to
  26.x.x** button appears at the upper right, beside **Refresh**, as in LabOps; the notice above the
  status line, easy to miss, is gone. (`src/DailyDocket.App/MainWindow.xaml`)

## Bug Fixes

- A Slack DM answered with **Reply** in a thread (Slack's reply on a message) stayed listed as
  needing a reply: the message that started the thread was counted with the DM, where the last word
  was still theirs. A thread's first message now belongs to the thread, as in Slack, so answering it
  clears it until someone writes again. (`src/DailyDocket.Core/Slack/SlackRules.cs`)

## Performance

## Breaking Changes
