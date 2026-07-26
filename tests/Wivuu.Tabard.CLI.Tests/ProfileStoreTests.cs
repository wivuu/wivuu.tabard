namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// These drive the real filesystem under the redirected home from <see cref="Sandbox"/>. There is
/// one home per process and <see cref="Paths"/> resolved it once, so they cannot overlap.
/// </summary>
[NotInParallel]
public class ProfileStoreTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    [Test]
    public async Task List_is_empty_before_anything_exists()
    {
        await Assert.That(ProfileStore.List()).IsEmpty();
    }

    /// <summary>A part-finished copy is staged under a dotted name and must stay invisible.</summary>
    [Test]
    public async Task List_skips_dotted_directories()
    {
        MakeProfileDir("work");
        MakeProfileDir(".incoming-abc123");

        await Assert.That(Names()).IsEqualTo("work");
    }

    /// <summary>
    /// The list used to float the last used to the top, which reshuffled it on every launch. The
    /// numbered rows and the picker's reorder mode both depend on that no longer happening.
    /// </summary>
    [Test]
    public async Task List_ignores_the_last_used_when_ordering()
    {
        MakeProfileDir("zeta");
        MakeProfileDir("alpha");
        MakeProfileDir("work");
        ProfileStore.SetLastUsed("work");

        await Assert.That(Names()).IsEqualTo("alpha,work,zeta");
    }

    [Test]
    public async Task List_orders_by_name_when_there_is_no_order_file()
    {
        MakeProfileDir("zeta");
        MakeProfileDir("Alpha");

        await Assert.That(Names()).IsEqualTo("Alpha,zeta");
    }

    /// <summary>A stale 'last' naming a deleted profile must not make LastUsed() throw.</summary>
    [Test]
    public async Task List_tolerates_a_last_used_that_no_longer_exists()
    {
        MakeProfileDir("alpha");
        ProfileStore.SetLastUsed("gone");

        await Assert.That(Names()).IsEqualTo("alpha");
    }

    [Test]
    public async Task List_follows_the_order_file()
    {
        MakeProfileDir("alpha");
        MakeProfileDir("beta");
        MakeProfileDir("gamma");
        ProfileStore.SetOrder(["gamma", "alpha"]);

        await Assert.That(Names()).IsEqualTo("gamma,alpha,beta");
    }

    /// <summary>A profile created since the order was written - or created outside tabard - must
    /// land at the tail rather than pushing an arranged one down and renumbering the rows.</summary>
    [Test]
    public async Task List_puts_profiles_the_order_does_not_name_last_by_name()
    {
        MakeProfileDir("zeta");
        MakeProfileDir("Alpha");
        MakeProfileDir("work");
        ProfileStore.SetOrder(["work"]);

        await Assert.That(Names()).IsEqualTo("work,Alpha,zeta");
    }

    [Test]
    public async Task List_ignores_order_entries_for_profiles_that_are_gone()
    {
        MakeProfileDir("alpha");
        MakeProfileDir("beta");
        ProfileStore.SetOrder(["gone", "beta", "alpha"]);

        await Assert.That(Names()).IsEqualTo("beta,alpha");
    }

    /// <summary>
    /// The match is Ordinal, like Find. Ranking case-insensitively would give two profiles
    /// differing only in case the same rank, and a comparison returning 0 for two distinct
    /// profiles is exactly the unstable Sort the name tiebreak exists to prevent.
    /// </summary>
    [Test]
    public async Task List_ignores_an_order_entry_differing_only_in_case()
    {
        MakeProfileDir("alpha");
        MakeProfileDir("work");
        ProfileStore.SetOrder(["WORK"]);

        await Assert.That(Names()).IsEqualTo("alpha,work");
    }

    /// <summary>
    /// Case-insensitive matching here would let 'rm Work' delete 'work' on a case-sensitive
    /// filesystem. Create() is what stops the two coexisting.
    /// </summary>
    [Test]
    public async Task Find_matches_exactly()
    {
        MakeProfileDir("work");

        await Assert.That(ProfileStore.Find("work")).IsNotNull();
        await Assert.That(ProfileStore.Find("Work")).IsNull();
        await Assert.That(ProfileStore.Find("missing")).IsNull();
    }

    [Test]
    public async Task Create_makes_a_directory_under_the_profiles_root()
    {
        var profile = ProfileStore.Create("work");

        await Assert.That(profile.Name).IsEqualTo("work");
        await Assert.That(profile.Dir).IsEqualTo(Path.Combine(Paths.ProfilesRoot, "work"));
        await Assert.That(Directory.Exists(profile.Dir)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments(".hidden")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("trailing.")]
    [Arguments("trailing ")]
    [Arguments("CON")]
    [Arguments("lpt9")]
    [Arguments("nul.txt")]
    public async Task Create_rejects_a_name_that_would_not_travel(string name)
    {
        await Assert.That(() => ProfileStore.Create(name)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Create_rejects_a_name_that_is_too_long()
    {
        await Assert
            .That(() => ProfileStore.Create(new string('a', 65)))
            .Throws<ArgumentException>();
        ProfileStore.Create(new string('a', 64));

        await Assert.That(Names()).IsEqualTo(new string('a', 64));
    }

    /// <summary>
    /// Both can exist side by side on a case-sensitive filesystem, and then no lookup can tell
    /// them apart safely.
    /// </summary>
    [Test]
    public async Task Create_rejects_a_name_differing_only_in_case()
    {
        ProfileStore.Create("work");

        await Assert.That(() => ProfileStore.Create("WORK")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Create_keeps_the_directory_private()
    {
        if (OperatingSystem.IsWindows())
            return;

        var profile = ProfileStore.Create("work");
        var mode = File.GetUnixFileMode(profile.Dir);

        await Assert
            .That(mode)
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task LastUsed_round_trips()
    {
        await Assert.That(ProfileStore.LastUsed()).IsNull();

        ProfileStore.SetLastUsed("work");
        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("work");
    }

    [Test]
    [Arguments("  work \n", "work")]
    [Arguments("", null)]
    [Arguments("   ", null)]
    public async Task LastUsed_trims_what_it_finds(string content, string? expected)
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllText(Paths.LastUsedFile, content);

        await Assert.That(ProfileStore.LastUsed()).IsEqualTo(expected);
    }

    [Test]
    public async Task SetLastUsed_leaves_no_temp_file_behind()
    {
        ProfileStore.SetLastUsed("work");

        var strays = Directory.EnumerateFiles(Paths.TabardRoot, "*.tmp").ToList();
        await Assert.That(strays).IsEmpty();
    }

    [Test]
    public async Task Order_round_trips()
    {
        await Assert.That(ProfileStore.Order()).IsEmpty();

        ProfileStore.SetOrder(["beta", "alpha"]);
        await Assert.That(Ordered()).IsEqualTo("beta,alpha");
    }

    /// <summary>The file is meant to be hand-editable, so it has to survive being hand-edited.</summary>
    [Test]
    public async Task Order_trims_and_skips_blank_lines()
    {
        WriteOrder("\n  beta  \n\nalpha\n");

        await Assert.That(Ordered()).IsEqualTo("beta,alpha");
    }

    /// <summary>A profile named twice has no position at all, so the first mention wins.</summary>
    [Test]
    public async Task Order_keeps_only_the_first_of_a_duplicate()
    {
        WriteOrder("alpha\nbeta\nalpha\n");

        await Assert.That(Ordered()).IsEqualTo("alpha,beta");
    }

    /// <summary>A directory where the file should be is the portable way to make a read fail on
    /// all three CI platforms. Losing the order must cost an alphabetical list, not a crash.</summary>
    [Test]
    public async Task Order_returns_nothing_when_the_file_cannot_be_read()
    {
        MakeProfileDir("zeta");
        MakeProfileDir("alpha");
        Directory.CreateDirectory(Paths.OrderFile);

        await Assert.That(ProfileStore.Order()).IsEmpty();
        await Assert.That(Names()).IsEqualTo("alpha,zeta");
    }

    /// <summary>
    /// Path.GetInvalidFileNameChars() is only { '\0', '/' } on Unix, so a name holding a line
    /// break is a legal profile there. One line per name cannot say that, and the halves would
    /// read back as two entries naming nothing.
    /// </summary>
    [Test]
    public async Task SetOrder_drops_a_name_holding_a_line_break()
    {
        ProfileStore.SetOrder(["alpha", "two\nlines", "beta"]);

        await Assert.That(Ordered()).IsEqualTo("alpha,beta");
    }

    [Test]
    public async Task SetOrder_leaves_no_temp_file_behind()
    {
        ProfileStore.SetOrder(["work"]);

        var strays = Directory.EnumerateFiles(Paths.TabardRoot, "*.tmp").ToList();
        await Assert.That(strays).IsEmpty();
    }

    /// <summary>The order names profiles, so a rename that did not carry over would drop the
    /// profile from wherever the user put it to the end of the list.</summary>
    [Test]
    public async Task Rename_keeps_the_profile_in_its_place_in_the_order()
    {
        MakeProfileDir("alpha");
        MakeProfileDir("beta");
        MakeProfileDir("gamma");
        ProfileStore.SetOrder(["gamma", "beta", "alpha"]);

        ProfileStore.Rename(ProfileStore.Find("beta")!, "zeta");

        await Assert.That(Ordered()).IsEqualTo("gamma,zeta,alpha");
        await Assert.That(Names()).IsEqualTo("gamma,zeta,alpha");
    }

    /// <summary>Replaced, never appended: a profile the order does not name has no position to
    /// keep, and inventing one for it would move whatever it landed in front of.</summary>
    [Test]
    public async Task Rename_does_not_add_a_profile_the_order_never_named()
    {
        MakeProfileDir("alpha");
        MakeProfileDir("beta");
        ProfileStore.SetOrder(["alpha"]);

        ProfileStore.Rename(ProfileStore.Find("beta")!, "zeta");

        await Assert.That(Ordered()).IsEqualTo("alpha");
    }

    [Test]
    public async Task Delete_drops_the_profile_from_the_order()
    {
        MakeProfileDir("alpha");
        MakeProfileDir("beta");
        MakeProfileDir("gamma");
        ProfileStore.SetOrder(["alpha", "beta", "gamma"]);

        ProfileStore.Delete(ProfileStore.Find("beta")!);

        await Assert.That(Ordered()).IsEqualTo("alpha,gamma");
    }

    [Test]
    public async Task WouldAdopt_only_when_a_real_claude_dir_stands_alone()
    {
        await Assert.That(ProfileStore.WouldAdopt()).IsFalse();

        Directory.CreateDirectory(Paths.ClaudeDir);
        await Assert.That(ProfileStore.WouldAdopt()).IsTrue();

        MakeProfileDir("work");
        await Assert.That(ProfileStore.WouldAdopt()).IsFalse();
    }

    [Test]
    public async Task WouldAdopt_is_false_once_claude_dir_is_a_link()
    {
        var profile = ProfileStore.Create("work");
        Directory.CreateSymbolicLink(Paths.ClaudeDir, profile.Dir);

        await Assert.That(ProfileStore.WouldAdopt()).IsFalse();
    }

    [Test]
    public async Task Adopt_moves_an_existing_login_into_the_store()
    {
        Directory.CreateDirectory(Paths.ClaudeDir);
        File.WriteAllText(Path.Combine(Paths.ClaudeDir, "settings.json"), "{}");
        File.WriteAllText(Paths.ClaudeJson, """{"numStartups":3}""");

        var result = ProfileStore.AdoptExistingIfNeeded();
        var target = Path.Combine(Paths.ProfilesRoot, ProfileStore.DefaultProfileName);

        await Assert.That(result.Adopted).IsTrue();
        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(File.ReadAllText(Path.Combine(target, "settings.json"))).IsEqualTo("{}");

        // The one at ~ is the file Claude Code actually reads, so it moves inside the profile too.
        await Assert
            .That(File.ReadAllText(Path.Combine(target, ".claude.json")))
            .IsEqualTo("""{"numStartups":3}""");
        await Assert.That(File.Exists(Paths.ClaudeJson)).IsFalse();
        await Assert.That(Directory.Exists(Paths.ClaudeDir)).IsFalse();
    }

    [Test]
    public async Task Adopt_is_a_no_op_the_second_time()
    {
        Directory.CreateDirectory(Paths.ClaudeDir);
        ProfileStore.AdoptExistingIfNeeded();

        await Assert.That(ProfileStore.AdoptExistingIfNeeded().Adopted).IsFalse();
        await Assert.That(Names()).IsEqualTo(ProfileStore.DefaultProfileName);
    }

    /// <summary>
    /// A .claude.json that came along inside the config dir is a vestige of an older layout. It is
    /// kept, but under a name nothing will load.
    /// </summary>
    [Test]
    public async Task Adopt_keeps_a_config_dir_claude_json_out_of_the_way()
    {
        Directory.CreateDirectory(Paths.ClaudeDir);
        File.WriteAllText(Path.Combine(Paths.ClaudeDir, ".claude.json"), "\"inner\"");
        File.WriteAllText(Paths.ClaudeJson, "\"outer\"");

        var result = ProfileStore.AdoptExistingIfNeeded();
        var target = Path.Combine(Paths.ProfilesRoot, ProfileStore.DefaultProfileName);

        await Assert
            .That(File.ReadAllText(Path.Combine(target, ".claude.json")))
            .IsEqualTo("\"outer\"");
        await Assert
            .That(File.ReadAllText(Path.Combine(target, ".claude.json.vestigial")))
            .IsEqualTo("\"inner\"");
        await Assert.That(string.Join(" ", result.Warnings)).Contains("vestigial");
    }

    [Test]
    public async Task Adopt_leaves_a_claude_json_that_is_already_a_link()
    {
        Directory.CreateDirectory(Paths.ClaudeDir);
        var elsewhere = Path.Combine(Sandbox.Home, "elsewhere.json");
        File.WriteAllText(elsewhere, "\"theirs\"");
        File.CreateSymbolicLink(Paths.ClaudeJson, elsewhere);

        ProfileStore.AdoptExistingIfNeeded();

        await Assert.That(Links.PointsAt(Paths.ClaudeJson, elsewhere)).IsTrue();
    }

    [Test]
    public async Task Relink_creates_both_links()
    {
        var profile = ProfileStore.Create("work");
        var warnings = ProfileStore.Relink(profile);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, profile.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, profile.ClaudeJsonFile)).IsTrue();
    }

    [Test]
    public async Task Relink_repoints_both_links_together()
    {
        var first = ProfileStore.Create("first");
        var second = ProfileStore.Create("second");

        ProfileStore.Relink(first);
        var warnings = ProfileStore.Relink(second);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, second.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, second.ClaudeJsonFile)).IsTrue();
    }

    [Test]
    public async Task Relink_leaves_a_link_pointing_outside_the_store_alone()
    {
        var profile = ProfileStore.Create("work");
        var elsewhere = Path.Combine(Sandbox.Home, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Paths.ClaudeDir, elsewhere);

        var warnings = ProfileStore.Relink(profile);

        await Assert.That(Links.PointsAt(Paths.ClaudeDir, elsewhere)).IsTrue();
        await Assert.That(string.Join(" ", warnings)).Contains("left it alone");
    }

    [Test]
    public async Task Relink_leaves_real_content_alone()
    {
        Directory.CreateDirectory(Paths.ClaudeDir);
        File.WriteAllText(Path.Combine(Paths.ClaudeDir, "keep.txt"), "keep");
        File.WriteAllText(Paths.ClaudeJson, "\"keep\"");

        var profile = ProfileStore.Create("work");
        var warnings = ProfileStore.Relink(profile);

        await Assert
            .That(File.ReadAllText(Path.Combine(Paths.ClaudeDir, "keep.txt")))
            .IsEqualTo("keep");
        await Assert.That(File.ReadAllText(Paths.ClaudeJson)).IsEqualTo("\"keep\"");
        await Assert.That(warnings.Count).IsEqualTo(2);
    }

    /// <summary>
    /// 'claude migrate-installer' puts the binary under ~/.claude/local, so repointing at a profile
    /// without one would break the claude command machine-wide.
    /// </summary>
    [Test]
    public async Task Relink_refuses_to_orphan_a_local_install()
    {
        var installed = ProfileStore.Create("installed");
        Directory.CreateDirectory(Path.Combine(installed.Dir, "local"));
        ProfileStore.Relink(installed);

        var bare = ProfileStore.Create("bare");
        var warnings = ProfileStore.Relink(bare);

        await Assert.That(string.Join(" ", warnings)).Contains("migrate-installer");

        // Both links stay put, or a bare 'claude' would read one profile's dir and another's json.
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, installed.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, installed.ClaudeJsonFile)).IsTrue();
    }

    [Test]
    public async Task Relink_proceeds_when_the_target_has_a_local_install_too()
    {
        var installed = ProfileStore.Create("installed");
        Directory.CreateDirectory(Path.Combine(installed.Dir, "local"));
        ProfileStore.Relink(installed);

        var other = ProfileStore.Create("other");
        Directory.CreateDirectory(Path.Combine(other.Dir, "local"));
        var warnings = ProfileStore.Relink(other);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, other.Dir)).IsTrue();
    }

    [Test]
    public async Task Rename_moves_the_directory_with_its_contents()
    {
        var profile = ProfileStore.Create("work");
        File.WriteAllText(Path.Combine(profile.Dir, ".credentials.json"), "{}");

        var result = ProfileStore.Rename(profile, "day-job");

        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(result.Profile.Name).IsEqualTo("day-job");
        await Assert.That(Directory.Exists(profile.Dir)).IsFalse();
        await Assert
            .That(File.ReadAllText(Path.Combine(result.Profile.Dir, ".credentials.json")))
            .IsEqualTo("{}");
        await Assert.That(Names()).IsEqualTo("day-job");
    }

    [Test]
    public async Task Rename_to_the_same_name_changes_nothing()
    {
        var profile = ProfileStore.Create("work");

        var result = ProfileStore.Rename(profile, "work");

        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(Directory.Exists(profile.Dir)).IsTrue();
        await Assert.That(Names()).IsEqualTo("work");
    }

    /// <summary>Recasing is the one time the case-insensitive clash check is looking at the profile
    /// being renamed, and on a case-insensitive filesystem the move needs a staging name.</summary>
    [Test]
    public async Task Rename_can_change_only_the_case()
    {
        var profile = ProfileStore.Create("work");

        var result = ProfileStore.Rename(profile, "Work");

        await Assert.That(result.Profile.Name).IsEqualTo("Work");
        await Assert.That(Names()).IsEqualTo("Work");
        await Assert.That(Path.GetFileName(result.Profile.Dir)).IsEqualTo("Work");
    }

    [Test]
    public async Task Rename_rejects_a_name_another_profile_holds()
    {
        var profile = ProfileStore.Create("work");
        ProfileStore.Create("personal");

        await Assert
            .That(() => ProfileStore.Rename(profile, "personal"))
            .Throws<InvalidOperationException>();
        await Assert
            .That(() => ProfileStore.Rename(profile, "PERSONAL"))
            .Throws<InvalidOperationException>();
        await Assert.That(Directory.Exists(profile.Dir)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("..")]
    [Arguments(".hidden")]
    [Arguments("a/b")]
    [Arguments("CON")]
    public async Task Rename_rejects_a_name_that_would_not_travel(string name)
    {
        var profile = ProfileStore.Create("work");

        await Assert.That(() => ProfileStore.Rename(profile, name)).Throws<ArgumentException>();
        await Assert.That(Names()).IsEqualTo("work");
    }

    [Test]
    public async Task Rename_carries_last_used_over()
    {
        var profile = ProfileStore.Create("work");
        ProfileStore.SetLastUsed("work");

        ProfileStore.Rename(profile, "day-job");

        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("day-job");
    }

    [Test]
    public async Task Rename_leaves_a_last_used_naming_another_profile_alone()
    {
        var profile = ProfileStore.Create("work");
        ProfileStore.Create("personal");
        ProfileStore.SetLastUsed("personal");

        ProfileStore.Rename(profile, "day-job");

        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("personal");
    }

    /// <summary>The links hold the old path, which the move has just made disappear.</summary>
    [Test]
    public async Task Rename_repoints_the_links_at_the_new_directory()
    {
        var profile = ProfileStore.Create("work");
        ProfileStore.Relink(profile);

        var result = ProfileStore.Rename(profile, "day-job");

        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, result.Profile.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, result.Profile.ClaudeJsonFile)).IsTrue();
    }

    [Test]
    public async Task Rename_leaves_the_links_alone_when_another_profile_holds_them()
    {
        var linked = ProfileStore.Create("linked");
        var other = ProfileStore.Create("other");
        ProfileStore.Relink(linked);

        ProfileStore.Rename(other, "renamed");

        await Assert.That(Links.PointsAt(Paths.ClaudeDir, linked.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, linked.ClaudeJsonFile)).IsTrue();
    }

    /// <summary>A migrate-installer install travels with the profile, so repointing at the new path
    /// is exactly what keeps the 'claude' command working.</summary>
    [Test]
    public async Task Rename_repoints_a_profile_holding_a_local_install()
    {
        var profile = ProfileStore.Create("work");
        Directory.CreateDirectory(Path.Combine(profile.Dir, "local"));
        ProfileStore.Relink(profile);

        var result = ProfileStore.Rename(profile, "day-job");

        await Assert.That(result.Warnings).IsEmpty();
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, result.Profile.Dir)).IsTrue();
    }

    [Test]
    public async Task Delete_removes_the_directory()
    {
        var profile = ProfileStore.Create("work");
        File.WriteAllText(Path.Combine(profile.Dir, ".credentials.json"), "{}");

        var warnings = ProfileStore.Delete(profile);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(Directory.Exists(profile.Dir)).IsFalse();
    }

    [Test]
    public async Task Delete_clears_a_last_used_naming_it()
    {
        var profile = ProfileStore.Create("work");
        ProfileStore.SetLastUsed("work");

        ProfileStore.Delete(profile);

        await Assert.That(ProfileStore.LastUsed()).IsNull();
    }

    [Test]
    public async Task Delete_repoints_the_links_at_a_survivor()
    {
        var keep = ProfileStore.Create("keep");
        var doomed = ProfileStore.Create("doomed");
        ProfileStore.SetLastUsed("doomed");
        ProfileStore.Relink(doomed);

        ProfileStore.Delete(doomed);

        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("keep");
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, keep.Dir)).IsTrue();
        await Assert.That(Links.PointsAt(Paths.ClaudeJson, keep.ClaudeJsonFile)).IsTrue();
    }

    [Test]
    public async Task Delete_leaves_the_links_alone_when_another_profile_holds_them()
    {
        var linked = ProfileStore.Create("linked");
        var doomed = ProfileStore.Create("doomed");
        ProfileStore.Relink(linked);
        ProfileStore.SetLastUsed("linked");

        ProfileStore.Delete(doomed);

        await Assert.That(ProfileStore.LastUsed()).IsEqualTo("linked");
        await Assert.That(Links.PointsAt(Paths.ClaudeDir, linked.Dir)).IsTrue();
    }

    /// <summary>Nothing left to point at, and an absent ~/.claude is the honest state.</summary>
    [Test]
    public async Task Delete_of_the_last_profile_drops_the_links()
    {
        var only = ProfileStore.Create("only");
        ProfileStore.SetLastUsed("only");
        ProfileStore.Relink(only);

        var warnings = ProfileStore.Delete(only);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(Path.Exists(Paths.ClaudeDir)).IsFalse();
        await Assert.That(Links.IsLink(Paths.ClaudeJson)).IsFalse();
        await Assert.That(ProfileStore.LastUsed()).IsNull();
    }

    [Test]
    public async Task AcquireLock_hands_out_one_holder_at_a_time()
    {
        using (var guard = ProfileStore.AcquireLock())
        {
            await Assert.That(guard).IsNotNull();
            await Assert.That(ProfileStore.AcquireLock()).IsNull();
        }

        using var after = ProfileStore.AcquireLock();
        await Assert.That(after).IsNotNull();
    }

    private static string Names() => string.Join(",", ProfileStore.List().Select(p => p.Name));

    /// <summary>The saved order as one string - the order is the assertion, so comparing it as a
    /// set the way a collection assertion would is exactly the wrong thing.</summary>
    private static string Ordered() => string.Join(",", ProfileStore.Order());

    private static void MakeProfileDir(string name) =>
        Directory.CreateDirectory(Path.Combine(Paths.ProfilesRoot, name));

    private static void WriteOrder(string content)
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllText(Paths.OrderFile, content);
    }
}
