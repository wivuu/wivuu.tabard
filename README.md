# tabard

[![CI](https://github.com/wivuu/wivuu.tabard/actions/workflows/ci.yml/badge.svg)](https://github.com/wivuu/wivuu.tabard/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Wivuu.Tabard.svg)](https://www.nuget.org/packages/Wivuu.Tabard)

A Claude Code profile switcher. Launch `claude` through `tabard` and pick which login to use.

## Install

```sh
dotnet tool install -g Wivuu.Tabard
```

The package id is `Wivuu.Tabard`; the command it installs is plain `tabard`. It lands in
`~/.dotnet/tools`, which needs to be on your PATH, and needs the .NET 10 runtime (or newer).
No runtime, or building from a checkout? See [other ways to install](#other-ways-to-install).

## Usage

```sh
tabard                       # pick a profile, then launch claude
tabard use work              # launch a specific profile
tabard add personal          # create a profile (Anthropic or OpenRouter)
tabard ls                    # list profiles — never changes anything
```

| Command | What it does |
| --- | --- |
| `tabard [claude args...]` | Pick a profile, then launch |
| `tabard use <name> [-- ...]` | Launch a specific profile |
| `tabard add <name>` | Create a profile, choosing Anthropic or OpenRouter |
| `tabard rm <name>` | Delete a profile |
| `tabard ls` | List profiles |
| `tabard openrouter <cmd>` | Configure a profile's [OpenRouter settings](#openrouter-profiles) |
| `tabard -- <claude args>` | Force everything through to claude |

`tabard --help` is tabard's help; `tabard -- --help` reaches Claude Code's.

**First run** adopts your existing login, so there is nothing to set up: `~/.claude` is moved to
`~/.tabard/profiles/default` and linked back. With one profile there is no picker — `tabard` goes
straight to `claude`. With more than one you get an arrow-key picker:

```
  Select a Claude profile

  > work              erik@example.com  -  max  -  valid 7h
    personal          erik@gmail.com    -  pro  -  refresh due

  up/down move   enter launch   x x delete   esc quit
```

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

First run *moves* `~/.claude` to `~/.tabard/profiles/default` and links `~/.claude` back at it.
Moving rather than copying keeps tokens and expiry intact with no second copy to go stale.
`~/.claude.json` gets the same treatment.

In the picker, `x` arms the highlighted row, a second `x` deletes it. Any other key disarms. If the window is
too short for every profile the list scrolls and the help line says how many are off-screen; if
it is too short for a frame at all (under seven rows), tabard prints the list and asks you to use
`tabard use <name>` rather than draw something you can't read.

After a launch, `~/.claude` is repointed at whichever profile you chose, so a bare `claude`
invocation stays consistent with your last choice. `tabard ls` never adopts or repoints anything —
it is safe to run first just to see what tabard would do.

## OpenRouter profiles

A profile does not have to be an Anthropic login. Claude Code will talk to
[OpenRouter](https://openrouter.ai) given the right environment, and since it reads `settings.json`
out of `CLAUDE_CONFIG_DIR`, that environment can live *inside the profile* — no wrapper, no
exported variables, and a bare `claude` behaves exactly like `tabard use <name>`.

`tabard add <name>` asks which provider you want. Picking OpenRouter runs a short wizard: the API
key (from `$OPENROUTER_API_KEY` if it is set, otherwise typed in with the echo masked), then how to
map Claude Code's model tiers, then a summary to confirm. The key is checked against OpenRouter
before anything is written, so a typo is caught there rather than on your first prompt.

What it writes to `~/.tabard/profiles/<name>/settings.json`:

```json
{
  "env": {
    "ANTHROPIC_BASE_URL": "https://openrouter.ai/api",
    "ANTHROPIC_AUTH_TOKEN": "sk-or-v1-...",
    "ANTHROPIC_API_KEY": "",
    "ANTHROPIC_DEFAULT_OPUS_MODEL": "~anthropic/claude-opus-latest",
    "ANTHROPIC_DEFAULT_SONNET_MODEL": "~anthropic/claude-sonnet-latest",
    "ANTHROPIC_DEFAULT_HAIKU_MODEL": "~anthropic/claude-haiku-latest",
    "ANTHROPIC_DEFAULT_FABLE_MODEL": "~anthropic/claude-fable-latest",
    "CLAUDE_CODE_SUBAGENT_MODEL": "~anthropic/claude-opus-latest"
  }
}
```

The empty `ANTHROPIC_API_KEY` is not an oversight — it stops Claude Code trying its own
authentication first. The five model slots default to OpenRouter's floating
`~vendor/model-latest` aliases so `/model opus|sonnet|haiku|fable` keeps meaning what it means and
nothing pins a version that ages out. `openrouter/auto` is offered too, if you would rather
OpenRouter picked per request.

The merge is non-destructive: every other key in `settings.json`, and every environment entry
tabard didn't set, is written back verbatim.

```
tabard openrouter add <name>    Create an OpenRouter profile and launch it
tabard openrouter set <name>    Change which models the profile uses
tabard openrouter key <name>    Replace the profile's API key
tabard openrouter show <name>   Print the settings, with the key redacted
tabard openrouter models [term] List the models OpenRouter offers
```

Everything the wizard asks can be given up front instead, which is what CI and dotfiles want:

```sh
tabard add cheap --openrouter --model qwen/qwen3-coder < /dev/null   # key from $OPENROUTER_API_KEY
tabard openrouter add work --model auto --opus anthropic/claude-opus-5
tabard openrouter set work --sonnet qwen/qwen3-coder                 # leaves the other tiers alone
pbpaste | tabard openrouter key work --key-stdin
```

`--model` sets every tier at once (`auto` is shorthand for `openrouter/auto`), and the per-tier
flags — `--opus`, `--sonnet`, `--haiku`, `--fable`, `--subagent` — override it. There is
deliberately **no `--key` flag**: a key on the command line ends up in your shell history and in
anyone's `ps` output, so it comes from `$OPENROUTER_API_KEY`, from `--key-stdin`, or from the
masked prompt.

Two things worth knowing:

- A new profile is a fresh config directory, so Claude Code still runs its first-run onboarding
  the first time you launch it. That is Claude Code's, not tabard's.
- The key sits in plaintext in `settings.json`, `chmod 0600` inside the `0700` profile directory —
  the same protection an OAuth token in the same directory gets. If a profile has both a saved
  Claude login and OpenRouter settings, the settings win; tabard says so when it notices, and
  `/logout` inside Claude Code clears the login if the two disagree.

The model list comes from OpenRouter at wizard time, filtered to models that can call tools —
Claude Code cannot work without those — plus the aliases and routers, which declare no parameters
and would otherwise be filtered out of their own defaults. If OpenRouter can't be reached, a small
built-in list is shown instead and any slug can still be set with a flag.

## Other ways to install

The package id has to sit under the reserved `Wivuu.*` prefix; the command doesn't, which is why
`dotnet tool install -g Wivuu.Tabard` gives you a plain `tabard`.

For no .NET runtime dependency at all, grab a native binary for your platform from the
[latest release](https://github.com/wivuu/wivuu.tabard/releases/latest) — `linux-x64`,
`linux-arm64`, `osx-arm64`, `osx-x64` and `win-x64` are published with a `SHA256SUMS.txt` — and
drop it on your PATH.

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

Or build the native binary yourself and drop it on your PATH:

```sh
dotnet publish src/Wivuu.Tabard.CLI -c Release -r osx-arm64   # or linux-x64, win-x64
```

`PublishAot` turns on whenever a RID is given, so this produces a single ~2.4 MB native
executable. It stays off otherwise, which is what keeps `dotnet pack` producing a portable
tool package.

## Notes

- `CLAUDE_CONFIG_DIR` is always passed fully expanded. A `~` left in that value has been written
  literally by some versions, stranding credentials in a `~` folder under the working directory.
- On Windows, real symlinks need Developer Mode or elevation, so tabard falls back to a directory
  junction (`mklink /J`), which doesn't. Files have no junction equivalent, so `~/.claude.json`
  linking may fail there — tabard warns and carries on.
- Profile directories are `chmod 0700` on Unix, and a `settings.json` tabard writes is `0600`.
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

## Contributing

Building, testing, CI and the release process are documented in
[CONTRIBUTING.md](CONTRIBUTING.md).
