namespace Wivuu.Tabard.Cli.Tests;

[NotInParallel]
public class HarnessTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    [Test]
    public async Task Profiles_launch_claude_unless_told_otherwise()
    {
        await Assert.That(Harnesses.Of("work")).IsEqualTo(Harness.Claude);
        await Assert.That(File.Exists(Paths.HarnessesFile)).IsFalse();
    }

    [Test]
    public async Task Set_round_trips_and_claude_is_never_written_down()
    {
        Harnesses.Set("local pi", Harness.Pi);
        Harnesses.Set("work", Harness.Claude);

        await Assert.That(Harnesses.Of("local pi")).IsEqualTo(Harness.Pi);
        await Assert.That(File.ReadAllLines(Paths.HarnessesFile)).IsEquivalentTo(["pi local pi"]);

        Harnesses.Set("local pi", Harness.Claude);
        await Assert.That(Harnesses.Of("local pi")).IsEqualTo(Harness.Claude);
        await Assert.That(File.ReadAllLines(Paths.HarnessesFile)).IsEmpty();
    }

    [Test]
    public async Task Unknown_harnesses_and_junk_lines_are_ignored()
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllLines(Paths.HarnessesFile, ["", "pi", "emacs work", "pi  local "]);

        var all = Harnesses.All();

        await Assert.That(all.Keys).IsEquivalentTo(["local"]);
    }

    [Test]
    public async Task Rename_and_delete_carry_the_harness()
    {
        var pi = ProfileStore.Create("pi");
        Harnesses.Set("pi", Harness.Pi);

        var renamed = ProfileStore.Rename(pi, "agent").Profile;
        await Assert.That(Harnesses.Of("agent")).IsEqualTo(Harness.Pi);
        await Assert.That(Harnesses.Of("pi")).IsEqualTo(Harness.Claude);

        ProfileStore.Delete(renamed);
        await Assert.That(Harnesses.All()).IsEmpty();
    }

    [Test]
    public async Task A_pi_profile_describes_its_default_model()
    {
        var profile = ProfileStore.Create("pi");
        Harnesses.Set("pi", Harness.Pi);
        File.WriteAllText(
            Path.Combine(profile.Dir, "settings.json"),
            """{ "defaultProvider": "strata", "defaultModel": "qwen" }"""
        );

        var found = ProfileStore.Find("pi")!;

        await Assert.That(found.Describe()).IsEqualTo("strata  -  qwen");
        await Assert.That(found.Summary()).IsEqualTo("pi      strata  -  qwen");
    }

    [Test]
    public async Task A_new_pi_profile_says_it_is_not_set_up()
    {
        ProfileStore.Create("pi");
        Harnesses.Set("pi", Harness.Pi);

        await Assert.That(ProfileStore.Find("pi")!.Summary()).IsEqualTo("pi      not set up");
    }

    [Test]
    public async Task A_claude_profile_summary_leads_with_claude()
    {
        ProfileStore.Create("work");

        await Assert.That(ProfileStore.Find("work")!.Summary()).IsEqualTo("claude  no token file");
    }

    /// <summary>pi gets the profile through its own variable, and ~/.claude is left on the Claude
    /// profile it was on - a pi directory is not a Claude Code config.</summary>
    [Test]
    public async Task Use_launches_pi_with_its_config_dir()
    {
        using var claude = new FakeClaude();
        using var pi = new FakeClaude("pi", "PI_CODING_AGENT_DIR");
        var work = ProfileStore.Create("work");
        var local = ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);

        global::Cli.Run(["switch", "work"]);

        await Assert.That(global::Cli.Run(["use", "local", "--", "-c"])).IsEqualTo(0);

        await Assert.That(claude.Launches).IsEmpty();
        await Assert.That(pi.Launches.Count).IsEqualTo(1);
        await Assert.That(pi.Launches[0].ConfigDir).IsEqualTo(Path.GetFullPath(local.Dir));
        await Assert.That(pi.Launches[0].Args).IsEqualTo("-c");
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, work.Dir)).IsTrue();
    }

    [Test]
    public async Task Add_with_pi_makes_a_pi_profile_and_launches_it()
    {
        using var pi = new FakeClaude("pi", "PI_CODING_AGENT_DIR");

        await Assert.That(global::Cli.Run(["add", "local", "--pi"])).IsEqualTo(0);

        await Assert.That(Harnesses.Of("local")).IsEqualTo(Harness.Pi);
        await Assert.That(pi.Launches.Count).IsEqualTo(1);
        await Assert
            .That(Links.PointsAt(Paths.ClaudeDir, ProfileStore.Find("local")!.Dir))
            .IsFalse();
    }

    /// <summary>Until a sandboxed pi exists, a pi profile marked sandboxed must refuse rather than
    /// quietly run outside one.</summary>
    [Test]
    public async Task A_sandboxed_pi_profile_refuses_to_launch()
    {
        using var pi = new FakeClaude("pi", "PI_CODING_AGENT_DIR");
        ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        Sandboxing.Set("local", true);

        await Assert
            .That(() => global::Cli.Run(["use", "local"]))
            .Throws<InvalidOperationException>();
        await Assert.That(pi.Launches).IsEmpty();
    }

    /// <summary>The shell function makes the file and names it; 'switch' fills it in with the
    /// one variable a bare 'pi' reads.</summary>
    [Test]
    public async Task Switch_to_pi_hands_its_dir_to_the_shell_function()
    {
        var local = ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        var file = Path.Combine(Sandbox.Scratch(), "env");
        File.WriteAllText(file, "");

        Environment.SetEnvironmentVariable("TABARD_ENV_FILE", file);
        try
        {
            await Assert.That(global::Cli.Run(["switch", "local"])).IsEqualTo(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TABARD_ENV_FILE", null);
        }

        await Assert
            .That(File.ReadAllText(file))
            .IsEqualTo($"PI_CODING_AGENT_DIR={Path.GetFullPath(local.Dir)}\n");
        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("local");
    }

    /// <summary>~/.claude already makes a Claude Code switch reach every shell, so the file is
    /// left empty - and a pi variable the shell has from an earlier switch is left alone.</summary>
    [Test]
    public async Task Switch_to_claude_hands_the_shell_nothing()
    {
        ProfileStore.Create("work");
        var file = Path.Combine(Sandbox.Scratch(), "env");
        File.WriteAllText(file, "");

        Environment.SetEnvironmentVariable("TABARD_ENV_FILE", file);
        try
        {
            await Assert.That(global::Cli.Run(["switch", "work"])).IsEqualTo(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TABARD_ENV_FILE", null);
        }

        await Assert.That(File.ReadAllText(file)).IsEmpty();
    }

    /// <summary>A file the function did not make is not tabard's to create.</summary>
    [Test]
    public async Task Switch_does_not_create_a_missing_env_file()
    {
        ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        var file = Path.Combine(Sandbox.Scratch(), "env");

        Environment.SetEnvironmentVariable("TABARD_ENV_FILE", file);
        try
        {
            await Assert.That(global::Cli.Run(["switch", "local"])).IsEqualTo(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TABARD_ENV_FILE", null);
        }

        await Assert.That(File.Exists(file)).IsFalse();
    }

    /// <summary>
    /// The function itself, in each shell: a stand-in tabard writes the file, and only the pi
    /// variable makes it into the shell - the file is data, never code - with tabard's exit code,
    /// and the temporary file gone afterwards.
    /// </summary>
    [Test]
    [Arguments("bash")]
    [Arguments("zsh")]
    public async Task The_shell_function_exports_only_what_switch_wrote(string shell)
    {
        if (OperatingSystem.IsWindows() || Launcher.Which(shell) is not { } exe)
            return;

        var dir = Sandbox.Scratch();
        var bin = Path.Combine(dir, "bin");
        var temp = Path.Combine(dir, "tmp");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(temp);

        var fake = Path.Combine(bin, "tabard");
        File.WriteAllText(
            fake,
            "#!/bin/sh\n"
                + "[ -n \"$TABARD_ENV_FILE\" ] && printf '%s\\n' 'PI_CODING_AGENT_DIR=/p/local pi' "
                + "'EVIL=1' '$(touch pwned)' >> \"$TABARD_ENV_FILE\"\n"
                + "exit 3\n"
        );
        File.SetUnixFileMode(
            fake,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );

        var script = Path.Combine(dir, $"tabard.{shell}");
        File.WriteAllText(script, Completions.Script(shell));

        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(
            $". '{script}' 2>/dev/null; tabard switch local; "
                + "echo \"$?|$PI_CODING_AGENT_DIR|${EVIL-unset}\"; "
                + "tabard ls; echo \"$PI_CODING_AGENT_DIR\""
        );
        psi.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
        psi.Environment["TMPDIR"] = temp;
        psi.Environment.Remove("PI_CODING_AGENT_DIR");

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var output = (await proc.StandardOutput.ReadToEndAsync()).Split('\n');
        await proc.WaitForExitAsync();

        await Assert.That(output[0]).IsEqualTo("3|/p/local pi|unset");
        await Assert.That(output[1]).IsEqualTo("/p/local pi");
        await Assert.That(File.Exists(Path.Combine(dir, "pwned"))).IsFalse();
        await Assert.That(Directory.EnumerateFileSystemEntries(temp)).IsEmpty();
    }

    [Test]
    public async Task Pi_does_not_take_the_openrouter_flags()
    {
        await Assert
            .That(() => AddOptions.Parse(["local", "--pi", "--openrouter"], "usage"))
            .Throws<ArgumentException>();
    }
}
