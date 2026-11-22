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

    [Test]
    public async Task Pi_does_not_take_the_openrouter_flags()
    {
        await Assert
            .That(() => AddOptions.Parse(["local", "--pi", "--openrouter"], "usage"))
            .Throws<ArgumentException>();
    }
}
