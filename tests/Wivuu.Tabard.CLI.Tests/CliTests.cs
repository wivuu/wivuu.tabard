namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// 'switch' is the one command that changes what a bare 'claude' would use and then returns, so it
/// can be driven end to end without a claude process. Everything else in the dispatch table either
/// launches or asks a question, which is why this covers only the one verb.
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
}
