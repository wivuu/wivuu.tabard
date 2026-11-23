using System.Diagnostics;

namespace Wivuu.Tabard.Cli.Tests;

[NotInParallel]
public class CompanionsTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    [Test]
    public async Task Set_round_trips_and_blank_clears_it()
    {
        await Assert.That(Companions.Of("local")).IsNull();

        using (ProfileStore.AcquireLock())
            Companions.Set("local", "  strata serve --port 8080 ");

        await Assert.That(Companions.Of("local")).IsEqualTo("strata serve --port 8080");

        using (ProfileStore.AcquireLock())
            Companions.Set("local", "   ");

        await Assert.That(Companions.Of("local")).IsNull();
    }

    [Test]
    public async Task A_command_must_fit_on_one_line()
    {
        await Assert
            .That(() => Companions.Set("local", "strata serve\nrm -rf ~"))
            .Throws<ArgumentException>();
        await Assert.That(File.Exists(Paths.CompanionsFile)).IsFalse();
    }

    /// <summary>Unreadable means start nothing, the way every profile began.</summary>
    [Test]
    public async Task A_broken_file_starts_nothing()
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllText(Paths.CompanionsFile, "{ not json");

        await Assert.That(Companions.Of("local")).IsNull();
    }

    [Test]
    public async Task Rename_and_delete_carry_the_command()
    {
        var profile = ProfileStore.Create("local");
        using (ProfileStore.AcquireLock())
            Companions.Set("local", "strata serve");

        var renamed = ProfileStore.Rename(profile, "strata").Profile;
        await Assert.That(Companions.Of("strata")).IsEqualTo("strata serve");
        await Assert.That(Companions.Of("local")).IsNull();

        ProfileStore.Delete(renamed);
        await Assert.That(Companions.Of("strata")).IsNull();
    }

    [Test]
    public async Task Start_returns_nothing_for_a_profile_without_one()
    {
        var profile = ProfileStore.Create("local");

        await Assert.That(Companions.Start(profile)).IsNull();
    }

    /// <summary>
    /// The command runs under a shell that need not exec it, so stopping only the shell would leave
    /// the real server running. This one starts a grandchild on purpose and checks both go.
    /// </summary>
    [Test]
    public async Task Stopping_takes_down_everything_it_started()
    {
        if (OperatingSystem.IsWindows())
            return;

        var profile = ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);

        var pids = Path.Combine(Sandbox.Scratch("companion"), "pids");
        using (ProfileStore.AcquireLock())
            Companions.Set(
                "local",
                $"sleep 300 & echo \"$$ $!\" > '{pids}'; echo \"for $TABARD_PROFILE in $PI_CODING_AGENT_DIR\"; wait"
            );

        var running = Companions.Start(profile);
        await Assert.That(running).IsNotNull();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(pids) || File.ReadAllText(pids).Trim().Split(' ').Length < 2)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the companion never wrote its pids");

            await Task.Delay(50);
        }

        var started = File.ReadAllText(pids).Trim().Split(' ').Select(int.Parse).ToArray();
        await Assert.That(started.All(Alive)).IsTrue();

        running!.Dispose();

        await Assert.That(started.Any(Alive)).IsFalse();

        var log = File.ReadAllText(Companions.LogFile("local"));
        await Assert.That(log).Contains($"for local in {Path.GetFullPath(profile.Dir)}");
        await Assert.That(log).Contains("tabard stopped it");
    }

    /// <summary>Launching starts it before the agent and stops it once the agent has gone.</summary>
    [Test]
    public async Task Launch_starts_and_stops_it_around_the_agent()
    {
        var profile = ProfileStore.Create("work");
        using (ProfileStore.AcquireLock())
            Companions.Set("work", OperatingSystem.IsWindows() ? "echo up" : "echo up; sleep 300");

        using var claude = new FakeClaude();
        var code = Launcher.Launch(profile, ["--resume"], sandboxed: false);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(claude.Launches).Count().IsEqualTo(1);

        var log = File.ReadAllText(Companions.LogFile("work"));
        await Assert.That(log).Contains("tabard started");
        await Assert.That(log).Contains("tabard stopped it");
    }

    [Test]
    [Arguments("8080", "localhost", 8080)]
    [Arguments("127.0.0.1:11434", "127.0.0.1", 11434)]
    [Arguments("[::1]:9000", "::1", 9000)]
    [Arguments("gpu-box:1", "gpu-box", 1)]
    public async Task ParseAddress_reads_a_port_or_host_and_port(
        string address,
        string host,
        int port
    )
    {
        await Assert.That(Companions.ParseAddress(address)).IsEqualTo((host, port));
    }

    [Test]
    [Arguments("")]
    [Arguments("http://localhost:8080")]
    [Arguments("localhost")]
    [Arguments("0")]
    [Arguments("70000")]
    [Arguments(":8080")]
    [Arguments("+80")]
    public async Task ParseAddress_refuses_anything_else(string address)
    {
        await Assert.That(() => Companions.ParseAddress(address)).Throws<ArgumentException>();
    }

    /// <summary>Each half stands alone: a profile can wait on a server it does not start, and
    /// clearing both leaves no entry behind.</summary>
    [Test]
    public async Task The_command_and_the_port_are_kept_separately()
    {
        using (ProfileStore.AcquireLock())
        {
            Companions.SetWaitFor("local", "8080");
            Companions.Set("local", "strata serve");
        }

        await Assert.That(Companions.Of("local")).IsEqualTo("strata serve");
        await Assert.That(Companions.WaitForOf("local")).IsEqualTo("8080");

        using (ProfileStore.AcquireLock())
            Companions.Set("local", null);

        await Assert.That(Companions.Of("local")).IsNull();
        await Assert.That(Companions.WaitForOf("local")).IsEqualTo("8080");

        using (ProfileStore.AcquireLock())
            Companions.SetWaitFor("local", "");

        await Assert.That(File.ReadAllText(Paths.CompanionsFile).Trim()).IsEqualTo("{}");
        await Assert.That(() => Companions.SetWaitFor("local", "soon")).Throws<ArgumentException>();
    }

    [Test]
    public async Task WaitFor_holds_the_launch_until_the_port_answers()
    {
        var profile = ProfileStore.Create("local");
        var port = FreePort();
        using (ProfileStore.AcquireLock())
            Companions.SetWaitFor("local", $"127.0.0.1:{port}");

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        var opened = Task.Delay(700).ContinueWith(_ => listener.Start());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var refused = Companions.WaitFor(profile, running: null);
        await opened;
        listener.Stop();

        await Assert.That(refused).IsNull();
        await Assert.That(clock.Elapsed).IsGreaterThan(TimeSpan.FromMilliseconds(600));
    }

    /// <summary>Nothing is coming up to answer once the command has gone, and launching anyway
    /// would draw the agent over the reason.</summary>
    [Test]
    public async Task WaitFor_refuses_to_launch_when_the_command_alongside_exits()
    {
        var profile = ProfileStore.Create("local");
        using (ProfileStore.AcquireLock())
        {
            Companions.Set("local", "echo missing --profile >&2; exit 3");
            Companions.SetWaitFor("local", $"127.0.0.1:{FreePort()}");
        }

        using var running = Companions.Start(profile);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.That(Companions.WaitFor(profile, running)).IsEqualTo(1);
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(running!.Tail(15)).IsEquivalentTo(["missing --profile"]);
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
