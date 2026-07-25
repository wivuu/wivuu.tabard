# Contributing

## Build

```sh
dotnet build
dotnet test
```

The CLI lives in `src/Wivuu.Tabard.CLI` and its tests in `tests/Wivuu.Tabard.CLI.Tests`.

Builds are expected to be clean — 0 warnings / 0 errors with `TreatWarningsAsErrors` and nullable
reference types on.

The [TUnit](https://tunit.dev) suite covers link handling, profile metadata parsing, the profile
store, the `settings.json` merge, the OpenRouter catalog parsing and the command-line flags. It
drives the real filesystem against a redirected `HOME` rather than mocking it, so adoption,
relinking and deletion are exercised as they actually run.

## CI / releases

`.github/workflows/ci.yml` runs on every push and pull request: the TUnit suite on Linux, macOS
and Windows (symlink handling is the part most likely to differ per platform, so all three run),
a `csharpier check`, and a `dotnet pack` to prove the tool package still builds.

`.github/workflows/release.yml` fires on a `v*` tag, or by hand with a version:

```sh
git tag v0.2.0 && git push origin v0.2.0
```

The tag supplies the version — `v0.2.0` builds `0.2.0`, overriding `<Version>` in the csproj, so
that property is only the fallback for local builds. Tests gate the release; after they pass it
packs the nupkg, publishes native AOT binaries for `linux-x64`, `linux-arm64`, `osx-arm64`,
`osx-x64` and `win-x64`, and attaches them with a `SHA256SUMS.txt` to a generated GitHub release.
A version containing a hyphen (`v0.2.0-rc.1`) is marked as a prerelease.

Each RID builds on a runner that can link it natively rather than cross-compiling; `linux-arm64`
uses the `ubuntu-24.04-arm` runner, which needs a public repo or a paid plan.

Push to nuget.org is the last step and uses [trusted publishing][tp] — no API key is stored
anywhere. The job mints a GitHub OIDC token, `NuGet/login` trades it for an API key nuget.org
honours for one hour, and `dotnet nuget push` spends it immediately. Setup is two things:

- A trusted publishing policy on nuget.org (your username → **Trusted Publishing**) owned by the
  Wivuu organization, with repository owner `wivuu`, repository `wivuu.tabard`, workflow file
  `release.yml`, and Environment left empty.
- A `NUGET_USER` repository *variable* (not a secret) holding the nuget.org profile name of
  whoever created the policy — the profile name, not an email address.

Because the policy is keyed on the workflow file name, renaming `release.yml` breaks publishing
until the policy is updated to match.

[tp]: https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing

## Homebrew

The tap serves the native AOT binaries the release job already publishes, so it builds nothing of
its own. This repo is its own tap — `Formula/tabard.rb` at the root is all a tap needs, so there is
no second repository to keep in step:

```sh
brew tap wivuu/tabard https://github.com/wivuu/wivuu.tabard
brew install tabard
```

The `packaging` job in `release.yml` rewrites the version and the four `sha256` lines on every
non-prerelease tag and commits the result to `master`. Each `sha256` line carries a trailing RID
comment (`# osx-arm64`) that the job keys off, and it reads every hash back after writing — a hash
that failed to update would serve the previous release under the new version number. Prereleases
are skipped, so `v0.2.0-rc.1` never reaches brew users.

To check the formula before tagging:

```sh
brew style Formula/tabard.rb
```

## winget

`manifests/w/Wivuu/Tabard/<version>/` holds the three manifests, laid out the way
`microsoft/winget-pkgs` wants them. The package is a zip with a nested portable exe, which is why
the installer manifest declares `NestedInstallerType: portable` and a `PortableCommandAlias` of
`tabard`. Like the formula, it points at binaries the release already published rather than
building anything.

The first submission has to be done by hand, because `wingetcreate update` edits a manifest that is
already published:

```sh
wingetcreate submit --token <pat> manifests/w/Wivuu/Tabard/0.1.1
```

Once that PR merges into `winget-pkgs`, set up the automation:

- A `WINGET_TOKEN` repository *secret* — a classic PAT with `public_repo`, on an account that has
  forked `microsoft/winget-pkgs`. It opens the PR.
- A `WINGET_PUBLISH` repository *variable* set to `true`. The `winget` job is gated on it, so
  releases don't fail on submissions that can't succeed yet.

The `packaging` job keeps the in-repo manifests current on each release regardless; the `winget`
job is what actually opens the PR.

## Two things to verify on your machines

Both of these are assumptions the design rests on, and both are cheap to test.

**1. macOS Keychain.** On macOS, Claude Code stores credentials in the system Keychain rather
than a `.credentials.json` file — that's only Linux and Windows. So `CLAUDE_CONFIG_DIR` may not
isolate *logins* on macOS even though it isolates everything else. Test:

```sh
tabard add test      # log in with a second account
tabard ls            # does 'test' show a token, or "no token file"?
tabard use default   # is the original account still logged in?
```

If macOS turns out to share one Keychain entry across profiles, the fallback is to save and
restore the Keychain item per profile via the `security` CLI on switch — which reintroduces the
sync-back problem, so it needs a write-back on exit. Worth knowing before you rely on it.

**2. `~/.claude.json` location.** This file sits beside the config dir rather than inside it in
at least some versions. tabard links `~/.claude.json` at the default profile's copy, which is
correct if Claude Code honours `CLAUDE_CONFIG_DIR` for it — and wrong if it doesn't, in which
case every profile shares the default's file. Test by creating a second profile and checking
whether `~/.tabard/profiles/<name>/.claude.json` appears after login.
