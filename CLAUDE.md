# CLAUDE.md

Build, test, CI and packaging details live in [CONTRIBUTING.md](CONTRIBUTING.md). This file covers
versioning, which has rules that are easy to get wrong.

## The version lives in the tag

`release.yml` takes the version from the pushed tag — `v0.2.0` builds `0.2.0` — and injects it with
`-p:Version=` into every `pack` and `publish`. Nothing else is consulted at release time.

`<Version>` in `src/Wivuu.Tabard.CLI/Wivuu.Tabard.CLI.csproj` is only the fallback for local builds,
but keep it in step: bump it in its own `chore: release vX.Y.Z` PR, merge that, *then* tag. Tagging
a commit whose csproj still says the old version ships fine but leaves local `dotnet pack` output
disagreeing with what was published.

Neither `global.json` (SDK pin) nor `Directory.Packages.props` (TUnit) is the product version.

## Cutting a release

```sh
# 1. bump <Version> in the csproj, PR it, merge it
# 2. tag the merge commit
git tag v0.2.0 && git push origin v0.2.0
# 3. CI opens a `formula/vX.Y.Z` PR that auto-merges once its checks pass
#    - pull once it lands before doing more work
git pull
```

SemVer, and still pre-1.0: minor for features and breaking changes, patch for fixes.

A tag containing a hyphen (`v0.2.0-rc.1`) is marked a prerelease and skips the packaging jobs, so
Homebrew and winget users are only ever offered a stable version.

## Do not hand-edit the packaging files

The `packaging` job owns the version inside `Formula/tabard.rb` and inside
`manifests/w/Wivuu/Tabard/`. After each stable release it rewrites the version, URLs and checksums
from the release's own `SHA256SUMS.txt` and opens a `formula/vX.Y.Z` PR that merges itself once CI
is green.

The bump cannot be pushed straight to master: the `prs` ruleset requires a pull request, and its
bypass list cannot name the Actions bot (GitHub only accepts roles, teams, deploy keys and
installed Apps there). The same ruleset is why the job dispatches `ci.yml` on the branch by hand —
a `GITHUB_TOKEN` push starts no workflow run, so the required checks would otherwise never report.

Editing those by hand is at best redundant and at worst wrong: the checksums have to match binaries
that do not exist until the release job has built them. If a version is wrong there, fix the job,
not the file.

## Versions are immutable once published

nuget.org will not accept a re-push of an existing version, and the release job runs with
`--skip-duplicate`, so a re-run of a published version silently no-ops. Never move a tag or re-cut
a released version to fix something — bump to the next patch and release that.
