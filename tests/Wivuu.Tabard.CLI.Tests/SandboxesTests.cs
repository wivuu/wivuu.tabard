using System.Diagnostics;

namespace Wivuu.Tabard.Cli.Tests;

[NotInParallel]
public class SandboxesTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    [Test]
    public async Task A_profile_uses_whichever_kind_is_installed_unless_it_picks_one()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var path = new OnlyOnPath();
        var profile = ProfileStore.Create("work");

        // Neither installed: srt, which is what the install hint offers first.
        await Assert.That(Sandboxes.For(profile)).IsTypeOf<SrtSandbox>();

        using (new FakeSbx())
        {
            await Assert.That(Sandboxes.For(profile)).IsTypeOf<SbxSandbox>();

            using (new FakeSrt())
                await Assert.That(Sandboxes.For(profile)).IsTypeOf<SrtSandbox>();
        }

        // A choice holds whatever is installed.
        SandboxOptions.SetKind("work", "SBX");
        await Assert.That(Sandboxes.For(profile)).IsTypeOf<SbxSandbox>();
        await Assert.That(SandboxOptions.KindOf("work")).IsEqualTo("sbx");

        SandboxOptions.SetKind("work", "srt");
        await Assert.That(SandboxOptions.KindOf("work")).IsEqualTo("srt");

        SandboxOptions.SetKind("work", null);
        await Assert.That(SandboxOptions.KindOf("work")).IsNull();

        await Assert.That(Sandboxes.Named("firecracker")).IsNull();
        await Assert
            .That(() => SandboxOptions.SetKind("work", "firecracker"))
            .Throws<ArgumentException>();
    }

    /// <summary>Switching sandboxing on with nothing to run it says what to install, and how.
    /// </summary>
    [Test]
    public async Task Sandboxing_cannot_be_switched_on_with_nothing_installed()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var path = new OnlyOnPath();
        ProfileStore.Create("work");

        var refusal = Sandboxes.Refusal("work");
        await Assert.That(refusal).Contains("npm install -g @anthropic-ai/sandbox-runtime");
        await Assert.That(refusal).Contains("brew install docker/tap/sbx");

        await Assert.That(global::Cli.Run(["sandbox", "on", "work"])).IsEqualTo(1);
        await Assert.That(Sandboxing.IsOn("work")).IsFalse();

        // A kind picked but not installed names that one.
        SandboxOptions.SetKind("work", "sbx");
        using (new FakeSrt())
        {
            await Assert.That(Sandboxes.Refusal("work")).Contains("brew install docker/tap/sbx");
            await Assert.That(Sandboxes.Refusal("work")).DoesNotContain("npm install");
        }

        using (new FakeSbx())
        {
            await Assert.That(Sandboxes.Refusal("work")).IsNull();
            await Assert.That(global::Cli.Run(["sandbox", "on", "work"])).IsEqualTo(0);
            await Assert.That(Sandboxing.IsOn("work")).IsTrue();
        }
    }

    /// <summary>Only a server on this machine needs bridging into a sandbox; one elsewhere is
    /// reached the same way from inside as from out.</summary>
    [Test]
    [Arguments("8080", 8080)]
    [Arguments("127.0.0.1:11434", 11434)]
    [Arguments("[::1]:9000", 9000)]
    [Arguments("gpu-box:8080", null)]
    public async Task HostPorts_are_the_local_port_the_launch_waits_for(string address, int? port)
    {
        var profile = ProfileStore.Create("local");
        using (ProfileStore.AcquireLock())
            Companions.SetWaitFor("local", address);

        var expected = port is { } p ? new[] { p } : [];
        await Assert.That(Sandboxes.HostPorts(profile)).IsEquivalentTo(expected);
    }

    [Test]
    public async Task HostPorts_are_empty_without_a_port_to_wait_for()
    {
        await Assert.That(Sandboxes.HostPorts(ProfileStore.Create("local"))).IsEmpty();
    }

    /// <summary>Extensions are code pi runs on the host the next time the profile launches
    /// directly, so a sandbox must not be able to create one where there was none.</summary>
    [Test]
    public async Task A_pi_profile_holds_its_code_and_instructions_out_of_reach()
    {
        var profile = ProfileStore.Create("local");
        File.WriteAllText(Path.Combine(profile.Dir, "models.json"), "{}");
        File.WriteAllText(Path.Combine(profile.Dir, "auth.json"), "{}");

        Sandboxing.Prepare(profile.Dir, Harness.Pi);
        var dir = Path.GetFullPath(profile.Dir);
        var paths = Sandboxing.ReadOnlyPaths(dir, Harness.Pi);

        foreach (var entry in new[] { "settings.json", "models.json", "extensions", "skills" })
            await Assert.That(paths).Contains(Path.Combine(dir, entry));

        await Assert.That(paths).DoesNotContain(Path.Combine(dir, "auth.json"));
    }

    [Test]
    public async Task PiRunArgs_create_a_shell_sandbox_from_the_template()
    {
        var profile = ProfileStore.Create("local");
        Sandboxing.Prepare(profile.Dir, Harness.Pi);
        var dir = Path.GetFullPath(profile.Dir);

        var args = Sbx.PiRunArgs(
            "tabard-local-x",
            profile.Dir,
            new Workspace("/w", "/w", [], []),
            exists: false,
            ["-c"],
            "tabard-pi:1.6.0",
            [8080, 11434],
            ["GIT_AUTHOR_NAME=A"]
        );

        await Assert
            .That(args.Take(8))
            .IsEquivalentTo(
                new[]
                {
                    "run",
                    "shell",
                    "--name",
                    "tabard-local-x",
                    "--pull",
                    "never",
                    "-t",
                    "tabard-pi:1.6.0",
                }
            );
        await Assert.That(args).Contains($"PI_CODING_AGENT_DIR={dir}");
        await Assert.That(args).Contains("TABARD_FORWARD=8080 11434");
        await Assert.That(args).Contains("GIT_AUTHOR_NAME=A");
        await Assert.That(args).Contains($"{Path.Combine(dir, "extensions")}:ro");
        await Assert
            .That(args.TakeLast(5))
            .IsEquivalentTo(new[] { "--", "-c", Sbx.PiScript, "tabard-pi", "-c" });
    }

    [Test]
    public async Task PiRunArgs_go_back_into_a_sandbox_by_name_alone()
    {
        var args = Sbx.PiRunArgs(
            "tabard-local-x",
            "/p/local",
            new Workspace("/w", "/w", [], []),
            exists: true,
            [],
            "tabard-pi:1.6.0",
            []
        );

        await Assert.That(args.Take(3)).IsEquivalentTo(new[] { "run", "--name", "tabard-local-x" });
        await Assert.That(args).DoesNotContain("-t").And.DoesNotContain("/w");
        await Assert.That(args).Contains("TABARD_FORWARD=");
    }

    [Test]
    [Arguments("1.6.0", "1.6.0")]
    [Arguments("(node:1) ExperimentalWarning: x\n1.6.0\n", "1.6.0")]
    [Arguments("pi version one", null)]
    [Arguments("", null)]
    public async Task ParseVersion_finds_the_version_line(string output, string? expected)
    {
        await Assert.That(Sbx.ParseVersion(output)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""[{"repository":"docker.io/library/tabard-pi","tag":"1.6.0"}]""", true)]
    [Arguments("""[{"repository":"tabard-pi","tag":"1.6.0"}]""", true)]
    [Arguments("""[{"repository":"docker.io/library/tabard-pi","tag":"1.5.0"}]""", false)]
    [Arguments("""[{"repository":"docker.io/library/not-tabard-pi","tag":"1.6.0"}]""", false)]
    [Arguments("not json", false)]
    public async Task TemplateListed_matches_the_tag_however_sbx_spells_it(string json, bool listed)
    {
        await Assert.That(Sbx.TemplateListed(json, "tabard-pi:1.6.0")).IsEqualTo(listed);
    }

    /// <summary>The first sandboxed pi launch builds the template from a builder sandbox, removes
    /// the builder, and only then launches - from the template, under the shell agent.</summary>
    [Test]
    public async Task A_sandboxed_pi_launch_builds_the_template_first()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var pi = new FakeClaude("pi", "PI_CODING_AGENT_DIR");
        using var sbx = new FakeSbx();
        var profile = ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        Sandboxing.Set("local", true);
        SandboxOptions.SetKind("local", "sbx");

        await Assert.That(global::Cli.Run(["use", "local", "--", "-c"])).IsEqualTo(0);

        // The fake pi prints no version, so the template is tagged 'latest'.
        var runs = sbx.Runs.Select(r => string.Join(' ', r.Take(4))).ToList();
        await Assert.That(runs[0]).StartsWith("--no-input run -d --name");
        await Assert.That(runs[1]).StartsWith("--no-input exec -u root");
        await Assert
            .That(sbx.Runs.Any(r => r is ["--no-input", "template", "save", _, "tabard-pi:latest"]))
            .IsTrue();
        await Assert.That(sbx.Runs.Any(r => r is ["rm", "-f", _])).IsTrue();

        // sbx only saves a stopped sandbox, so the stop has to come first.
        var stop = sbx.Runs.ToList().FindIndex(r => r is ["--no-input", "stop", _]);
        var save = sbx.Runs.ToList().FindIndex(r => r is ["--no-input", "template", "save", ..]);
        await Assert.That(stop).IsGreaterThanOrEqualTo(0).And.IsLessThan(save);

        var launch = sbx.Runs[^1];
        await Assert.That(launch.Take(2)).IsEquivalentTo(new[] { "run", "shell" });
        await Assert.That(launch).Contains("tabard-pi:latest");
        await Assert.That(launch).Contains($"PI_CODING_AGENT_DIR={Path.GetFullPath(profile.Dir)}");
        await Assert.That(launch[^1]).IsEqualTo("-c");

        // pi on the host was only asked its version - nothing ran outside the sandbox.
        await Assert.That(pi.Launches.Select(l => l.Args)).IsEquivalentTo(new[] { "--version" });
    }

    [Test]
    public async Task A_built_template_is_used_without_building_again()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var pi = new FakeClaude("pi", "PI_CODING_AGENT_DIR");
        using var sbx = new FakeSbx();
        sbx.Template("docker.io/library/tabard-pi", "latest");
        ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        Sandboxing.Set("local", true);
        SandboxOptions.SetKind("local", "sbx");

        await Assert.That(global::Cli.Run(["use", "local"])).IsEqualTo(0);

        await Assert.That(sbx.Runs.Count).IsEqualTo(1);
        await Assert.That(sbx.Runs[0].Take(2)).IsEquivalentTo(new[] { "run", "shell" });
    }

    /// <summary>
    /// Runs the script the shell agent gets, here, with a stand-in pi: the forwarder has to come up
    /// on 127.0.0.1 - which proves the Python inside it parses - pi's exit status has to come back
    /// out, and the forwarder has to be gone afterwards.
    /// </summary>
    [Test]
    public async Task The_pi_script_forwards_runs_pi_and_cleans_up()
    {
        if (OperatingSystem.IsWindows() || Launcher.Which("python3") is null)
            return;

        var bin = Sandbox.Scratch("script-bin");
        var fakePi = Path.Combine(bin, "pi");
        File.WriteAllText(
            fakePi,
            """
            #!/bin/sh
            for i in $(seq 1 50); do
              python3 -c "import socket,sys; socket.create_connection(('127.0.0.1', int(sys.argv[1])), 1)" "$TABARD_FORWARD" 2>/dev/null && { echo "args:$*"; exit 7; }
              sleep 0.1
            done
            echo "forwarder never came up"; exit 1
            """
        );
        File.SetUnixFileMode(
            fakePi,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );

        var port = FreePort();
        var psi = new ProcessStartInfo("/bin/bash")
        {
            ArgumentList = { "-c", Sbx.PiScript, "tabard-pi", "--model", "x" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
        psi.Environment["TABARD_FORWARD"] = port.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );

        using var script = Process.Start(psi)!;
        var output = await script.StandardOutput.ReadToEndAsync();
        await script.WaitForExitAsync();

        await Assert.That(output.Trim()).IsEqualTo("args:--model x");
        await Assert.That(script.ExitCode).IsEqualTo(7);

        await Task.Delay(300);
        using var probe = new System.Net.Sockets.TcpClient();
        await Assert
            .That(async () => await probe.ConnectAsync("127.0.0.1", port))
            .ThrowsException();
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
