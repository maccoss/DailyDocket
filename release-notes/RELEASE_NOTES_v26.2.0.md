# DailyDocket v26.2.0

Setup walks a new computer through Claude Code, Git, GitHub and the data repository, and anyone can point DailyDocket at their own data repository.

## New Features

- **Setup.** A **Setup** button beside **Refresh** opens a checklist of what DailyDocket needs on
  this computer, with a button for each step, so a new computer needs no terminal: **Install Claude
  Code** (Anthropic's installer), **Sign in** to Claude (in the browser, with no console window),
  **Install Git** (winget), the GitHub sign-in that git uses for the data repository (installing
  the GitHub command-line tool if needed, or **Connect** when git is not using it), **Download**
  for the data repository, and **Use my GitHub name** when git has no name to save changes under.
  Setup opens by itself at startup when something is missing for certain; a step that could not be
  checked, such as GitHub while offline, does not open it (`"OpenSetupAtStartup"` turns this off).
  The banner names what is missing and points to Setup instead of listing commands
  (`src/DailyDocket.Core/Setup/SetupChecks.cs`, `src/DailyDocket.App/Views/SetupWindow.xaml`).
- **Your own data repository.** Setup's **Which data repository** box takes another private GitHub
  repository, as owner/name or its address, so someone else can keep their own docket; an empty
  repository works. The copy of the current one on this computer is moved aside, never deleted,
  and only when everything in it is already on GitHub.

## Bug Fixes

- **The taskbar kept the old icon after 26.1.4.** Windows caches an app's icon by its path, and an
  update replaces the program under the same path, so the taskbar showed the old icon while the
  window showed the new one. DailyDocket now asks Windows to read icons again on the first start
  after each update.
- **A first download cut off partway could break syncing for good.** Closing DailyDocket while it
  was downloading the data repository left a half-made copy that it then treated as complete. The
  download now goes to a separate folder that takes its place only once it has finished.
