# DailyDocket v26.3.2

Slack replies that cannot go now say why, and a reply can no longer vanish without a word.

## Bug Fixes

- **A Slack reply in a channel shared with another organization** (Slack Connect) was held with
  "DailyDocket could not confirm whether this went out", though nothing was posted. Slack's
  connector cannot post to such channels. DailyDocket now checks for members of another
  organization before sending, and marks the reply failed with that reason, so you know to reply
  in Slack itself (`src/DailyDocket.Core/Outbox/OutboxRules.cs`, `OutboxWorker.cs`).

- **A reply could vanish without a word when you pressed Send.** One Slack reply never reached
  the outbox, and nothing was logged. If the reply box has lost track of its conversation,
  **Send** now says the reply was not sent and how to keep your text. Every reply opened, queued,
  closed or discarded is now logged by its id and conversation, never its text, so a repeat can be
  traced (`src/DailyDocket.App/ViewModels/ComposeViewModel.cs`).
