# DailyDocket v26.4.0

The Email and Slack lists can be sorted, and Reply on the Slack tab opens the conversation in the
Slack app so replies go out as yours.

## New Features

- **Email and Slack: sort the list.** Pick from Sort above each list: oldest first (as before),
  newest first, by sender, or by subject (Email) or channel (Slack). Each tab remembers its choice,
  and unsent drafts stay at the top. With newest first, a new email no longer sits at the bottom
  of a long list (`src/DailyDocket.Core/Infrastructure/ListOrder.cs`).

- **Slack: Reply opens the conversation in the Slack app.** Sending from DailyDocket added a
  "Sent using @Claude" line that could not be turned off, so the reply now goes out from Slack as
  yours. Without the Slack app on the computer, the message opens in the browser instead. Replies
  already written in DailyDocket and waiting in the outbox still send
  (`src/DailyDocket.Core/Slack/SlackLinks.cs`).
