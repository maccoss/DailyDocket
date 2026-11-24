# DailyDocket v26.3.1

Email you answered in Gmail now leaves the Email tab at the next sync.

## Bug Fixes

- **An email you answered in Gmail stayed on the Email tab** until someone else wrote in the
  thread. DailyDocket searched only the inbox, and a reply you send from Gmail sits in Sent. Each
  mail pull now also searches your Sent mail for today and the two days before, as the dashboard
  did, and reads the thread again when it finds a reply it has not seen
  (`src/DailyDocket.Core/Jobs/JobPlans.cs`, `src/DailyDocket.Data/CaptureIngestor.cs`).

- **A reply that came in after yours was missed**, such as a quick "Thanks". Gmail's search does
  not reliably name a thread's newest message, so DailyDocket now reads again any thread you may
  owe a reply on every 10 minutes, and any other thread active in the last two days every 30
  minutes (`src/DailyDocket.Data/Store.cs`).

## Performance

- **Fewer thread reads per sync.** Every thread you had already answered was read again three
  times on every sync, because Gmail's search named an older message than the one DailyDocket had
  read. A thread is now read only when something may have changed.
