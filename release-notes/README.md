# Release Notes

This directory contains per-version release notes for DailyDocket. The convention is LabOps'
(`uw-maccosslab/LabOps`, `release-notes/README.md`); this file is the canonical description of it for
this repository.

## Versioning Scheme

DailyDocket uses a `YY.feature.patch` versioning convention:

- **YY**: Two-digit year (e.g., `26` for 2026)
- **feature**: Incremented for each release containing new features, restarting each year
- **patch**: Incremented for bug-fix-only releases within the same feature version

Examples: `26.1.0` (first feature release of 2026), `26.1.1` (patch), `26.2.0` (second feature release).

The version lives in exactly one place, `<Version>` in `Directory.Build.props`, and is bumped only
at release time, not during development. `release.yml` refuses to publish when the tag disagrees
with it. The first release was `26.1.0` (November 2026).

## File Format

Each release gets one file: `RELEASE_NOTES_v{version}.md`. During development, the unreleased draft
lives in `RELEASE_NOTES_next.md` and gets renamed at release time.

```text
release-notes/
  README.md                  # this file
  RELEASE_NOTES_next.md      # working draft for the next release
  RELEASE_NOTES_v26.1.0.md
  RELEASE_NOTES_v26.1.1.md
```

## Writing Release Notes

### During Development

Maintain `RELEASE_NOTES_next.md` as a working draft for the next planned version. Append entries
**in the same commit as the change**, not at release time. **User-visible** means behavior, a
message someone reads, a button or setting, or performance they would notice. Refactors, tests and
documentation get no entry; say so in the commit message instead, so the omission reads as a
decision rather than an oversight. The file stays unversioned until the release is finalized, so the
target version can still change.

### Content Structure

```markdown
# DailyDocket v{version}

One-sentence summary of the release.

## New Features

- Grouped by area (Schedule, To do, Email, Slack, Outbox, Offline, Setup, Updates)
- Focus on what changed from the user's perspective, not implementation details

## Bug Fixes

- The bug, its impact, and what was fixed

## Performance

- Improvements with context ("a mail pull finishes in 20 s instead of 60 s")

## Breaking Changes

- Anything requiring user action. Omit the section when there is nothing.
```

Sections can be omitted if empty. For patch releases, a flat list is sufficient.

> [!IMPORTANT]
> **Delete the empty headings when you rename the draft.** The rolling draft is seeded with all
> four headings, so a renamed draft always arrives carrying the ones nobody filled in. The file is
> published verbatim as the GitHub Release description.

### Style

- Write in past tense ("Added", "Fixed", "Removed")
- Lead with user impact, not implementation details
- Include specific numbers where relevant (sizes, times, counts)
- Name buttons and settings exactly as the user sees them (**Send**, `"BetaUpdates"`)
- Reference modified files with paths so reviewers can locate the change
- American English, no em dashes, no emojis

## Release Process

1. Finalize `RELEASE_NOTES_next.md`; `git mv` it to `RELEASE_NOTES_v{version}.md`, update its
   heading, **delete every section heading with no entries under it**, and create a fresh
   `RELEASE_NOTES_next.md` seeded with the four headings
2. Bump `<Version>` in `Directory.Build.props` to `{version}`
3. Commit and push to `main`, and let CI go green (both the x64 and the ARM64 job)
4. Tag: `git tag -a v{version} -m "DailyDocket {version}"`
5. Push the tag: `git push origin v{version}`. **Pushing the tag both builds the installers and
   creates the GitHub Release** (`.github/workflows/release.yml`). Do not
   hand-create the Release.

A tag containing `alpha`, `beta`, or `rc` is published as a GitHub prerelease. A tag containing
`beta` also goes to the beta update channels, for trying a release on one computer before the others
get it (set `"BetaUpdates": true` in `%LOCALAPPDATA%\DailyDocket\settings.json` there).

## Update Channels

DailyDocket ships native builds for two runtimes, so unlike LabOps it has a Velopack channel per
runtime: `win-x64` and `win-arm64`, and `win-x64-beta` and `win-arm64-beta`. An installed copy reads
only its own runtime's channel (`AppInfo.RuntimeIdentifier`), so the Surface never takes an x64 build
that would run under emulation. The repository is public, so the update check needs no token.
