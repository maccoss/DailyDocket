# DailyDocket: guide for AI-assisted development

A Windows desktop app (.NET 10, WPF) that mirrors Mike's dashboard (Schedule, To do, Email needing a
reply, Slack needing a reply), keeps working offline, and sends what was written offline once a
connection returns. The design is `docs/design.md`; keep it current when a decision changes.

Write American English and do not use em dashes, in code, comments, docs, commit messages and UI
text alike.

## The rules that shape everything

- **Gmail, Calendar and Slack are the sources of truth.** The SQLite database is a cache plus the
  outbox; it can be deleted and rebuilt by a full pull. Only the outbox and unsent drafts are
  irreplaceable (also exported to `outbox.json` on every change).
- **Claude Code headless (`claude -p`) is the only path to the connectors.** No Google Cloud or
  Slack app. Each sync is one run with Mike's own Claude login.
- **The model decides what to fetch; it never retypes what it fetched.** A `PostToolUse` hook
  (`DailyDocket.Hook capture`) saves each connector result verbatim; C# parsers turn captures into
  rows.
- **Nothing sends without Mike pressing Send.** Every write goes to the outbox first.
- **Readers cannot send; senders cannot read.** Pull, triage, sweep and prep runs get read-only
  tools. The Send run sees outbox items and thread ids only, and the `PreToolUse` guard
  (`DailyDocket.Hook guard`) allows a send only when it matches a claimed outbox item exactly, once.
  If the guard fails it exits 2, which blocks the call.
- **Claude runs in an empty per-run folder** under `%LOCALAPPDATA%\DailyDocket\runs`, so no project
  CLAUDE.md, settings or hooks are picked up; its hooks come only from the run's `--settings` file.

## Layout

```
Directory.Build.props        single <Version> (CalVer YY.feature.patch), warnings as errors
global.json                  .NET 10 SDK; opts dotnet test into Microsoft Testing Platform
docs/design.md               the design
spike/                       phase 1 checks (run-spike.ps1); spike/out is git-ignored (holds real mail)
release-notes/               one file per version; becomes the GitHub Release body
src/DailyDocket.Core/        all logic, no UI types                          net10.0
  Claude/                    copied from LabOps.Core: ClaudeSession, StreamJsonParser, events, sign-in
  Infrastructure/            AppInfo, AppPaths, SettingsStore, SingleInstance (from LabOps)
  Processes/                 ProcessRunner, ToolLocator (from LabOps)
  Sync/                      GitClient (from LabOps); SyncService comes with the to-do repository
src/DailyDocket.Hook/        the hook exe, no Core reference, Native AOT     net10.0
src/DailyDocket.Tests/       xUnit v3 + Shouldly                             net10.0
```

Still to come, in build order: `DailyDocket.Data` (SQLite with SQLCipher), `DailyDocket.App` (WPF
shell), `DailyDocket.LocalAI` (Foundry Local behind `IAssistant`).

Code copied from LabOps (`uw-maccosslab/LabOps`, `src/LabOps.Core`) keeps its comments; when a
fix lands in one copy, consider whether the other needs it.

## Building, testing, running

```bash
dotnet build DailyDocket.slnx -c Debug
dotnet test --project src/DailyDocket.Tests/DailyDocket.Tests.csproj
```

- `DAILYDOCKET_DATA=<folder>` runs with settings, database and logs in that folder instead of
  `%LOCALAPPDATA%\DailyDocket`.
- Publishing the hook ahead of time needs Visual Studio's C++ build tools.
- Both `win-x64` and `win-arm64` are published; nothing may depend on x64 emulation.

## House style (from LabOps and PanoramaBridge)

- No emojis anywhere.
- Comments explain why, especially why an obvious alternative was rejected.
- Errors are written for the user: what happened and what to do.
- Put logic in Core; keep the WPF layer thin.
- Add the release note in the same commit as any user-visible change.
- Logs never record message bodies at the normal level.
