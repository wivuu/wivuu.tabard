namespace Wivuu.Tabard.Cli.Tests;

public class LinksTests
{
    [Test]
    public async Task IsLink_tells_links_from_real_content()
    {
        var scratch = Sandbox.Scratch();
        var real = Path.Combine(scratch, "real");
        var link = Path.Combine(scratch, "link");

        Directory.CreateDirectory(real);
        await Assert.That(Links.TryCreateDirectoryLink(link, real, out _)).IsTrue();

        await Assert.That(Links.IsLink(link)).IsTrue();
        await Assert.That(Links.IsLink(real)).IsFalse();
        await Assert.That(Links.IsLink(Path.Combine(scratch, "nothing"))).IsFalse();
    }

    [Test]
    public async Task ResolveTarget_returns_null_for_real_content()
    {
        var scratch = Sandbox.Scratch();
        var real = Path.Combine(scratch, "real");
        Directory.CreateDirectory(real);

        await Assert.That(Links.ResolveTarget(real)).IsNull();
        await Assert.That(Links.ResolveTarget(Path.Combine(scratch, "nothing"))).IsNull();
    }

    [Test]
    public async Task ResolveTarget_absolutises_a_relative_target()
    {
        var scratch = Sandbox.Scratch();
        var target = Path.Combine(scratch, "target");
        var link = Path.Combine(scratch, "link");

        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, "target");

        await Assert.That(Links.ResolveTarget(link)).IsEqualTo(target);
    }

    /// <summary>
    /// ~/.claude.json is linked before Claude Code has written it, so a dangling link has to keep
    /// reporting its target rather than looking like real content.
    /// </summary>
    [Test]
    public async Task ResolveTarget_still_works_when_the_target_does_not_exist()
    {
        var scratch = Sandbox.Scratch();
        var missing = Path.Combine(scratch, "not-written-yet.json");
        var link = Path.Combine(scratch, "link.json");

        await Assert.That(Links.TryCreateFileLink(link, missing, out _)).IsTrue();

        await Assert.That(Links.IsLink(link)).IsTrue();
        await Assert.That(Links.ResolveTarget(link)).IsEqualTo(missing);
        await Assert.That(Links.PointsAt(link, missing)).IsTrue();
    }

    [Test]
    public async Task PointsAt_is_false_for_a_different_target_and_for_real_content()
    {
        var scratch = Sandbox.Scratch();
        var a = Path.Combine(scratch, "a");
        var b = Path.Combine(scratch, "b");
        var link = Path.Combine(scratch, "link");

        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        Directory.CreateSymbolicLink(link, a);

        await Assert.That(Links.PointsAt(link, a)).IsTrue();
        await Assert.That(Links.PointsAt(link, b)).IsFalse();
        await Assert.That(Links.PointsAt(a, a)).IsFalse();
    }

    [Test]
    public async Task PointsAt_ignores_a_trailing_separator()
    {
        var scratch = Sandbox.Scratch();
        var target = Path.Combine(scratch, "target");
        var link = Path.Combine(scratch, "link");

        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target + Path.DirectorySeparatorChar);

        await Assert.That(Links.PointsAt(link, target)).IsTrue();
        await Assert.That(Links.PointsAt(link, target + Path.DirectorySeparatorChar)).IsTrue();
    }

    [Test]
    public async Task IsInside_covers_the_root_itself_and_its_children()
    {
        var root = Path.Combine(Path.GetTempPath(), "tabard-inside");

        await Assert.That(Links.IsInside(root, root)).IsTrue();
        await Assert.That(Links.IsInside(Path.Combine(root, "work"), root)).IsTrue();
        await Assert.That(Links.IsInside(Path.Combine(root, "work", "local"), root)).IsTrue();
    }

    /// <summary>
    /// A sibling sharing the root's name as a prefix must not classify as ours, or Relink would
    /// treat someone else's directory as part of the profile store.
    /// </summary>
    [Test]
    public async Task IsInside_does_not_match_a_name_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "tabard-inside");

        await Assert.That(Links.IsInside(root + "-other", root)).IsFalse();
        await Assert.That(Links.IsInside(Path.GetTempPath(), root)).IsFalse();
    }

    [Test]
    public async Task Unlink_removes_the_link_and_leaves_the_target_intact()
    {
        var scratch = Sandbox.Scratch();
        var target = Path.Combine(scratch, "target");
        var file = Path.Combine(target, "credentials.json");
        var link = Path.Combine(scratch, "link");

        Directory.CreateDirectory(target);
        File.WriteAllText(file, "secret");
        Directory.CreateSymbolicLink(link, target);

        Links.Unlink(link);

        await Assert.That(Path.Exists(link)).IsFalse();
        await Assert.That(Directory.Exists(target)).IsTrue();
        await Assert.That(File.ReadAllText(file)).IsEqualTo("secret");
    }

    /// <summary>Deleting through a link would take a profile's login with it.</summary>
    [Test]
    public async Task Unlink_does_nothing_to_real_content()
    {
        var scratch = Sandbox.Scratch();
        var real = Path.Combine(scratch, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "keep.txt"), "keep");

        Links.Unlink(real);

        await Assert.That(Directory.Exists(real)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(real, "keep.txt"))).IsTrue();
    }

    [Test]
    public async Task Unlink_tolerates_a_path_that_is_not_there()
    {
        var scratch = Sandbox.Scratch();
        Links.Unlink(Path.Combine(scratch, "nothing"));

        await Assert.That(Path.Exists(Path.Combine(scratch, "nothing"))).IsFalse();
    }

    [Test]
    public async Task Creating_a_link_over_something_that_exists_fails_softly()
    {
        var scratch = Sandbox.Scratch();
        var target = Path.Combine(scratch, "target");
        var occupied = Path.Combine(scratch, "occupied");

        Directory.CreateDirectory(target);
        Directory.CreateDirectory(occupied);

        var created = Links.TryCreateDirectoryLink(occupied, target, out var error);

        await Assert.That(created).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }
}
