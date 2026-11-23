# DailyDocket v26.1.2

Mail sent to the lab's group addresses is no longer missed, a Slack DM answered in a thread clears, and a new version shows as a green button at the upper right.

## New Features

- **Updates at the upper right.** When a new version is downloaded, a green **Restart to update to
  26.x.x** button appears at the upper right, beside **Refresh**, as in LabOps; the notice above the
  status line, easy to miss, is gone. (`src/DailyDocket.App/MainWindow.xaml`)

## Bug Fixes

- Email sent to a group address (such as the lab's services address) never reached the Email tab:
  Gmail files mail that arrives through a Google Group under Forums, which DailyDocket leaves out.
  Addresses listed under `mail.groups` in `docket.yaml` are now read wherever Gmail files them;
  the rest of Forums (department lists, notifications) still is not.
  (`src/DailyDocket.Core/Jobs/JobPlans.cs`, `docs/docket.example.yaml`)

- A Slack DM answered with **Reply** in a thread (Slack's reply on a message) stayed listed as
  needing a reply: the message that started the thread was counted with the DM, where the last word
  was still theirs. A thread's first message now belongs to the thread, as in Slack, so answering it
  clears it until someone writes again. (`src/DailyDocket.Core/Slack/SlackRules.cs`)
