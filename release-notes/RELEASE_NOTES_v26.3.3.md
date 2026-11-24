# DailyDocket v26.3.3

Email reading is unstuck: long threads are read again, and one that cannot be read no longer
holds up the rest.

## Bug Fixes

- **The Email tab stopped catching up.** Claude Code refuses a connector result over a size limit,
  so a long email thread came back as an error instead of the thread. DailyDocket never counted
  such a thread as read and asked for it first on every sync: 39 of them took every read, and
  about 600 threads (including ones you had already answered in Gmail) waited behind them. Syncs
  now allow results four times larger, a thread larger still is read from the file Claude Code put
  it in, and a thread whose reads keep failing waits behind the others instead of blocking them
  (`src/DailyDocket.Hook/Capture.cs`, `src/DailyDocket.Core/Jobs/JobRunner.cs`,
  `src/DailyDocket.Data/Store.cs`).

- **Your own Slack messages could show under a colleague's name.** When Slack gave one of your
  messages no name, DailyDocket took the name from a later message in the same result. It now
  uses the name Slack gives that person elsewhere in the result
  (`src/DailyDocket.Core/Slack/SlackParser.cs`).
