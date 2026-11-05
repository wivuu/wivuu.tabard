namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// 'switch' changes what a bare 'claude' would use and then returns, so it can be driven end to end
/// as it is. The launching commands get a <see cref="FakeClaude"/> to launch instead. What this cannot
/// reach is the live selector, a render loop against a real console: without a terminal it falls back
/// to printing a list, and the tests that rely on that step aside when the suite has a real one.
///
/// The class under test lives in the global namespace, and inside this one the bare name 'Cli'
/// resolves to the <c>Wivuu.Tabard.Cli</c> namespace instead - hence <c>global::Cli</c> throughout.
/// Runs against the redirected home from <see cref="Sandbox"/>, so it shares the
/// one-home-per-process constraint.
/// </summary>
[NotInParallel]
public class CliTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    [Test]
    public async Task Switch_points_the_links_at_the_named_profile()
    {
        ProfileStore.Create("work");
        var personal = ProfileStore.Create("personal");

        var code = global::Cli.Run(["switch", "personal"]);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("personal");
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, personal.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, personal.ClaudeJsonFile)).IsTrue();
    }

    /// <summary>Switching again has to move both links, or a bare 'claude' would read one
    /// profile's config dir alongside another's .claude.json.</summary>
    [Test]
    public async Task Switch_moves_the_links_off_the_previous_profile()
    {
        var work = ProfileStore.Create("work");
        var personal = ProfileStore.Create("personal");

        global::Cli.Run(["switch", "work"]);
        var code = global::Cli.Run(["switch", "personal"]);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, work.Dir)).IsFalse();
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, personal.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, personal.ClaudeJsonFile)).IsTrue();
    }

    /// <summary>The short form is the same command; it exists because this one gets typed a lot.
    /// </summary>
    [Test]
    public async Task Switch_answers_to_its_alias()
    {
        var work = ProfileStore.Create("work");
        ProfileStore.Create("personal");

        await Assert.That(global::Cli.Run(["sw", "work"])).IsEqualTo(0);
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, work.Dir)).IsTrue();
    }

    /// <summary>
    /// With one profile there is nothing to pick, and the run is worth making anyway: it is how
    /// links that have drifted get repaired.
    /// </summary>
    [Test]
    public async Task Switch_with_no_name_takes_the_only_profile()
    {
        var only = ProfileStore.Create("only");

        await Assert.That(global::Cli.Run(["switch"])).IsEqualTo(0);
        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("only");
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, only.Dir)).IsTrue();
    }

    /// <summary>'switch' moves a pointer. Creating the first profile is 'add' or a bare 'tabard',
    /// and quietly making one here would adopt ~/.claude as a side effect of a typo.</summary>
    [Test]
    public async Task Switch_creates_nothing_when_there_are_no_profiles()
    {
        await Assert
            .That(() => global::Cli.Run(["switch"]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("tabard add");

        // Adopt() makes the empty store root, as it does for every command that would change
        // something. What must not appear is a profile, or a ~/.claude link into one.
        await Assert.That(ProfileStore.List()).IsEmpty();
        await Assert.That(Directory.Exists(Paths.ClaudeDir)).IsFalse();
        await Assert.That(ProfileStore.LastUsed()).IsNull();
    }

    [Test]
    public async Task Switch_reports_a_name_that_is_not_there()
    {
        ProfileStore.Create("work");

        await Assert
            .That(() => global::Cli.Run(["switch", "nope"]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("tabard ls");
    }

    /// <summary>A flag would otherwise be looked up as a profile name and reported as a missing
    /// one, which says nothing about the real mistake.</summary>
    [Test]
    public async Task Switch_rejects_what_it_does_not_take()
    {
        ProfileStore.Create("work");

        await Assert
            .That(() => global::Cli.Run(["switch", "--force"]))
            .Throws<ArgumentException>()
            .WithMessageContaining("usage: tabard switch");

        await Assert
            .That(() => global::Cli.Run(["switch", "work", "extra"]))
            .Throws<ArgumentException>()
            .WithMessageContaining("usage: tabard switch");
    }

    /// <summary>
    /// The first run adopts an existing ~/.claude rather than relinking over it, exactly as 'use'
    /// does - otherwise the switch would strand a real login outside the store.
    /// </summary>
    [Test]
    public async Task Switch_adopts_an_unmanaged_claude_directory_first()
    {
        Directory.CreateDirectory(Paths.ClaudeDir);
        File.WriteAllText(Path.Combine(Paths.ClaudeDir, "settings.json"), "{}");

        await Assert.That(global::Cli.Run(["switch"])).IsEqualTo(0);

        var adopted = Path.Combine(Paths.ProfilesRoot, ProfileStore.DefaultProfileName);

        await Assert.That(File.Exists(Path.Combine(adopted, "settings.json"))).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, adopted)).IsTrue();
        await Assert.That(ProfileStore.LastUsed()).IsEqualTo(ProfileStore.DefaultProfileName);
    }

    /// <summary>
    /// The folder's answer, not the global one: 'switch' has pointed ~/.claude elsewhere since, and
    /// that is what a launch following ~/.tabard/last would have picked up.
    /// </summary>
    [Test]
    public async Task Bare_launches_the_profile_last_used_in_this_folder()
    {
        using var claude = new FakeClaude();
        var work = ProfileStore.Create("work");
        ProfileStore.Create("personal");

        global::Cli.Run(["use", "work"]);
        global::Cli.Run(["switch", "personal"]);

        await Assert.That(global::Cli.Run([])).IsEqualTo(0);

        await Assert.That(claude.Launches.Count).IsEqualTo(2);
        await Assert.That(claude.Launches[^1].ConfigDir).IsEqualTo(Path.GetFullPath(work.Dir));
        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("work");
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, work.Dir)).IsTrue();
    }

    [Test]
    [Arguments(new[] { "--resume" }, "--resume")]
    [Arguments(new[] { "--", "--help" }, "--help")]
    [Arguments(new[] { "-p", "hello" }, "-p hello")]
    public async Task Bare_passes_claude_args_to_the_remembered_profile(
        string[] args,
        string expected
    )
    {
        using var claude = new FakeClaude();
        ProfileStore.Create("work");
        ProfileStore.Create("personal");
        global::Cli.Run(["use", "work"]);

        await Assert.That(global::Cli.Run(args)).IsEqualTo(0);

        await Assert.That(claude.Launches[^1].Args).IsEqualTo(expected);
    }

    [Test]
    public async Task Use_changes_what_the_folder_remembers()
    {
        using var claude = new FakeClaude();
        ProfileStore.Create("work");
        ProfileStore.Create("personal");

        global::Cli.Run(["use", "work"]);
        await Assert.That(DirectoryMemory.Recall()).IsEqualTo("work");

        global::Cli.Run(["use", "personal"]);
        await Assert.That(DirectoryMemory.Recall()).IsEqualTo("personal");
    }

    /// <summary>It starts nothing, so nothing has been used in this folder.</summary>
    [Test]
    public async Task Switch_leaves_what_the_folder_remembers_alone()
    {
        using var claude = new FakeClaude();
        ProfileStore.Create("work");
        ProfileStore.Create("personal");

        global::Cli.Run(["switch", "personal"]);
        await Assert.That(DirectoryMemory.Recall()).IsNull();

        global::Cli.Run(["use", "work"]);
        global::Cli.Run(["switch", "personal"]);
        await Assert.That(DirectoryMemory.Recall()).IsEqualTo("work");
    }

    /// <summary>Nobody is there to pick a name, and a pipe gets the login flow it always has.
    /// </summary>
    [Test]
    public async Task Without_a_terminal_bare_creates_the_first_profile()
    {
        SkipIfThereIsATerminal();
        using var claude = new FakeClaude();

        await Assert.That(global::Cli.Run([])).IsEqualTo(0);

        var created = ProfileStore.Find(ProfileStore.DefaultProfileName);

        await Assert.That(created).IsNotNull();
        await Assert.That(claude.Launches[^1].ConfigDir).IsEqualTo(Path.GetFullPath(created!.Dir));
        await Assert.That(DirectoryMemory.Recall()).IsEqualTo(ProfileStore.DefaultProfileName);
    }

    [Test]
    public async Task Without_a_terminal_bare_launches_the_only_profile()
    {
        SkipIfThereIsATerminal();
        using var claude = new FakeClaude();
        var only = ProfileStore.Create("only");

        await Assert.That(global::Cli.Run([])).IsEqualTo(0);

        await Assert.That(claude.Launches[^1].ConfigDir).IsEqualTo(Path.GetFullPath(only.Dir));
        await Assert.That(DirectoryMemory.Recall()).IsEqualTo("only");
    }

    /// <summary>Exit 130 and no launch is how the plain selector looks from out here.</summary>
    [Test]
    public async Task Without_a_terminal_a_new_folder_with_a_real_choice_launches_nothing()
    {
        SkipIfThereIsATerminal();
        using var claude = new FakeClaude();
        ProfileStore.Create("work");
        ProfileStore.Create("personal");

        await Assert.That(global::Cli.Run([])).IsEqualTo(130);

        await Assert.That(claude.Launches).IsEmpty();
        await Assert.That(DirectoryMemory.Recall()).IsNull();
        await Assert.That(ProfileStore.LastUsed()).IsNull();
    }

    /// <summary>A hint naming a profile that has gone means the folder is not known, not an error.
    /// </summary>
    [Test]
    public async Task A_remembered_profile_that_has_gone_falls_through_to_selection()
    {
        SkipIfThereIsATerminal();
        using var claude = new FakeClaude();
        ProfileStore.Create("work");
        ProfileStore.Create("personal");
        DirectoryMemory.Remember("gone");

        await Assert.That(global::Cli.Run([])).IsEqualTo(130);

        await Assert.That(claude.Launches).IsEmpty();
    }

    /// <summary>'select' is the way to change a folder's answer, so it must not just repeat it.
    /// </summary>
    [Test]
    public async Task Select_asks_even_in_a_folder_it_knows()
    {
        SkipIfThereIsATerminal();
        using var claude = new FakeClaude();
        ProfileStore.Create("work");
        ProfileStore.Create("personal");
        global::Cli.Run(["use", "work"]);

        await Assert.That(global::Cli.Run(["select"])).IsEqualTo(130);

        await Assert.That(claude.Launches.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(new[] { "select" }, "")]
    [Arguments(new[] { "select", "--resume" }, "--resume")]
    [Arguments(new[] { "select", "--", "--resume" }, "--resume")]
    public async Task Select_passes_claude_args_through(string[] args, string expected)
    {
        SkipIfThereIsATerminal();
        using var claude = new FakeClaude();
        ProfileStore.Create("only");

        await Assert.That(global::Cli.Run(args)).IsEqualTo(0);

        await Assert.That(claude.Launches[^1].Args).IsEqualTo(expected);
        await Assert.That(DirectoryMemory.Recall()).IsEqualTo("only");
    }

    /// <summary>With a real terminal these would open the live selector and wait for a key forever.
    /// </summary>
    private static void SkipIfThereIsATerminal() =>
        Skip.When(Term.Interactive, "needs the selector's no-terminal fallback");
}
