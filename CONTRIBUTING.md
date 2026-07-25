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
