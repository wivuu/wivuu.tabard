# envy

A Claude Code profile switcher. Launch `claude` through `envy` and pick which login to use.

## How it works

Claude Code reads its whole config root from one directory (`~/.claude` by default), and the
`CLAUDE_CONFIG_DIR` environment variable redirects all of it — settings, `.credentials.json`,
`CLAUDE.md`, project configs, history.

So envy does **not** copy credentials in and out of `~/.claude`. Each profile is just a directory
under `~/.envy/profiles/<name>`, and switching means setting `CLAUDE_CONFIG_DIR` and exec'ing
`claude`. Two consequences worth understanding:

- **No sync-back problem.** Claude Code rotates refresh tokens in place. A design that copies
  credentials into `~/.claude` on launch and copies them back on exit will silently kill saved
  profiles the first time a rotation isn't captured. Pointing at the directory avoids the whole class of bug.
- **Concurrent sessions work.** Two terminals, two profiles, no clobbering.

There is no index file — profiles are discovered by listing directories, so nothing can drift
out of sync with what's actually on disk.

## Behaviour

**First run** adopts your existing login: `~/.claude` is *moved* to `~/.envy/profiles/default`
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

`x` arms the highlighted row, a second `x` deletes it. Any other key disarms.

After a launch, `~/.claude` is repointed at whichever profile you chose, so a bare `claude`
invocation stays consistent with your last choice.

## Commands

```
envy [claude args...]     Pick a profile, then launch
envy use <name> [-- ...]  Launch a specific profile
envy add <name>           Create a profile and log in
envy rm <name>            Delete a profile
envy ls                   List profiles
envy -- <claude args>     Force everything through to claude
```

`envy --help` is envy's help; `envy -- --help` reaches Claude Code's.

## Build

```sh
dotnet build
dotnet publish src/Envy.Cli -c Release -r linux-x64   # or osx-arm64, win-x64
```

`PublishAot` is on, so you get a single native binary with no runtime dependency. Drop it on
your PATH as `envy`.

## Two things to verify on your machines

Both of these are assumptions the design rests on, and both are cheap to test.

**1. macOS Keychain.** On macOS, Claude Code stores credentials in the system Keychain rather
than a `.credentials.json` file — that's only Linux and Windows. So `CLAUDE_CONFIG_DIR` may not
isolate *logins* on macOS even though it isolates everything else. Test:

```sh
envy add test      # log in with a second account
envy ls            # does 'test' show a token, or "no token file"?
envy use default   # is the original account still logged in?
```

If macOS turns out to share one Keychain entry across profiles, the fallback is to save and
restore the Keychain item per profile via the `security` CLI on switch — which reintroduces the
sync-back problem, so it needs a write-back on exit. Worth knowing before you rely on it.

**2. `~/.claude.json` location.** This file sits beside the config dir rather than inside it in
at least some versions. envy links `~/.claude.json` at the default profile's copy, which is
correct if Claude Code honours `CLAUDE_CONFIG_DIR` for it — and wrong if it doesn't, in which
case every profile shares the default's file. Test by creating a second profile and checking
whether `~/.envy/profiles/<name>/.claude.json` appears after login.

## Notes

- `CLAUDE_CONFIG_DIR` is always passed fully expanded. A `~` left in that value has been written
  literally by some versions, stranding credentials in a `~` folder under the working directory.
- On Windows, real symlinks need Developer Mode or elevation, so envy falls back to a directory
  junction (`mklink /J`), which doesn't. Files have no junction equivalent, so `~/.claude.json`
  linking may fail there — envy warns and carries on.
- Profile directories are `chmod 0700` on Unix.
- Deleting a profile removes any link pointing at it first, so nothing is deleted *through* a link.

## Status

Written but not compiled — there was no SDK available where this was drafted. Expect to fix a
few things on first `dotnet build`.
