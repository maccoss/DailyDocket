# Local Dashboard (.NET) Design

Nov 22, 2026 · @Michael MacCoss

Build a Windows desktop app in C# (.NET 10) that mirrors Mike's Dashboard artifact (Schedule, To do, Email needing a reply, Slack needing a reply), keeps working with no network, and sends what was written offline once a connection returns.

## Goals and constraints

The app keeps a local copy of everything it shows, so reading, triage, writing replies, RSVPs and to-do edits all work offline. Gmail, Google Calendar and Slack stay the single source of truth across machines; the app never tries to be a second mailbox. Only the to-do list (and a little dashboard state) needs its own sync, through a private GitHub repo.

| Machine | CPU / accelerator | RAM | Build target | Local model runs on |
| --- | --- | --- | --- | --- |
| pillar (work desktop) | Intel i9-14900K, RTX 4060 Ti 16 GB | 64 GB | win-x64 | NVIDIA GPU (CUDA) |
| SpeedyGonzales | AMD Ryzen AI 9 HX 370, Radeon 890M, XDNA 2 NPU | 96 GB | win-x64 | NPU or CPU, whichever Foundry Local picks |
| Surface Laptop 7 | Snapdragon X Elite, 45 TOPS Hexagon NPU | 32 GB | win-arm64 | Hexagon NPU (QNN) |
| skiddaw (least used) | Intel i9-12900K | 64 GB | win-x64 | CPU; runs the nightly sweep and meeting prep |

Hard constraints:

- **Native on ARM64 and x64.** One codebase, published twice (`win-x64`, `win-arm64`). No x64 emulation on the Surface.
- **No new cloud service.** Claude Code in headless mode (`claude -p`) is the only path to Gmail, Calendar and Slack, through the claude.ai connectors already set up. No Google Cloud OAuth app or Slack app to register (Phase 1 at least; see Open decisions).
- **Offline is normal, not an error.** Every write goes to a local outbox first. Nothing is lost if the lid closes mid-send.
- **Nothing sends without Mike pressing Send.** Claude and the local model only draft.
- **Windows 11 24H2 or later** on all four machines (required by Foundry Local).

## Architecture

&#91;embedded content: DailyDocket architecture · one machine and the online services\]

The app reads only from its SQLite cache and writes only to the outbox or the git clone, so nothing Mike does waits on the network. When a connection exists, `claude -p` runs move data between the cache and Gmail, Calendar and Slack, and git syncs the to-dos. The three machines never talk to each other directly.

## Sync engine: Claude headless as the transport

Each sync is one `claude -p` run with Mike's own Claude login (team seat, no API key), started and read by the same `ClaudeSession` and `StreamJsonParser` LabOps uses. Since Claude Code 2.1.83, the claude.ai connectors (Gmail, Calendar, Slack) load in `-p` mode, as long as `--bare` is not used ([source](https://claudeissues.com/issue/38567-docs-claude-ai-mcp-connectors-slack-gmail-etc-availability-in-print-p-mode-is-un)). The model decides what to fetch; it does not retype what it fetched.

**Raw capture through hooks, not transcription.** A `PostToolUse` hook on `mcp__claude_ai_Gmail__*`, `mcp__claude_ai_Google_Calendar__*` and `mcp__claude_ai_Slack__*` hands every tool result, verbatim, to a small `DailyDocket.Hook.exe`, which posts it to the app. C# parsers (ported from the artifact, which already parses the Gmail payloads and Slack's labeled text reports) turn those captures into rows. Three reasons: no output tokens spent copying mail, no chance of a paraphrased body, and the parsers are unit-testable against recorded responses.

The same hook can then shrink what the model reads: `PostToolUse` may return `updatedToolOutput`, which replaces a tool's output before Claude sees it ([source](https://code.claude.com/docs/en/hooks)). For pulls, the hook swaps a full thread for a short receipt (thread id, message count, next page token), so a 200-thread sweep costs paging decisions, not 200 bodies of input tokens.

**What the model does write** goes through the app's local MCP server (LabOps' `AppToolServer` pattern: 127.0.0.1, random port, per-run bearer token). Those tools are typed and validated, for example `docket_get_watermarks`, `docket_get_untriaged`, `docket_record_verdicts`, `docket_get_outbox`, `docket_claim_outbox_item`, `docket_mark_sent`.

| Job | When | Model | Connector tools allowed | DailyDocket tools |
| --- | --- | --- | --- | --- |
| Calendar pull | Every 15 min online, and on Refresh | Haiku | list\_calendars, list\_events, get\_event | get\_watermarks |
| Mail pull | Every 15 min, and on Refresh | Haiku | search\_threads, get\_thread, list\_drafts | get\_watermarks |
| Slack pull | Every 15 min, and on Refresh | Haiku | search (public and private), read\_thread, get\_reactions, read\_user\_profile | get\_watermarks |
| Triage | After each mail or Slack pull, new or changed items only | Sonnet | none | get\_untriaged, record\_verdicts |
| Send | Outbox not empty and online | Haiku | Gmail reply, send\_message, create\_draft; Slack send\_message; Calendar respond\_to\_event, create\_event, update\_event | get\_outbox, claim\_outbox\_item, mark\_sent, mark\_failed |
| Deep sweep | Nightly, one designated machine | Sonnet | Mail and Slack read tools | record\_verdicts (shared through git) |
| Meeting prep | Nightly for the next two working days, a midday catch-up, and on demand; one designated machine | Sonnet | Calendar, Gmail, Drive read tools | write\_brief (shared through git) |

Watermarks keep pulls small: mail asks for `after:<last sync>` plus the in:sent and drafts sweeps the artifact needed, Slack asks for `after:<date>` and follows the cursor to the end. Triage reads thread text from the local database, never from a connector, so it can also run on the local model when offline (marked provisional and re-judged by Claude on reconnect).

If pull runs cost too much of the team seat's usage, the pull side can later move to direct Gmail, Calendar and Slack APIs behind the same `ISource` interface. The database, rules and UI would not change.

## Offline compose and the outbox

Everything Mike writes lands in a local outbox first, online or not, and is sent only by the Send job, one item at a time, with a check that the conversation has not moved on since he wrote.

What can be queued:

- Email: reply, reply all, new message, or "save to Gmail drafts" (so an offline draft shows up on his phone and the other two machines once online).

Writing a reply (phase 4, from Mike, November 2026): it is written in the app, under the thread he is reading, never by opening Gmail. The recipients start as the sender (Reply) or everyone on the thread (Reply all) and each one can be removed or added, so a reply can go to a subset of the people. The compose box has basic formatting (bold, italic, underline, bulleted and numbered lists, links), sent as `htmlBody` with a plain-text `body` alternative. The connector supports all of this: `reply` takes `messageId`, `replyAll`, explicit `to`, `cc` and `bcc` lists that override the defaults, `body` and `htmlBody`; `send_message` takes `replyThreadId` for a send that must thread. The outbox item stores the exact recipients and both bodies, and the send guard matches them exactly. Claude helps draft in phase 5 (a Draft with Claude button that fills the compose box for Mike to edit), never sending.
- Slack: reply in a thread, or a DM.
- Calendar: accept, decline or tentative; create or move an event.
- Dashboard state: Handled marks and to-dos go to git instead (next section), not the outbox.

An item moves through these states:

1. **Draft.** Saved locally on every keystroke pause. Never leaves the machine.
2. **Queued.** Mike pressed Send. A 60-second undo window starts (setting), even when online.
3. **Sending.** The Send job claimed it (`docket_claim_outbox_item` records the time and the machine).
4. **Sent.** The connector returned a message id or event id, stored as `remoteId`.
5. **Held.** The pre-send check found something new in the conversation: a reply from someone else, or Mike's own reply from his phone or another machine. The new messages are shown above his draft and he confirms, edits or discards. Nothing held is sent on its own.
6. **Failed.** The connector refused (expired sign-in, rate limit, bad address). Shown with the reason; retried only when Mike asks or the reason was a rate limit.

Each queued item records its **base**: the newest message id in the email thread, or the newest `ts` in the Slack conversation, at the moment he started writing. The Send job first pulls that one conversation fresh; if anything is newer than the base, the item goes to Held.

Three rules make sending safe to automate:

- **Exact text, enforced by a hook.** A `PreToolUse` hook on every send tool compares the call's recipient, thread and body, byte for byte, with the claimed outbox item and denies anything else. Claude cannot reword, add a recipient or send a second message, whatever it was told.
- **No double sends.** An item stuck in Sending (machine slept mid-send) is never resent blindly. The next run looks for it first, in Gmail `in:sent` after the claim time, or Slack messages from Mike in that conversation, and marks it Sent if found.
- **Senders never read inbound mail.** The Send job gets the outbox and thread ids, not message bodies, so a hostile email cannot steer a send (Security, below).

Attachments are out of scope for offline replies in version 1: the Gmail connector takes attachments only as base64 typed into the call, which fails on larger files. A reply that needs one is saved to Gmail drafts and finished in Gmail.

## To-dos and dashboard state in GitHub

The to-do list, and the little dashboard state that Gmail, Calendar and Slack cannot hold, live in one private repository, `maccoss/DailyDocket-Data` (separate from the app's code), one file per item, synced with LabOps' `SyncService` (commit to main, rebase before push, never merge or force-push). Git already works offline: each machine commits locally and pushes when it can.

```
todos/t3k9f2a1.md          one task: YAML front matter + notes
handled/email/<threadId>.yaml
handled/slack/<channel>~<threadTs>.yaml
verdicts/email/<threadId>.yaml   Claude's needs-reply verdict, keyed to the newest message id
verdicts/slack/<channel>~<threadTs>.yaml
briefs/2026-11-23/<eventId>.md   meeting prep, written by one machine
state/lease.yaml           which machine is running the nightly jobs, and when it is next due
config/tags.yaml           tag color overrides
config/docket.yaml           which machine runs the nightly sweep and meeting prep
```

A task file:

```
---
title: Send Eisai the CSF-EV PRM target list
due: 2026-11-26
tags: [eisai, assays]
rank: "m4"            # fractional rank: a drag rewrites one file
done: false
source: {kind: email, id: 19a2c0f3e8b1, subject: "PRM targets"}
updated: 2026-11-22T16:20:00-08:00
updatedOn: pillar
---
Notes typed by Mike.
```

Why this shape:

- **One file per item** means two machines editing different tasks offline never conflict. The fractional rank the artifact already uses keeps a reorder to one file.
- **Same task edited on two machines offline:** the later `updated` wins, and the other version is kept on a local `set-aside/...` branch (LabOps' `SetAsideAsync`) and flagged in the To do tab.
- **Sharing verdicts and briefs** means the nightly sweep and meeting prep run on one machine, not three, so the team seat's usage is spent once.
- **Readable anywhere:** GitHub mobile shows the files, and Claude on any device can read the repository.
- **No message bodies in git.** Files carry ids, subjects and what Mike typed (meeting briefs are one exception, below; the other is a to-do's notes when Mike keeps the line **Draft with Claude** suggested, up to 300 characters drawn from the message); the verdict's reason is capped at 70 characters as in the artifact.

## Meeting prep

Every meeting that can be prepped is prepped before the day starts, and the brief lives in the private repository, so every machine shows it under the event, offline included.

**When briefs are made.** One designated machine (skiddaw by default) runs the prep job. It needs no GPU, only to be on and online, because Claude does the work in the cloud. The machine renews a lease file (`state/lease.yaml`) in the repository after each run; if the lease is more than two hours past due, the first other machine online takes the job until the designated one returns. The schedule:

- Nightly, for the next two working days, so a Monday morning meeting is ready Friday night.
- A midday catch-up for events added or changed since the nightly run.
- On demand: a **Prep this** button on any event. Online, Claude writes it; offline, the local model writes a lighter brief from the cached mail and Slack, marked "local", and Claude's version replaces it at the next online run.

**Which events.** Skipped: declined events, all-day events, the home calendar, and Genome Sciences events Mike has not accepted. Everything else on the work and lab calendars gets a brief. The rules live in `config/docket.yaml` so they can change without a release.

**What a brief holds** (one screen, about 200 words):

- Why the meeting exists: the invitation text and the email or Slack threads that led to it.
- Who is coming, with the last exchange Mike had with each.
- What Mike owes these people: open loops from the verdicts, and to-dos whose source involves them.
- Linked documents (Drive, Panorama, manuscripts), as links.
- Two or three suggested questions or decisions to reach.

**File and freshness.** `briefs/2026-11-24/<eventId>.md`, with front matter recording the event's `updated` time and attendee list, the time and machine it was written on, the model, and the thread ids and links it drew on. A brief is marked stale, and remade on the next run, when the event's time or attendees change or a new message arrives in one of its source threads. The Schedule tab shows stale briefs with a "may be out of date" note rather than hiding them.

Briefs are the one place mail-derived text enters git: short summaries written for Mike, never copied messages. The repository is private for this reason.

## AI help: Claude online, a local model offline

One interface, `IAssistant` (built on `Microsoft.Extensions.AI`'s `IChatClient`), with two implementations picked by connectivity: Claude through `claude -p` when online, and Foundry Local in-process when not. The compose box shows which one wrote a draft.

| Task | Online (Claude) | Offline (local model) |
| --- | --- | --- |
| Draft a reply | Full thread from the local DB, plus read-only connector lookups (earlier threads with the sender, a Drive doc) | Text since Mike's last reply, plus 3 to 5 of his own past replies to similar mail as style examples |
| Shorten, firm up, soften | Yes | Yes |
| Summarize a long thread | Yes | Yes, capped input |
| Turn an email or Slack item into a to-do | Yes | Yes |
| Propose meeting times | From live calendars | From the local copy, marked "as of last sync" |
| Triage (needs a reply?) | Authoritative verdict | Provisional; re-judged by Claude on reconnect |

**Local runtime: Foundry Local.** The `Microsoft.AI.Foundry.Local.WinML` package runs in the app's process with no separate server, targets both `win-x64` and `win-arm64`, and needs Windows 11 24H2 or later. Given a model alias such as `phi-4-mini`, it picks the build for the hardware: a QNN NPU variant on Snapdragon, a CUDA variant on NVIDIA, or CPU ([source](https://learn.microsoft.com/fil-ph/windows/ai/foundry-local/get-started)). Foundry Local went generally available in April 2026 ([source](https://devblogs.microsoft.com/foundry/foundry-local-ga/)).

- **Surface Laptop 7:** `phi-4-mini` on the Hexagon NPU. Long battery life matters more than size here.
- **pillar:** the 16 GB RTX 4060 Ti can run a larger model from the catalog; the alias is a setting, chosen from `foundry model list` at setup rather than hard-coded.
- **SpeedyGonzales:** whatever variant Foundry Local resolves for the Ryzen AI 9 HX 370 (its NPU or CPU); 96 GB of RAM leaves room for a larger CPU model.
- Models (a few GB each) download while online, on first run, with progress in the status bar.

**Why not Phi Silica.** Windows' built-in NPU model needs a Copilot+ PC (pillar is not one) and is a limited access feature that needs an unlock token ([source](https://learn.microsoft.com/zh-cn/windows/ai/apis/phi-silica-winui-tutorial)). Foundry Local covers all three machines with one code path.

**House style, enforced in code.** Every draft, from either model, passes a deterministic filter: em dashes replaced, American spelling. A local draft can be marked "polish with Claude before sending"; the polished text comes back as Held for Mike to approve, never sent on its own.

## Local data model

One SQLite file per machine (`%LOCALAPPDATA%\DailyDocket\docket.db`, WAL mode), a cache of the online sources plus the outbox. It can be deleted and rebuilt by a full pull; only the outbox and unsent drafts are irreplaceable, and they are also exported to a JSON file on every change.

| Table | Holds | Key fields |
| --- | --- | --- |
| CalendarEvent | Events from the four calendars (work, home, lab, Genome Sciences) | eventId, calendarKey, start, end, allDay, title, location, attendees, myResponse, htmlLink, updated |
| EmailThread | One row per thread, with the rules' result | threadId, subject, lastMessageAt, lastFromMeAt, arrivedSinceMyReply, hasUnsentDraft, category (auto, invite, informed, owed, loop), verdict, verdictForMessageId |
| EmailMessage | Messages, body capped (the artifact's budget: at least 600 characters per message) | messageId, threadId, from, to, cc, date, bodyText, fromMe |
| SlackConversation | A thread, or a DM channel outside a thread | key (channel\~threadTs), kind (dm, mention), lastAskAt, lastMineAt, reactedByMe |
| SlackMessage | Messages in those conversations | ts, conversationKey, userId, userName, text |
| TodoItem | Read model of the git files | id, title, due, tags, rank, done, source, updated |
| OutboxItem | Everything queued to send | id (GUID), kind, target, baseRef, payload, status, machine, claimedAt, attempts, remoteId, error |
| RawCapture | Hook captures before parsing | id, runId, toolName, toolInput, toolResponse, capturedAt, parsedAt |
| Watermark | Where each pull left off | source, scope, value |
| JobRun | One row per `claude -p` run | id, job, sessionId, startedAt, endedAt, outcome, usage, logPath |
| StyleExample | Mike's recent sent replies, for local drafting | messageId, recipientDomain, text |

Raw captures are kept for 14 days. They let a parser fix be replayed over real data without pulling again, and they become the test fixtures.

## UI tabs and the rules to port

Four tabs, the same as the artifact, plus a status bar that always says whether the app is online, when each source last synced, how many items wait in the outbox, and whether the local model is loaded.

| Tab | Shows | Actions (all work offline) |
| --- | --- | --- |
| Schedule | Week grid of the four calendars; Genome Sciences events shown quietly; clashes flagged on primary events only; each event on its own row; meeting brief under an event when one exists | Accept, decline, tentative; new event; open in Calendar |
| To do | Open list in dragged order, or by date; tag filter; due labels (today, tomorrow, 3d late); stable tag colors | Add, edit, done, drag, recolor tag |
| Email | Owed replies (who is waiting, Claude's one-line why), open loops (Mike promised something), unsent Gmail drafts first; the selected thread is read in full beside the list, from the cache | Reply in app (to all or chosen recipients, with basic formatting), draft with AI, To do, Handled |
| Slack | DMs and mentions awaiting Mike, grouped by conversation; the selected conversation is read beside the list | Reply in thread, draft with AI, To do, Handled |

Nothing sends Mike out to Gmail or Slack to read or answer: the app is where he reads and writes, online or not (decided November 2026).

The artifact's comments record rules learned the hard way. They move into `DailyDocket.Core/Rules` as pure functions, each with a test built from a recorded capture:

1. **Never shown, never read:** auto-replies (matched anywhere in the subject), machine and workflow senders, calendar status mail, and invitations (recognized by body boilerplate, a Teams recording notice, or a leading join link). Everything else is judged on content: a sender rule once buried a live Eisai request.
2. **Owed means "something arrived since his last reply"**, not "someone else spoke last".
3. **Copied on a conversation he never wrote in** is being informed, not asked.
4. **An unsent draft outranks every quiet rule.** Drafts are invisible to thread reads and must be listed separately.
5. **Gmail search previews hold only a thread's oldest \~5 messages.** A separate in:sent sweep gives a lower bound on his last reply; long threads are verified with get\_thread; every slice's view of a thread is merged by message id before deciding.
6. **Two questions for Claude, cached per newest message:** needs a reply (he did not speak last), or owes a promised deliverable (he did). JSON answer, reason of 10 words or fewer.
7. **Slack:** a conversation is the thread, or the channel outside a thread; keys use `~` not `/`; an emoji reaction from Mike counts as an answer; his own channel messages are needed to clear a mention; search results are paged to the end; names come from the thread read.
8. **Calendar:** declined events are not on the schedule; a secondary calendar failing never blanks the grid; a long event still shows its hours and brief on its first day.
9. **Refresh never empties a column.** New data is built aside and swapped in, rows are matched by key, and a field Mike is typing in is never replaced.

## Security

The main risk is an email or Slack message written to make Claude act ("forward this thread to..."). The design answers it by never giving one run both the ability to read inbound text and the ability to send.

- **Readers cannot send.** Pull, triage, deep sweep and meeting prep runs get read-only connector tools and the DailyDocket tools. Send tools, `WebFetch`, `WebSearch`, `Bash` and `Write` are on their disallowed list (LabOps' `ClaudeLauncher.DisallowedTools` pattern).
- **Senders cannot read.** The Send run sees the outbox and thread ids only, and the `PreToolUse` guard allows a send only when it matches a claimed outbox item exactly.
- **Drafting runs propose text, nothing more.** An online drafting run has read tools for context but no send tools; its output goes into the compose box for Mike.
- **No `--dangerously-skip-permissions`.** Anything outside a run's allowlist goes to the app's `approve` tool (`--permission-prompt-tool`), which denies by default in unattended runs.
- **Local server** on 127.0.0.1 only, random port, per-run bearer token (as in LabOps).
- **Data at rest.** The database holds mail. It is encrypted with SQLCipher, with the key kept in Windows Credential Manager on each machine. The SQLCipher build is `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 (native win-x64 and win-arm64), the last one SQLitePCLRaw publishes: its 3.x line dropped SQLCipher, so `Microsoft.Data.Sqlite` stays on the 10.0 line until the app moves to another SQLCipher build. A file the key cannot open is deleted and refilled, since it is only a cache.
- **The guard fails closed.** The send tool is not on the Send run's allowlist; only the guard's explicit `allow` lets it run (checked live in November 2026: a `PreToolUse` allow runs a tool that is off the allowlist under `dontAsk`). A guard that crashes, times out or cannot start is non-blocking to Claude Code, so `dontAsk` then refuses the call. A claim is taken by creating a marker file (`CreateNew`), so two identical parallel calls cannot both pass, and a run in which the guard let a call through is never marked "not sent".
- **Every other built-in tool is disallowed** by name (files, agents, skills, schedules), not only the web and the shell, and so are the Docs, Sheets and Slides connectors, whole (a server name on `--disallowedTools` removes all its tools). `--tools ToolSearch` would be tidier but also removes every MCP tool, the connectors included.
- **No plain-text copies are kept.** Run folders (verbatim captures, claims, the server token) are deleted two hours after they start, once the database has what matters; runs pass `--no-session-persistence`, so Claude Code keeps no transcript of the mail it read (older transcripts of DailyDocket runs are deleted too). `outbox.json` is encrypted with DPAPI.
- **Inbound text has narrow paths to a model.** The capture receipt carries only token-shaped ids and page tokens (a subject that is JSON cannot add sentences or replace the real page token). A Claude draft loses any link or URL that is not in the conversation or guide, invisible characters and long blank runs, so it cannot carry Mike's other messages (in the prompt for his voice) out in a link he would not see.
- **Logs** (Serilog) never record message bodies at the normal level.
- **Offline drafting** runs entirely on the device; nothing leaves the machine until Mike sends.

## Solution layout and build plan

The app follows LabOps' layout and house style (logic in Core, a thin WPF layer, CommunityToolkit.Mvvm, Serilog, xUnit v3 with Shouldly, Velopack releases from GitHub, CalVer, a release note with every user-visible change). Name: DailyDocket.

```
DailyDocket.sln
src/DailyDocket.Core      net10.0               models, rules, outbox, jobs; Claude/ and Sync/ copied from LabOps.Core
src/DailyDocket.Data      net10.0               SQLite with SQLCipher (Microsoft.Data.Sqlite + Dapper), migrations
src/DailyDocket.LocalAI   net10.0-windows10.0.26100.0   Foundry Local behind IAssistant
src/DailyDocket.Hook      net10.0, Native AOT   the hook exe: PostToolUse capture, PreToolUse send guard
src/DailyDocket.App       net10.0-windows10.0.26100.0   WPF shell, tray icon, notifications
src/DailyDocket.Tests     xUnit v3              rules and parsers against recorded captures; outbox state machine
```

Reuse from LabOps: `ClaudeSession`, `StreamJsonParser`, `ClaudeLauncher`, `AppToolServer`, `PermissionMemory`, `ClaudeSignIn`, `GitClient`, `SyncService`, `SettingsStore`, `SingleInstance`, `AppPaths`, and the Velopack `release.yml`. One change from LabOps: it publishes `win-x64` only, so the release adds a `win-arm64` build and a Velopack channel per runtime.

Build order, each phase usable on its own:

1. **Spike (a day or two), on all four machines.** Confirm that `claude -p` sees the Gmail, Calendar and Slack connectors and their exact tool names; that a `PostToolUse` hook receives their raw output and `updatedToolOutput` shrinks it; that a `PreToolUse` hook can deny a send; that hooks load from a per-run `--settings` file; that Foundry Local loads `phi-4-mini` on the NPU (Surface), CUDA (pillar) and Speedy; and whether `claude.exe` runs natively on ARM64.
2. **Read-only mirror.** Pull jobs, capture, parsers, rules, and the four tabs. Offline reading works.
3. **To-dos in git.** The repository, Handled marks, capture to-do from email and Slack, migration of the artifact's current to-dos.
4. **Outbox and sending.** Compose, queue, undo window, Send job, guard hook, Held review, Gmail drafts, RSVPs.
5. **AI help.** Claude drafting online, Foundry Local drafting and provisional triage offline, style examples, polish before send.
6. **Shared jobs and polish.** Nightly sweep and meeting prep on one machine, results in git; tray, notifications, scheduled sync while minimized.

### Spike results

`spike/run-spike.ps1` runs the checks; its summary lands in `spike/out` and its runs and captures under `%LOCALAPPDATA%\DailyDocket\spike`.

**skiddaw (x64, Claude Code 2.1.221), 2026-11-22: all connector checks passed.**

- **Connectors load in `-p` mode** (68 Gmail, Google Calendar, Slack and Meetings tools). They connect after the `init` event is sent, so `init` lists no MCP servers and none of their tools; they are deferred, and a run finds them through `ToolSearch`. Every run's allowlist therefore includes `ToolSearch`. Prefixes: `mcp__claude_ai_Gmail__`, `mcp__claude_ai_Google_Calendar__`, `mcp__claude_ai_Slack__slack_` (the Slack tool names repeat `slack_`, for example `slack_search_public_and_private`).
- **Hooks load from a per-run `--settings` file**, both `PostToolUse` and `PreToolUse`, with a regex matcher (`mcp__claude_ai_.*`).
- **Capture is verbatim**, and the hook input carries `session_id`, `tool_name`, `tool_input`, `tool_response`, `tool_use_id` and `duration_ms`. Raw shapes: Gmail and Calendar return a JSON string (`{resultCountEstimate, threads[]}`, `{id, messages[]}`, `{summary, timeZone, events[], ...}`); Slack search returns a content-block array whose `text` is a JSON envelope, `{results, pagination_info}`, around the same labeled text report the artifact parsed (`### Result n of m`, `Channel: ... (ID: C...)`, `From: ... (ID: U...)`, `Message_ts:`, a permalink carrying `thread_ts`, `Text:`); the cursor is prose in `pagination_info`.
- **Gmail search results carry no dates, labels or snippets** in any view, whatever the tool's description says; only `get_thread` has `internalDate`, `labelIds`, `snippet` and `plaintextBody` (and it returns every message, not five). `THREAD_VIEW_SUMMARY` returns each thread's newest message id, sender and recipients. So the mail pull searches in summary view, the app compares each thread's newest message id with the cache (a docket tool returns the changed ids), and only those threads are read with `get_thread`. That replaces rule 5's preview and in:sent workarounds with an exact read. `list_drafts` returns `{}` when there are none.
- **Slack search context answers "did he reply?".** With `include_context` on, each result's `Context after:` lists the thread's later replies (or a DM's next messages), his own among them, so most conversations settle from the search alone; `slack_read_thread` is needed only when the context runs out. A message's text can be empty (a file with no comment).
- **Receipts must carry Slack's ids too.** Slack's results are text, so the receipt's JSON id walk found none and the run could not follow a search with `slack_read_thread`. The hook also reads `(ID: ...)`, `Message_ts:` and `thread_ts=` from text.
- **`updatedToolOutput` replaces the result.** Claude received only the receipt and the thread and message ids, never the mail.
- **The `PreToolUse` guard denies before the allowlist check.** Claude received "DailyDocket send guard: no outbox item is claimed in this run" for `create_draft`; no draft was made.
- **`claude.exe` is x64 here**, native.
- **The CLI keeps its own login**, separate from the desktop app's: `claude auth login --claudeai` once per machine. Setup should check `claude auth status` and offer sign-in.
- **Run folders must be outside any repository.** Claude Code walks up from its working folder for `CLAUDE.md`; a run under `spike/out` read this repo's and was confused by it. `%LOCALAPPDATA%\DailyDocket\runs` meets this.
- **Launch with the parent's `CLAUDE*` and `ANTHROPIC*` variables removed**, so a DailyDocket started from inside a Claude session does not inherit host auth or session state.

Still to run: pillar, SpeedyGonzales and the Surface (ARM64 `claude.exe`), and Foundry Local with `phi-4-mini` on each.

### Phase 2 as built

The read-only mirror works against the real accounts on skiddaw. A first sync took about three minutes and $1.50 (calendar $0.07, mail $0.13, Slack $0.19, triage $1.13, every item new); later syncs read only what changed. Choices made while building it:

- **Exact calls, not goals.** Each pull's prompt lists the calls and their arguments; Claude only pages (following tokens it sees in receipts) and, for mail, reads the thread ids a docket tool returns. Haiku follows this reliably.
- **Full sweeps every six hours, two-day sweeps in between.** A full sweep's start is a watermark; the Email tab shows threads seen since the last full sweep began, so an archived thread leaves at the next one. Each run reads at most 40 changed threads; the rest wait for the next run.
- **Triage is one Sonnet run per sync**, reading transcripts from the cache through `triage_batch` and writing through `record_verdicts`, in batches of 15, mail first. It has no connector tools.
- **Run isolation:** `--setting-sources local` (the owner's `~/.claude` settings and hooks do not apply), `--permission-mode dontAsk`, inherited `CLAUDE*`/`ANTHROPIC*` variables removed.
- **Not yet:** the tray, notifications, open loops ("you said you would"), and `slack_read_thread` for conversations whose search context runs out.

### Phase 3 as built

The to-dos and Handled marks live in `maccoss/DailyDocket-Data` as designed: `todos/<id>.md`, `handled/email/<threadId>.yaml`, `handled/slack/<channel>~<threadTs>.yaml`, and `config/docket.yaml` (adopted from the data folder on first clone). The artifact's 22 to-dos and 238 Handled marks were imported with `--import-artifact` from its database exported by the ArtifactData tool.

- **A Handled mark holds the newest message it covers** (email: the time of the message he was waiting on; Slack: its ts, kept as the exact text since a double loses digits), and lapses when a newer one arrives, as in the artifact. Old artifact marks keyed by channel alone became `<channel>~main`.
- **Sync is small, not LabOps' `SyncService`:** `DataSync` commits at once, fetches, rebases onto `origin/main` and pushes; offline the commit waits; on a conflict this machine's commits go to `set-aside/<machine>-<time>` and main takes the remote's. A change is pushed four seconds after the last edit, so a burst of edits is one commit.
- **Reorder is Up/Down (and Alt+arrows)**, one file per move; drag and drop is still to come. Saving a task captured from a message marks the message handled (the artifact's rule). Claude's help drafting the task's title and notes (the artifact's capture brief) comes with phase 5.
- **Verdicts and briefs are not in git yet**: verdicts stay in each machine's cache until the shared nightly jobs (phase 6) need them.

### Phases 4 and 5 as built (email and Slack replies; Claude drafting online)

- **Compose** is a WPF rich text box under the reader: recipient chips (email) that can be removed or added, and the toolbar's bold, italic, underline, lists and links. The document is converted to Gmail-style HTML (a `div` per line) with a plain-text alternative, or to standard Markdown for Slack (its send tool takes Markdown). The draft, its own XAML included, is saved to the outbox on every pause.
- **Every send is two runs.** A reading run fetches the one conversation fresh (`get_thread` metadata, or `slack_read_thread`/`slack_read_channel` from the base ts); anything newer than the item's base holds it. Then a sending run, allowed only the one send tool, with every read and every other write disallowed, makes the exact call; the `PreToolUse` guard compares it with the item's claim (keys sorted, exact values) and allows it once. The result's id is taken from the run's capture of the call. A Slack fresh read is text, so any later ts in it holds the item: it errs toward a look.
- **Email replies always pass explicit `to` and `cc`** (the person answered, or everyone else on the newest message for Reply all, never Mike), so what the guard checks is what he saw. `includeSignature` follows the setting (default on).
- **The outbox is schema version 2**, added to an existing cache without dropping it; a database from a newer version is refused rather than dropped. Unsent items are mirrored to `outbox.json`, encrypted with DPAPI for the Windows account (it holds unsent replies and their recipients; DPAPI rather than the database key so it survives losing the database).
- **Draft with Claude** runs Sonnet with no tools at all, the conversation and a few of his recent sent emails in the prompt, and an optional line of guidance ("Say"). Without guidance it must not decide for him: a reply that turns on a decision is written around a bracketed choice. A house-style filter removes em dashes. About $0.08 to $0.29 a draft on skiddaw.
- **Added the night of November 22:** Accept, Maybe and Decline on events from his own calendar (on a shared calendar the attendee "self" is that calendar, so responses are offered only where it is him), behind the undo window, a new choice replacing any earlier unsent one; Save to Gmail drafts from the reply box; Claude drafting the To do capture (the artifact's brief, no tools).
- **Marking threads read in Gmail (decided November 22):** Gmail kept showing mail already handled in the app as unread, so a thread is marked read (`unlabel_thread` UNREAD, through the guarded outbox) when Mike presses Handled, and when a reply of his has actually been sent: never when it is queued, held or undone, so a message that arrived in the meantime is not hidden. A setting (`MarkReadInGmail`) turns it off; it is housekeeping, so a failed one is dropped, not shown.
- **A send that cannot be confirmed is held**, never marked failed: only a guard refusal proves nothing went out.
- **Not yet:** creating or moving events, the offline local model (Foundry Local), and the in:sent check for an interrupted send (it is held for Mike instead).

### Phase 6 as built (shared nightly jobs, meeting prep, tray and notifications)

- **The lease.** `config/docket.yaml`'s `nightly` section names the machine, its fallbacks in order, the nightly time (02:00) and the midday catch-up (12:30). `state/lease.yaml` holds the holder, when it renewed, when it is next due (its next slot), and when the last nightly and catch-up runs finished, on any machine. The designated machine always runs; a fallback runs while it holds the lease, or once the lease is two hours past due times its place in the list (so the second fallback waits four). Due-ness comes from the lease's shared run times, so a fallback's 2 a.m. run is not repeated when the designated machine wakes. The schedule is checked every ten minutes (`NightlyJobs`, injected clock); the repository is synced only when something is due; offline nothing runs; the lease is renewed after every run, but only a run that finished well counts as done, and a failed one is retried after an hour. Machines not named never sync for the lease. "Prep now" ignores the lease.
- **Meeting prep is one Sonnet run per event**, so each brief's cost is visible and write_brief can only save the event its run was given. Tools: Calendar `get_event`, Gmail `search_threads` and `get_thread`, and the Drive connector's `search_files`, `read_file_content` and `get_file_metadata` (`mcp__claude_ai_Google_Drive__`, confirmed by a headless ToolSearch). Connector results are not captured: Claude must read them to summarize, and old threads it reads must not enter the Email tab. The prompt includes Mike's open to-dos and owed replies (titles, subjects and reasons only) for "what he owes". `write_brief` takes typed arguments (attendees with name, org, role and organizer; agenda, context, owes and gaps; sources) and refuses an empty attendee list or agenda with a message the model fixes. Every item is collapsed to one line, at most 240 characters and eight items, with em dashes removed; sources keep only thread ids and https links. Selection (`prep` in docket.yaml): the first two working days from today; declined, cancelled, all-day, out-of-office, focus-time and working-location events, meetings with fewer than two invited people, `prep.skipCalendars`, and secondary calendars' events not accepted are skipped; at most 20 a run. A brief is remade only when its event's start or updated time changes (compared in whole seconds); stale briefs say "may be out of date". New messages in a source thread do not yet make a brief stale. One live prep on skiddaw (a real Monday meeting) cost $0.33 and took 18 seconds.
- **Brief files** are YAML front matter (event id, title, start, the event's updated time, written, writtenOn, model, sources) and Markdown sections (Who, Agenda, Context, You owe, Gaps), one bullet per item; the sections are the stored form, so the file reads well on GitHub and round-trips. An attendee is `- Name (Org): role [organizer]`; parentheses, brackets and colons are removed from names so the line always parses.
- **Verdicts in git.** Every verdict triage records is also merged into `verdicts/email/<threadId>.yaml` or `verdicts/slack/<key>.yaml` (the message id or ts it read, the message's time for mail, the reason capped at 70 characters, when and where), and the repository's verdicts are read into the cache before each sync's triage and after a data sync that changed something. One merge rule both ways: a verdict for an older message never replaces one for a newer message, and the first judgment of a message stands. Verdict files are never pruned yet.
- **The deep sweep** is the mail pull over `in:inbox ... older_than:<window>d newer_than:<deepDays>d` (90), at most 10 pages and three rounds of 40 thread reads, then triage, under the sync's lock. The Email tab and triage include threads the last deep sweep saw whose wait began before the live window (watermark `mail/deep`). Slack has no deep sweep.
- **Tray and notifications.** Minimizing hides the window to a Windows Forms `NotifyIcon` (setting `MinimizeToTray`); the view model keeps syncing. After each sync and each outbox change, `NoticeRules` compares owed email, owed Slack and Held or Failed outbox items with the last look (the first look at start is only a baseline) and shows a balloon, which Windows 10 and 11 present as a notification; clicking it opens that tab and conversation. The icon's menu toggles `Notifications`. Balloons were chosen over toasts because unpackaged toasts need an app identity and a Start-menu shortcut.
- **Not yet:** the offline local-model brief, staleness from new messages in a brief's source threads, pruning old verdict and brief files, and a Slack deep sweep.

## Open decisions

Each has a recommendation, so work can start on the defaults.

- [x] **Where the to-do repository lives.** Decided: `maccoss/DailyDocket-Data`, private (it holds personal tasks and mail-derived briefs, so not in uw-maccosslab). The app repository, `maccoss/DailyDocket`, holds no data, so it can be public, and Velopack then updates every machine from its GitHub Releases without a token. (GitHub Issues were set aside: they need their own offline queue.)
- [ ] **UI framework.** Decided: WPF, to share LabOps' code and conventions. WinUI 3 would only be needed for Phi Silica, which this design does not use.
- [ ] **Encrypt the local database?** Decided: yes, SQLCipher with the key in Credential Manager, since the laptop travels.
- [ ] **Which machine runs the nightly sweep and meeting prep.** Decided: skiddaw, the least-used machine (i9-12900K, 64 GB), with SpeedyGonzales as the first fallback. No GPU is needed, since these runs are Claude working in the cloud; the machine only has to be on and online. A lease file in the repository lets another machine take over when it misses a run. Set in `config/docket.yaml`.
- [ ] **The artifact dashboard.** Decided: retire it later. It keeps running alongside the app until the app covers its daily use; its to-dos move to the repository in phase 3, and from then on the repository is the only to-do list.
- [ ] **Undo window before sending.** Decided: 60 seconds, adjustable in settings.
- [ ] **Surface RAM**, which sets how large an offline model it can hold: 32 GB (to confirm). Enough for phi-4-mini with room to spare, or a 7B-class model if its drafts prove too weak.
- [ ] **App name.** Decided: DailyDocket.
- [ ] **Direct APIs later?** Only if pull runs use too much of the team seat; needs a Google Cloud OAuth client (UW Workspace may require approval) and a Slack app.
