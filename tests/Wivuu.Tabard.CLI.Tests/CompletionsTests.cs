namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// The profile-name cases read the real store under the redirected home from
/// <see cref="Sandbox"/>, so these share its one-home-per-process constraint.
/// </summary>
[NotInParallel]
public class CompletionsTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    [Test]
    public async Task Suggest_offers_the_commands_first()
    {
        await Assert.That(Complete("tabard", "")).Contains("use").And.Contains("openrouter");
    }

    [Test]
    public async Task Suggest_filters_by_what_has_been_typed()
    {
        await Assert.That(Complete("tabard", "op")).IsEquivalentTo(new[] { "openrouter" });
    }

    /// <summary>
    /// Matching is what a shell would do, and what Find() does with the name that lands on the
    /// line: 'Work' is not a completion of 'w'.
    /// </summary>
    [Test]
    public async Task Suggest_matches_case_sensitively()
    {
        MakeProfiles("Work");

        await Assert.That(Complete("tabard", "use", "w")).IsEmpty();
        await Assert.That(Complete("tabard", "use", "W")).IsEquivalentTo(new[] { "Work" });
    }

    /// <summary>The cursor sitting past the last word is the ordinary 'tabard use &lt;tab&gt;'.</summary>
    [Test]
    public async Task Suggest_offers_profiles_after_use()
    {
        MakeProfiles("work", "personal");

        await Assert
            .That(Complete("tabard", "use", ""))
            .IsEquivalentTo(new[] { "personal", "work" });
    }

    [Test]
    public async Task Suggest_offers_profiles_after_rm()
    {
        MakeProfiles("work");

        await Assert.That(Complete("tabard", "rm", "")).IsEquivalentTo(new[] { "work" });
        await Assert.That(Complete("tabard", "remove", "")).IsEquivalentTo(new[] { "work" });
    }

    /// <summary>Past the name the words are claude's, and '--' is what hands them over.</summary>
    [Test]
    public async Task Suggest_offers_the_passthrough_after_a_profile()
    {
        MakeProfiles("work");

        await Assert.That(Complete("tabard", "use", "work", "")).IsEquivalentTo(new[] { "--" });
        await Assert.That(Complete("tabard", "use", "work", "--", "")).IsEmpty();
    }

    /// <summary>The name being typed here is one that does not exist yet.</summary>
    [Test]
    public async Task Suggest_does_not_offer_profiles_to_add()
    {
        MakeProfiles("work");

        await Assert.That(Complete("tabard", "add", "")).DoesNotContain("work");
        await Assert.That(Complete("tabard", "add", "")).Contains("--openrouter");
    }

    [Test]
    public async Task Suggest_offers_the_openrouter_commands()
    {
        await Assert.That(Complete("tabard", "openrouter", "")).Contains("models");
        await Assert.That(Complete("tabard", "or", "s")).IsEquivalentTo(new[] { "set", "show" });
    }

    [Test]
    public async Task Suggest_offers_profiles_to_the_openrouter_commands_that_take_one()
    {
        MakeProfiles("work");

        await Assert.That(Complete("tabard", "openrouter", "set", "")).IsEquivalentTo(["work"]);
        await Assert.That(Complete("tabard", "openrouter", "key", "")).IsEquivalentTo(["work"]);
        await Assert.That(Complete("tabard", "openrouter", "show", "")).IsEquivalentTo(["work"]);
    }

    [Test]
    public async Task Suggest_offers_the_model_flags_after_a_profile()
    {
        MakeProfiles("work");

        await Assert.That(Complete("tabard", "openrouter", "set", "work", "")).Contains("--opus");

        // The key command takes no model flags - they belong to 'set', which says so when given one.
        await Assert
            .That(Complete("tabard", "openrouter", "key", "work", ""))
            .IsEquivalentTo(new[] { "--key-stdin" });
    }

    /// <summary>Whichever command it belongs to, the word after a model flag is that flag's value.</summary>
    [Test]
    [Arguments("--model")]
    [Arguments("--opus")]
    [Arguments("--subagent")]
    public async Task Suggest_offers_model_slugs_after_a_model_flag(string flag)
    {
        await Assert.That(Complete("tabard", "add", "work", flag, "")).Contains("auto");
        await Assert
            .That(Complete("tabard", "add", "work", flag, "anthropic/"))
            .Contains("anthropic/claude-opus-5");
    }

    [Test]
    public async Task Suggest_offers_the_shells_it_can_print()
    {
        await Assert.That(Complete("tabard", "completion", "")).Contains("install");
        await Assert
            .That(Complete("tabard", "completion", "install", ""))
            .IsEquivalentTo(Completions.Shells);
    }

    /// <summary>
    /// Nothing tabard can usefully guess at: 'ls' and 'help' take nothing, and the terms
    /// 'openrouter models' searches with are free text.
    /// </summary>
    [Test]
    public async Task Suggest_offers_nothing_where_there_is_nothing_to_offer()
    {
        MakeProfiles("work");

        await Assert.That(Complete("tabard", "ls", "")).IsEmpty();
        await Assert.That(Complete("tabard", "help", "")).IsEmpty();
        await Assert.That(Complete("tabard", "openrouter", "models", "")).IsEmpty();
        await Assert.That(Complete("tabard", "openrouter", "nope", "")).IsEmpty();
    }

    /// <summary>The command name itself is not tabard's to complete.</summary>
    [Test]
    public async Task Suggest_offers_nothing_at_the_command_itself()
    {
        await Assert.That(Completions.Suggest(["tabard"], 0)).IsEmpty();
    }

    /// <summary>An index past the end is what a shell that drops the empty trailing word sends.</summary>
    [Test]
    public async Task Suggest_treats_a_cursor_past_the_last_word_as_an_empty_one()
    {
        await Assert
            .That(Completions.Suggest(["tabard"], 1))
            .IsEquivalentTo(Completions.Suggest(["tabard", ""], 1));
    }

    /// <summary>The cursor can be in the middle of the line, and what follows it is not context.</summary>
    [Test]
    public async Task Suggest_ignores_the_words_after_the_cursor()
    {
        MakeProfiles("work");

        await Assert
            .That(Completions.Suggest(["tabard", "use", "", "--", "--help"], 2))
            .IsEquivalentTo(new[] { "work" });
    }

    /// <summary>
    /// One candidate per line has no way to carry a name holding a line break, and a name is only
    /// worth completing if what lands on the command line is the name.
    /// </summary>
    [Test]
    public async Task Suggest_drops_a_profile_whose_name_would_not_survive_the_protocol()
    {
        if (OperatingSystem.IsWindows())
            return;

        MakeProfiles("work", "two\nlines");

        await Assert.That(Complete("tabard", "use", "")).IsEquivalentTo(new[] { "work" });
    }

    [Test]
    [Arguments("bash", "complete -F _tabard_complete tabard")]
    [Arguments("zsh", "compdef _tabard_complete tabard")]
    [Arguments("pwsh", "Register-ArgumentCompleter")]
    [Arguments("powershell", "Register-ArgumentCompleter")]
    public async Task Script_prints_the_shell_it_was_asked_for(string shell, string expected)
    {
        await Assert.That(Completions.Script(shell)).Contains(expected);
    }

    /// <summary>Every script speaks the same protocol, so every one has to call it.</summary>
    [Test]
    public async Task Script_calls_the_completion_hook()
    {
        foreach (var shell in Completions.Shells)
            await Assert.That(Completions.Script(shell)).Contains("__complete");
    }

    /// <summary>
    /// Homebrew installs this one into an fpath directory as '_tabard', where zsh autoloads it
    /// rather than sourcing it - which needs the '#compdef' line on the very first byte.
    /// </summary>
    [Test]
    public async Task Script_makes_the_zsh_one_loadable_from_fpath()
    {
        await Assert.That(Completions.Script("zsh")).StartsWith("#compdef tabard");
        await Assert.That(Completions.Script("zsh")).Contains("loadautofunc");
    }

    [Test]
    public async Task Script_says_so_when_it_has_nothing_for_that_shell()
    {
        await Assert
            .That(() => Completions.Script("fish"))
            .Throws<ArgumentException>()
            .WithMessageContaining("bash");
    }

    /// <summary>
    /// bash only. Installing for pwsh asks pwsh where its profile is, and on Windows the answer
    /// comes from the known-folder API, which ignores the sandbox and would name the real one.
    /// </summary>
    [Test]
    public async Task Install_writes_the_script_and_has_the_startup_file_load_it()
    {
        var result = Completions.Install("bash");

        await Assert.That(File.ReadAllText(result.ScriptFile)).Contains("complete -F");
        await Assert.That(File.ReadAllText(result.StartupFile)).Contains(result.LoadLine);
        await Assert.That(result.AlreadyLoaded).IsFalse();
        await Assert.That(result.Warning).IsNull();
    }

    /// <summary>Re-running after an upgrade has to refresh the script without a second line.</summary>
    [Test]
    public async Task Install_adds_its_line_once()
    {
        var first = Completions.Install("bash");
        var again = Completions.Install("bash");

        await Assert.That(again.AlreadyLoaded).IsTrue();
        await Assert.That(again.StartupFile).IsEqualTo(first.StartupFile);

        var occurrences = File.ReadAllLines(first.StartupFile)
            .Count(line => line == first.LoadLine);

        await Assert.That(occurrences).IsEqualTo(1);
    }

    /// <summary>The startup file is the user's. Appending is the only thing done to it, and a file
    /// that does not end in a newline must not get the new line glued onto its last one.</summary>
    [Test]
    public async Task Install_keeps_what_the_startup_file_already_said()
    {
        var bashrc = Path.Combine(Paths.Home, ".bashrc");
        File.WriteAllText(bashrc, "export EDITOR=vim");

        var result = Completions.Install("bash");
        var lines = File.ReadAllLines(bashrc);

        await Assert.That(result.StartupFile).IsEqualTo(bashrc);
        await Assert.That(lines[0]).IsEqualTo("export EDITOR=vim");
        await Assert.That(lines).Contains(result.LoadLine);
    }

    /// <summary>Someone who wired it up by hand out of the help has it wired up.</summary>
    [Test]
    public async Task Install_leaves_a_startup_file_that_already_loads_it_alone()
    {
        var bashrc = Path.Combine(Paths.Home, ".bashrc");
        const string byHand = "eval \"$(tabard completion bash)\"\n";
        File.WriteAllText(bashrc, byHand);

        var result = Completions.Install("bash");

        await Assert.That(result.AlreadyLoaded).IsTrue();
        await Assert.That(File.ReadAllText(bashrc)).IsEqualTo(byHand);
    }

    /// <summary>$SHELL is the shell the user's terminal starts, which is the one to install for.</summary>
    [Test]
    public async Task Install_falls_back_to_the_shell_in_the_environment()
    {
        var before = Environment.GetEnvironmentVariable("SHELL");

        try
        {
            Environment.SetEnvironmentVariable("SHELL", "/opt/homebrew/bin/bash");
            await Assert.That(Completions.Install(null).Shell).IsEqualTo("bash");

            Environment.SetEnvironmentVariable("SHELL", "/bin/zsh");
            await Assert.That(Completions.Install(null).Shell).IsEqualTo("zsh");

            Environment.SetEnvironmentVariable("SHELL", "/usr/bin/fish");
            await Assert
                .That(() => Completions.Install(null))
                .Throws<InvalidOperationException>()
                .WithMessageContaining("fish");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELL", before);
        }
    }

    /// <summary>
    /// zsh reads its startup files out of $ZDOTDIR when that is set, so a .zshrc written to the
    /// home directory instead would be one zsh never looks at.
    /// </summary>
    [Test]
    public async Task Install_follows_ZDOTDIR_for_zsh()
    {
        var before = Environment.GetEnvironmentVariable("ZDOTDIR");
        var elsewhere = Sandbox.Scratch();

        try
        {
            Environment.SetEnvironmentVariable("ZDOTDIR", elsewhere);

            var result = Completions.Install("zsh");

            await Assert.That(result.StartupFile).IsEqualTo(Path.Combine(elsewhere, ".zshrc"));
            await Assert.That(File.ReadAllText(result.StartupFile)).Contains(result.LoadLine);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZDOTDIR", before);
        }
    }

    /// <summary>Nothing is written for a shell there is no script for.</summary>
    [Test]
    public async Task Install_refuses_a_shell_it_has_nothing_for()
    {
        await Assert.That(() => Completions.Install("fish")).Throws<ArgumentException>();
        await Assert.That(Directory.Exists(Paths.CompletionsRoot)).IsFalse();
    }

    /// <summary>Completes the last word, which is how the shells always call this.</summary>
    private static IEnumerable<string> Complete(params string[] words) =>
        Completions.Suggest(words, words.Length - 1);

    private static void MakeProfiles(params string[] names)
    {
        foreach (var name in names)
            Directory.CreateDirectory(Path.Combine(Paths.ProfilesRoot, name));
    }
}
