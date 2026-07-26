namespace Wivuu.Tabard.Cli.Tests;

public class PathsTests
{
    [Test]
    public async Task Home_is_the_sandbox()
    {
        // Guards the whole suite: if this fails, every ProfileStore test is writing to the real
        // home directory rather than the sandbox.
        await Assert.That(Paths.Home).IsEqualTo(Sandbox.Home);
    }

    [Test]
    public async Task Roots_hang_off_home()
    {
        await Assert.That(Paths.TabardRoot).IsEqualTo(Path.Combine(Sandbox.Home, ".tabard"));
        await Assert
            .That(Paths.ProfilesRoot)
            .IsEqualTo(Path.Combine(Sandbox.Home, ".tabard", "profiles"));
        await Assert
            .That(Paths.LastUsedFile)
            .IsEqualTo(Path.Combine(Sandbox.Home, ".tabard", "last"));
        await Assert.That(Paths.LockFile).IsEqualTo(Path.Combine(Sandbox.Home, ".tabard", "lock"));
        await Assert
            .That(Paths.OrderFile)
            .IsEqualTo(Path.Combine(Sandbox.Home, ".tabard", "order"));
    }

    [Test]
    public async Task Claude_paths_sit_beside_each_other_in_home()
    {
        await Assert.That(Paths.ClaudeDir).IsEqualTo(Path.Combine(Sandbox.Home, ".claude"));
        await Assert.That(Paths.ClaudeJson).IsEqualTo(Path.Combine(Sandbox.Home, ".claude.json"));
    }
}
