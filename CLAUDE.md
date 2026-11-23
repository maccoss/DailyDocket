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
- **Claude runs in an empty per-run folder** under `%LOCALAPPDATA%\DailyDocket\runs`, outside any
  repository (Claude Code walks up for CLAUDE.md), with `--setting-sources local` so the owner's own
  settings and hooks do not apply, no inherited `CLAUDE*`/`ANTHROPIC*` variables, and
  `--permission-mode dontAsk`. Its hooks come only from the run's `--settings` file.
- **This repository is public.** Nothing personal goes in it: the owner's addresses, Slack id,
  calendar ids and machine senders live in `docket.yaml` (`docs/docket.example.yaml` shows the
  format). Test fixtures are synthetic, in the real shapes; real captures are read in place by the
  opt-in tests and never copied here.

## Layout

```
Directory.Build.props        single <Version> (CalVer YY.feature.patch), warnings as errors
global.json                  .NET 10 SDK; opts dotnet test into Microsoft Testing Platform
docs/design.md               the design; docs/docket.example.yaml, the personal config's format
spike/                       phase 1 checks (run-spike.ps1); spike/out is git-ignored (holds real mail)
release-notes/               one file per version; becomes the GitHub Release body
src/DailyDocket.Core/        all logic, no UI types                          net10.0
  Calendar/ Mail/ Slack/     parsers for each connector's results, and the artifact's rules
  Capture/                   CapturedCall: one hook capture
  Claude/                    from LabOps.Core: ClaudeSession, StreamJsonParser, AppTools, AppToolServer
  Configuration/             DocketConfig (docket.yaml)
  Jobs/                      pull and triage plans, JobRunner, SyncScheduler, the connector tool lists
  Infrastructure/            AppInfo, AppPaths, SettingsStore, SingleInstance (from LabOps)
  Processes/                 ProcessRunner, ToolLocator (from LabOps)
  Setup/                     SetupChecks: Claude Code installed and signed in, Git installed
  Updates/                   UpdatePolicy: when to check, what the update bar says
  Sync/                      GitClient (from LabOps); SyncService comes with the to-do repository
src/DailyDocket.Data/        SQLite with SQLCipher: schema, capture ingestion, the tabs' queries
src/DailyDocket.Hook/        the hook exe, no Core reference, Native AOT     net10.0
src/DailyDocket.App/         WPF shell, thin                                 net10.0-windows
src/DailyDocket.Tests/       xUnit v3 + Shouldly                             net10.0-windows
```

Still to come: the to-do repository (phase 3), the outbox and Send (phase 4), and
`DailyDocket.LocalAI` (Foundry Local behind `IAssistant`, phase 5).

Code copied from LabOps (`uw-maccosslab/LabOps`, `src/LabOps.Core`) keeps its comments; when a
fix lands in one copy, consider whether the other needs it.

## Building, testing, running

```bash
dotnet build DailyDocket.slnx -c Debug
dotnet test --project src/DailyDocket.Tests/DailyDocket.Tests.csproj
```

- `DAILYDOCKET_DATA=<folder>` runs with settings, database and logs in that folder instead of
  `%LOCALAPPDATA%\DailyDocket`. Put a `docket.yaml` there.
- Opt-in tests: `DAILYDOCKET_CAPTURES=<folder>` parses real captures in place;
  `DAILYDOCKET_LIVE=<folder with docket.yaml>` runs the real pull and triage jobs (read-only, costs a
  little Claude usage) into a temporary folder; `DAILYDOCKET_INSPECT=<that folder>` prints what the
  tabs would show.
- Publishing the hook ahead of time needs Visual Studio's C++ build tools.
- Both `win-x64` and `win-arm64` are published; nothing may depend on x64 emulation.

CI (`.github/workflows/ci.yml`) builds, tests and publishes the AOT hook on an x64 and an ARM64
Windows runner for every pull request and push to `main`. Locally, an AOT publish needs
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH (for `vswhere.exe`).

## Versioning and releases

LabOps' convention: CalVer `YY.feature.patch` in `Directory.Build.props`, bumped only at release
time; the tag `v{version}` must match. The draft notes live in `release-notes/RELEASE_NOTES_next.md`;
the full process is in `release-notes/README.md`.

Pushing a `v*` tag runs `.github/workflows/release.yml`: it checks the tag against `<Version>` and
that `release-notes/RELEASE_NOTES_v{version}.md` exists, then on an x64 and an ARM64 runner tests,
publishes the app self-contained, publishes the hook with Native AOT and copies
`DailyDocket.Hook.exe` beside `DailyDocket.exe` (where `HookLocator` looks), and packs with vpk
(package `MacCoss.DailyDocket`, one channel per runtime: `win-x64`, `win-arm64`, and `-beta` of
each for a tag containing `beta`). A final job uploads both runtimes into one GitHub Release
(`--merge`) with `SHA256SUMS.txt`. Signing happens only when the `SIGNING_CERT_BASE64` and
`SIGNING_CERT_PASSWORD` secrets exist. The installers are `MacCoss.DailyDocket-win-x64-Setup.exe`
and `MacCoss.DailyDocket-win-arm64-Setup.exe`.

Updates: `VelopackApp.Build().Run()` is the first line of `Program.Main`. `UpdateService` (App)
checks at startup and every four hours on `AppInfo.UpdateChannel(settings.BetaUpdates)`, with no
token (the repository is public), downloads in the background, and shows a bar with **Restart**;
it never restarts by itself, and a failed check is only logged. The rules that need no Velopack
live in `Core/Updates/UpdatePolicy`. A development build is not installed, so it never checks.

To try packaging locally (nothing is uploaded):

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish src/DailyDocket.App -c Release -r win-x64 --self-contained -o publish/app
dotnet publish src/DailyDocket.Hook -c Release -r win-x64 -o publish/hook
Copy-Item publish/hook/DailyDocket.Hook.exe publish/app/
dotnet tool install vpk --version 1.2.161 --tool-path publish/vpk
publish/vpk/vpk pack --packId MacCoss.DailyDocket --packVersion 26.0.0 --packDir publish/app `
  --mainExe DailyDocket.exe --icon src/DailyDocket.App/Assets/app.ico --channel win-x64 `
  --runtime win-x64 --outputDir publish/releases
```

`publish/` is git-ignored.

## House style (from LabOps and PanoramaBridge)

- No emojis anywhere.
- Comments explain why, especially why an obvious alternative was rejected.
- Errors are written for the user: what happened and what to do.
- Put logic in Core; keep the WPF layer thin.
- Add the release note in the same commit as any user-visible change.
- Logs never record message bodies at the normal level.
