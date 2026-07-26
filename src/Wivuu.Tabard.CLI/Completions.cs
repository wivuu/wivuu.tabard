using System.Diagnostics;

namespace Wivuu.Tabard.Cli;

/// <summary>What 'tabard completion install' wrote and what it had to change to load it.</summary>
internal sealed record InstallResult(
    string Shell,
    string ScriptFile,
    string StartupFile,
    string LoadLine,
    bool AlreadyLoaded,
    string? Warning = null
);

/// <summary>
/// Tab completion. The installed shell script knows one thing - how to ask 'here are the words on
/// the line and where the cursor is, what goes here?' - and every answer comes back from
/// <see cref="Suggest"/>. So the script never goes stale: profile names are read at the moment the
/// key is pressed, and a command added here is completable without anyone re-sourcing anything.
/// </summary>
internal static class Completions
{
    /// <summary>The shells 'tabard completion &lt;shell&gt;' can print a script for.</summary>
    public static readonly string[] Shells = ["bash", "zsh", "pwsh"];

    /// <summary>
    /// Exactly what 'tabard --help' documents. The aliases ('new', 'list', 'remove', 'or') still
    /// work when they are typed; completing both halves of every pair would make the list twice as
    /// long to teach nobody anything.
    /// </summary>
    private static readonly string[] Commands =
    [
        "use",
        "add",
        "rm",
        "ls",
        "openrouter",
        "completion",
        "help",
        "--help",
        "--",
    ];

    private static readonly string[] CompletionCommands = ["install", .. Shells];

    private static readonly string[] OpenRouterCommands =
    [
        "add",
        "set",
        "key",
        "show",
        "models",
        "help",
    ];

    /// <summary>The flags whose next word is a model slug, so they are also what triggers
    /// <see cref="ModelSlugs"/>.</summary>
    private static readonly string[] ModelFlags =
    [
        "--model",
        .. OpenRouter.Slots.Select(slot => slot.Flag),
    ];

    private static readonly string[] SetupFlags = [.. ModelFlags, "--key-stdin"];

    private static readonly string[] AddFlags = ["--openrouter", .. SetupFlags];

    /// <summary>
    /// Offered after a model flag. The built-in list rather than the live catalog on purpose: a
    /// completer that reaches the network makes every tab press wait on OpenRouter, and
    /// 'tabard openrouter models' is already the command for the full one.
    /// </summary>
    private static readonly string[] ModelSlugs =
    [
        "auto",
        "auto-beta",
        .. OpenRouter.Fallback.Select(model => model.Id),
    ];

    /// <summary>
    /// What can go at <paramref name="cword"/>, given every word on the line. The words arrive as
    /// the shell split them, so <c>words[0]</c> is how tabard itself was typed.
    /// </summary>
    public static IEnumerable<string> Suggest(IReadOnlyList<string> words, int cword)
    {
        // Nothing to offer at or before the command name itself.
        if (cword < 1)
            return [];

        // A cursor past the last word - the usual 'tabard use <space><tab>' - completes nothing yet,
        // so every candidate for the position qualifies.
        var prefix = cword < words.Count ? words[cword] : "";

        // Whatever is to the right of the cursor is not context: the user is editing here, not there.
        var before = words.Take(Math.Min(cword, words.Count)).Skip(1).ToList();

        return Candidates(before)
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal))
            // A profile name can hold a line break on Unix, and one candidate per line has no way to
            // say that. Dropping it beats handing the shell two halves naming nothing.
            .Where(candidate => candidate.AsSpan().IndexOfAny('\r', '\n') < 0);
    }

    private static IEnumerable<string> Candidates(IReadOnlyList<string> before)
    {
        if (before.Count == 0)
            return Commands;

        // The strongest signal on the line, whichever command it belongs to: the word after a model
        // flag is that flag's value.
        if (ModelFlags.Contains(before[^1], StringComparer.Ordinal))
            return ModelSlugs;

        return before[0] switch
        {
            "use" => before.Count switch
            {
                1 => Profiles(),
                // 'tabard use <name> -- ...' is the documented way through to claude's own flags,
                // and past that the words belong to claude rather than to tabard.
                2 => ["--"],
                _ => [],
            },
            "rm" or "remove" => before.Count == 1 ? Profiles() : [],
            // Not Profiles(): the name being typed here is one that does not exist yet.
            "add" or "new" => AddFlags,
            "openrouter" or "or" => OpenRouterCandidates(before),
            "completion" or "completions" => before.Count switch
            {
                1 => CompletionCommands,
                2 when before[1] is "install" => Shells,
                _ => [],
            },
            _ => [],
        };
    }

    private static IEnumerable<string> OpenRouterCandidates(IReadOnlyList<string> before)
    {
        if (before.Count == 1)
            return OpenRouterCommands;

        return before[1] switch
        {
            // 'set', 'key' and 'show' work on a profile that exists; 'add' names one that does not.
            "set" or "key" or "show" when before.Count == 2 => Profiles(),
            "set" => ModelFlags,
            "key" => ["--key-stdin"],
            "add" => SetupFlags,
            // 'show' takes only a name, and 'models' takes free text to search the catalog with.
            _ => [],
        };
    }

    /// <summary>
    /// Adopts nothing and repoints nothing - a tab press is not a decision.
    /// <see cref="ProfileStore.List"/> only reads directory names, so this stays cheap enough to
    /// run on every keystroke.
    /// </summary>
    private static IEnumerable<string> Profiles()
    {
        try
        {
            return ProfileStore.List().Select(profile => profile.Name);
        }
        catch
        {
            // A completer that reports a problem scribbles it across the line being typed.
            return [];
        }
    }

    public static string Script(string shell) =>
        shell switch
        {
            "bash" => Bash,
            "zsh" => Zsh,
            "pwsh" or "powershell" => Pwsh,
            _ => throw new ArgumentException(
                $"no completion script for '{shell}'. Try one of: {string.Join(", ", Shells)}."
            ),
        };

    /// <summary>Where a shell's script goes, and the line that loads it.</summary>
    private sealed record Target(string ScriptFile, string StartupFile, string LoadLine);

    /// <summary>
    /// Writes the script under ~/.tabard and adds one line to the shell's startup file to load it.
    /// The startup file is only ever appended to - it is the user's, and tabard rewriting it would
    /// be a way to lose things. The line it gains checks the script is still there first, so a
    /// tabard that has been uninstalled leaves a shell that starts quietly rather than one that
    /// complains at every prompt.
    /// </summary>
    public static InstallResult Install(string? shell)
    {
        var resolved = Normalize(shell ?? Detect());
        var script = Script(resolved);
        var target = TargetFor(resolved);

        Directory.CreateDirectory(Paths.CompletionsRoot);
        File.WriteAllText(target.ScriptFile, script + Environment.NewLine);

        if (Loads(target))
            return new InstallResult(
                resolved,
                target.ScriptFile,
                target.StartupFile,
                target.LoadLine,
                AlreadyLoaded: true
            );

        string? warning = null;
        try
        {
            Append(target);
        }
        catch (Exception ex)
        {
            // The script is written and works the moment it is loaded, so this is worth reporting
            // and finishing rather than failing outright.
            warning = ex.Message;
        }

        return new InstallResult(
            resolved,
            target.ScriptFile,
            target.StartupFile,
            target.LoadLine,
            AlreadyLoaded: false,
            warning
        );
    }

    private static string Normalize(string shell) =>
        shell switch
        {
            "powershell" => "pwsh",
            _ => shell,
        };

    /// <summary>
    /// $SHELL, which is the shell the user's terminal starts - not whatever happens to be running
    /// tabard right now, which for a one-liner in a script is neither.
    /// </summary>
    private static string Detect()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");

        // Windows sets no $SHELL, and PowerShell is what a terminal opens there.
        if (string.IsNullOrEmpty(shell))
        {
            return OperatingSystem.IsWindows()
                ? "pwsh"
                : throw new InvalidOperationException(
                    $"could not tell which shell you use ($SHELL is not set). Name one: 'tabard completion install {Shells[0]}'."
                );
        }

        var name = Path.GetFileNameWithoutExtension(shell);

        return name is "bash" or "zsh" or "pwsh" or "powershell"
            ? name
            : throw new InvalidOperationException(
                $"your shell is '{name}', and tabard has completion scripts for "
                    + $"{string.Join(", ", Shells)} so far. Name one to install it anyway: "
                    + $"'tabard completion install {Shells[0]}'."
            );
    }

    private static Target TargetFor(string shell) =>
        shell switch
        {
            "bash" => new Target(
                Path.Combine(Paths.CompletionsRoot, "tabard.bash"),
                BashStartupFile(),
                // Written with $HOME and forward slashes rather than the resolved path: a startup
                // file is the kind of thing people sync between machines, and every shell that
                // reads this one takes both.
                "[ -r \"$HOME/.tabard/completions/tabard.bash\" ] && . \"$HOME/.tabard/completions/tabard.bash\""
            ),
            "zsh" => new Target(
                Path.Combine(Paths.CompletionsRoot, "tabard.zsh"),
                ZshStartupFile(),
                "[ -r \"$HOME/.tabard/completions/tabard.zsh\" ] && . \"$HOME/.tabard/completions/tabard.zsh\""
            ),
            "pwsh" => new Target(
                Path.Combine(Paths.CompletionsRoot, "tabard.ps1"),
                PwshStartupFile(),
                "if (Test-Path \"$HOME/.tabard/completions/tabard.ps1\") { . \"$HOME/.tabard/completions/tabard.ps1\" }"
            ),
            _ => throw new ArgumentException($"cannot install completion for '{shell}'."),
        };

    /// <summary>
    /// A macOS terminal starts a login shell, which reads .bash_profile and never .bashrc unless
    /// that file says to - so writing a .bashrc there when there is none would look like it worked
    /// and do nothing. Everywhere else .bashrc is the file for an interactive shell.
    /// </summary>
    private static string BashStartupFile()
    {
        var bashrc = Path.Combine(Paths.Home, ".bashrc");
        if (File.Exists(bashrc))
            return bashrc;

        var profile = Path.Combine(Paths.Home, ".bash_profile");
        return File.Exists(profile) || OperatingSystem.IsMacOS() ? profile : bashrc;
    }

    /// <summary>
    /// zsh has one startup file for interactive shells, and $ZDOTDIR moves the whole set of them.
    /// A .zshrc written to the home directory when that is set is a file zsh will never read.
    /// </summary>
    private static string ZshStartupFile()
    {
        var zdotdir = Environment.GetEnvironmentVariable("ZDOTDIR");

        return Path.Combine(string.IsNullOrEmpty(zdotdir) ? Paths.Home : zdotdir, ".zshrc");
    }

    /// <summary>
    /// PowerShell knows where its own profile is, and on Windows that is not guessable - a
    /// Documents folder redirected into OneDrive moves it. Asking costs one short process, and the
    /// conventional path is only the fallback for a machine where pwsh is not installed yet.
    /// </summary>
    private static string PwshStartupFile()
    {
        if (Launcher.Which("pwsh") is { } pwsh && AskForProfile(pwsh) is { Length: > 0 } answer)
            return answer;

        return OperatingSystem.IsWindows()
            ? Path.Combine(
                Paths.Home,
                "Documents",
                "PowerShell",
                "Microsoft.PowerShell_profile.ps1"
            )
            : Path.Combine(Paths.Home, ".config", "powershell", "Microsoft.PowerShell_profile.ps1");
    }

    private static string? AskForProfile(string pwsh)
    {
        try
        {
            var psi = new ProcessStartInfo(pwsh)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("$PROFILE");

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var answer = proc.StandardOutput.ReadToEnd().Trim();

            // A pwsh that hangs must not take 'completion install' with it; the conventional path
            // is a perfectly good answer.
            if (!proc.WaitForExit(10_000))
            {
                proc.Kill(entireProcessTree: true);
                return null;
            }

            return proc.ExitCode == 0 ? answer : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True if the startup file already pulls the completion in - by the line this command adds,
    /// or by one someone wrote themselves out of the help. Re-running then changes nothing, which
    /// is what makes this safe to put in a setup script.
    /// </summary>
    private static bool Loads(Target target)
    {
        try
        {
            if (!File.Exists(target.StartupFile))
                return false;

            var content = File.ReadAllText(target.StartupFile);
            var file = Path.GetFileName(target.ScriptFile);

            return content.Contains($".tabard/completions/{file}", StringComparison.Ordinal)
                || content.Contains($".tabard\\completions\\{file}", StringComparison.Ordinal)
                || content.Contains("tabard completion", StringComparison.Ordinal);
        }
        catch
        {
            // Unreadable is not the same as absent, and appending to a file we cannot read is a
            // good way to duplicate a line. Say it is there and let the caller's message stand.
            return true;
        }
    }

    private static void Append(Target target)
    {
        if (Path.GetDirectoryName(target.StartupFile) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        var existing = File.Exists(target.StartupFile) ? File.ReadAllText(target.StartupFile) : "";

        // A startup file not ending in a newline would otherwise get the comment glued onto
        // whatever its last line was.
        var gap = existing.Length == 0 || existing.EndsWith('\n') ? "" : Environment.NewLine;

        File.AppendAllText(
            target.StartupFile,
            $"{gap}{Environment.NewLine}# tabard completion{Environment.NewLine}{target.LoadLine}{Environment.NewLine}"
        );
    }

    private const string Bash = """
        # tabard completion for bash.
        #
        #   eval "$(tabard completion bash)"
        #
        # Every tab press asks tabard itself, so a profile you add or rename can be completed
        # straight away without reloading this.
        _tabard_complete() {
        	local candidate escaped
        	COMPREPLY=()

        	# $1 is the command as it was typed, so a renamed or wrapped copy asks its own binary
        	# rather than whichever tabard happens to come first on PATH.
        	while IFS= read -r candidate; do
        		[ -n "$candidate" ] || continue

        		# Keeps a profile name with a space in it one word once bash inserts it.
        		printf -v escaped '%q' "$candidate"
        		COMPREPLY+=("$escaped")
        	done < <("$1" __complete "$COMP_CWORD" "${COMP_WORDS[@]}" 2>/dev/null)
        }

        complete -F _tabard_complete tabard
        """;

    /// <summary>
    /// Written to work both ways round: sourced from a .zshrc, and autoloaded from an fpath
    /// directory under the name '_tabard' - which is where Homebrew puts it, and the only one of
    /// the two that needs the '#compdef' line and the self-call.
    /// </summary>
    private const string Zsh = """
        #compdef tabard
        # tabard completion for zsh.
        #
        #   eval "$(tabard completion zsh)"
        #
        # Every tab press asks tabard itself, so a profile you add or rename can be completed
        # straight away without reloading this.
        _tabard_complete() {
        	local -a candidates

        	# (f) splits on newlines and nothing else, so a profile name with a space in it stays
        	# one candidate, and :# drops the empty line an empty answer leaves behind. $words[1]
        	# is the command as it was typed, so a renamed or wrapped copy asks its own binary
        	# rather than whichever tabard happens to come first on PATH.
        	candidates=(${(f)"$("${words[1]}" __complete $((CURRENT - 1)) "${words[@]}" 2>/dev/null)"})

        	# compadd quotes what it inserts, so a name needing quotes arrives as one word.
        	compadd -- ${candidates:#}
        }

        if [[ ${zsh_eval_context[-1]} == loadautofunc ]]; then
        	# Autoloaded from fpath: this file is the completion function, so run it.
        	_tabard_complete "$@"
        else
        	# Sourced instead, where compdef is only defined once compinit has run. A startup file
        	# that has not got there yet gets one, rather than a completion that loads and then
        	# silently does nothing.
        	(( $+functions[compdef] )) || { autoload -Uz compinit && compinit }
        	compdef _tabard_complete tabard
        fi
        """;

    private const string Pwsh = """
        # tabard completion for PowerShell.
        #
        #   tabard completion pwsh | Out-String | Invoke-Expression
        #
        # Every tab press asks tabard itself, so a profile you add or rename can be completed
        # straight away without reloading this.
        Register-ArgumentCompleter -Native -CommandName tabard, tabard.exe -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            # Extent.Text is the word as it was typed, quotes and all; a quoted word has to arrive
            # as the value it stands for instead, or nothing would match it.
            $words = @(
                foreach ($element in $commandAst.CommandElements) {
                    if ($element -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                        $element.Value
                    }
                    else {
                        $element.Extent.Text
                    }
                }
            )

            # Nothing to complete means the cursor sits one past the last word. Deliberately not
            # padding the list out with an empty word to match: Windows PowerShell drops an empty
            # argument on its way to a native command.
            $cword = if ($wordToComplete) { $words.Count - 1 } else { $words.Count }

            & $words[0] __complete $cword @words 2>$null | ForEach-Object {
                # Quote anything that would otherwise go back in as two arguments.
                $insert = if ($_ -match '[\s''"]') { "'" + $_.Replace("'", "''") + "'" } else { $_ }

                [System.Management.Automation.CompletionResult]::new(
                    $insert,
                    $_,
                    [System.Management.Automation.CompletionResultType]::ParameterValue,
                    $_
                )
            }
        }
        """;
}
