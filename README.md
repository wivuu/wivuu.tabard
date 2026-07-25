# tabard

A Claude Code profile switcher. Launch `claude` through `tabard` and pick which login to use.

## How it works

Claude Code reads its whole config root from one directory (`~/.claude` by default), and the
`CLAUDE_CONFIG_DIR` environment variable redirects all of it — settings, `.credentials.json`,
`CLAUDE.md`, project configs, history.

So tabard does **not** copy credentials in and out of `~/.claude`. Each profile is just a directory
under `~/.tabard/profiles/<name>`, and switching means setting `CLAUDE_CONFIG_DIR` and exec'ing
`claude`. Two consequences worth understanding:

- **No sync-back problem.** Claude Code rotates refresh tokens in place. A design that copies
  credentials into `~/.claude` on launch and copies them back on exit will silently kill saved
  profiles the first time a rotation isn't captured. Pointing at the directory avoids the whole class of bug.
- **Concurrent sessions work.** Two terminals, two profiles, no clobbering.

Profiles are discovered by listing directories, so the set of profiles can't drift out of sync
with what's actually on disk. The only state file is `~/.tabard/last`, which records your last
choice so a bare `claude` can follow it; deleting it is harmless.

## Behaviour

**First run** adopts your existing login: `~/.claude` is *moved* to `~/.tabard/profiles/default`
and `~/.claude` is linked back at it. Moving rather than copying keeps tokens and expiry intact
with no second copy to go stale. `~/.claude.json` gets the same treatment.

**One profile** → no picker, straight to `claude`.

**More than one** → arrow-key picker:

```
  Select a Claude profile

  > work              erik@example.com  -  max  -  valid 7h
    personal          erik@gmail.com    -  pro  -  refresh due

  up/down move   enter launch   x x delete   esc quit
```

`x` arms the highlighted row, a second `x` deletes it. Any other key disarms. If the window is
too short for every profile the list scrolls and the help line says how many are off-screen; if
it is too short for a frame at all (under seven rows), tabard prints the list and asks you to use
`tabard use <name>` rather than draw something you can't read.

After a launch, `~/.claude` is repointed at whichever profile you chose, so a bare `claude`
invocation stays consistent with your last choice. `tabard ls` never adopts or repoints anything —
it is safe to run first just to see what tabard would do.

## Commands

```
tabard [claude args...]     Pick a profile, then launch
tabard use <name> [-- ...]  Launch a specific profile
tabard add <name>           Create a profile and log in
tabard rm <name>            Delete a profile
tabard ls                   List profiles
tabard -- <claude args>     Force everything through to claude
```

`tabard --help` is tabard's help; `tabard -- --help` reaches Claude Code's.

## Install

As a global .NET tool, from nuget.org:

```sh
dotnet tool install -g Wivuu.Tabard
```

The package id is `Wivuu.Tabard` but the command it installs is plain `tabard` — the id has to
sit under the reserved `Wivuu.*` prefix, the command doesn't.

From a checkout instead:

```sh
dotnet pack src/Wivuu.Tabard.CLI -c Release
dotnet tool install -g --add-source ./nupkg Wivuu.Tabard
```

`dotnet tool install` takes a package id, not a directory, so the local package feed
(`--add-source ./nupkg`) is what makes installing from source work. Later:

```sh
dotnet tool update -g --add-source ./nupkg Wivuu.Tabard
dotnet tool uninstall -g Wivuu.Tabard
```

This puts `tabard` in `~/.dotnet/tools`, which needs to be on your PATH. The tool package is
portable IL and needs the .NET 10 runtime; `RollForward` is `Major`, so a newer runtime will do.

For no runtime dependency at all, publish the native binary instead and drop it on your PATH:

```sh
dotnet publish src/Wivuu.Tabard.CLI -c Release -r osx-arm64   # or linux-x64, win-x64
```

`PublishAot` turns on whenever a RID is given, so this produces a single ~2.4 MB native
executable. It stays off otherwise, which is what keeps `dotnet pack` producing a portable
tool package.

## Build

```sh
dotnet build
dotnet test
```

The CLI lives in `src/Wivuu.Tabard.CLI` and its tests in `tests/Wivuu.Tabard.CLI.Tests`.

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

## Notes

- `CLAUDE_CONFIG_DIR` is always passed fully expanded. A `~` left in that value has been written
  literally by some versions, stranding credentials in a `~` folder under the working directory.
- On Windows, real symlinks need Developer Mode or elevation, so tabard falls back to a directory
  junction (`mklink /J`), which doesn't. Files have no junction equivalent, so `~/.claude.json`
  linking may fail there — tabard warns and carries on.
- Profile directories are `chmod 0700` on Unix.
- Deleting a profile removes the directory first and drops the links only once that has worked, so
  a delete that fails part-way can't leave you with no `~/.claude` at all. If the profile you
  deleted was the linked one, `~/.claude` is repointed at a survivor.
- `~/.claude` and `~/.claude.json` are only ever replaced when they are links into
  `~/.tabard/profiles`. A real directory, or a link aimed anywhere else, is yours — tabard leaves it
  alone and says so.
- `~/.claude.json` is linked into the chosen profile even before that file exists. The dangling
  link is deliberate: Claude Code creates the file the first time it writes, and it lands inside
  the profile rather than being shared.
- If `~/.claude` holds a `claude migrate-installer` install (a `local/` directory that
  `~/.local/bin/claude` points into) and the profile you're switching to doesn't, tabard leaves the
  link where it is rather than breaking the `claude` command. The session still gets the right
  profile through `CLAUDE_CONFIG_DIR`.

## Status

Builds clean (`dotnet build`, 0 warnings / 0 errors) with `TreatWarningsAsErrors` and nullable
reference types on, and publishes as a native AOT binary.

The [TUnit](https://tunit.dev) suite covers link handling, profile metadata parsing and the
profile store. It drives the real filesystem against a redirected `HOME` rather than mocking it,
so adoption, relinking and deletion are exercised as they actually run.

Installs as a global .NET tool (`tabard`, verified end to end) and publishes as a native AOT
binary from the same project.

## Future
- [ ] Support for OpenRouter API endpoint + api key handling