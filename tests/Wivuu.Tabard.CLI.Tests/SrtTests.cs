using System.Diagnostics;

namespace Wivuu.Tabard.Cli.Tests;

[NotInParallel]
public class SrtTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    private static string ProfileDir(string name) =>
        Path.GetFullPath(Path.Combine(Paths.ProfilesRoot, name));

    /// <summary>The whole srt launch short of a real sandbox: the agent installed here, wrapped in
    /// srt with a policy that confines it to the folder and its profile.</summary>
    [Test]
    public async Task A_sandboxed_profile_launches_through_srt_by_default()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var claude = new FakeClaude();
        using var srt = new FakeSrt();
        var work = ProfileStore.Create("work");
        global::Cli.Run(["sandbox", "on", "work"]);

        await Assert.That(global::Cli.Run(["use", "work", "--", "--continue"])).IsEqualTo(0);

        // srt ran claude - the fake srt runs nothing, so claude itself never started.
        await Assert.That(claude.Launches).IsEmpty();
        await Assert.That(srt.Ran).IsTrue();

        var dir = Path.GetFullPath(work.Dir);
        var args = srt.Args;
        await Assert.That(args[0]).IsEqualTo("-s");
        await Assert.That(args[2]).IsEqualTo("--");
        await Assert.That(Path.GetFileName(args[3])).IsEqualTo("claude");
        await Assert.That(args[^1]).IsEqualTo("--continue");
        await Assert.That(srt.ConfigDir).IsEqualTo(dir);
        await Assert.That(srt.TempDir).IsEqualTo(SrtSandbox.TempDir("work"));

        // Written for the launch, gone after it.
        await Assert.That(File.Exists(args[1])).IsFalse();

        var root = Sandboxing.ResolveWorkspace(Directory.GetCurrentDirectory(), []).Root;
        await Assert.That(srt.List("filesystem", "allowWrite")).Contains(root);
        await Assert.That(srt.List("filesystem", "allowWrite")).Contains(dir);
        await Assert
            .That(srt.List("filesystem", "denyWrite"))
            .Contains(Path.Combine(dir, "settings.json"));
        await Assert
            .That(srt.List("filesystem", "denyRead"))
            .Contains(Path.GetFullPath(Paths.TabardRoot));
        await Assert.That(srt.List("filesystem", "allowRead")).IsEquivalentTo([dir]);
        await Assert.That(srt.List("network", "allowedDomains")).Contains("*.anthropic.com");
    }

    [Test]
    public async Task The_profiles_allowed_sites_are_added_to_its_agents_own()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var claude = new FakeClaude();
        using var srt = new FakeSrt();
        ProfileStore.Create("work");
        global::Cli.Run(["sandbox", "on", "work"]);
        await Assert
            .That(global::Cli.Run(["sandbox", "allow", "work", "github.com,", "*.npmjs.org"]))
            .IsEqualTo(0);

        await Assert.That(global::Cli.Run(["use", "work"])).IsEqualTo(0);

        var sites = srt.List("network", "allowedDomains");
        await Assert.That(sites).Contains("github.com");
        await Assert.That(sites).Contains("*.npmjs.org");
        await Assert.That(sites).Contains("anthropic.com");
        await Assert.That(sites).Contains("registry.npmjs.org");
    }

    /// <summary>A pi profile pointed at a server on this machine reaches it through srt's proxy,
    /// at that one port: the launch goes through the script that takes loopback out of NO_PROXY.
    /// </summary>
    [Test]
    public async Task A_pi_profile_reaches_its_local_server_through_the_proxy()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var pi = new FakeClaude("pi", "PI_CODING_AGENT_DIR");
        using var srt = new FakeSrt("PI_CODING_AGENT_DIR");
        var profile = ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        Sandboxing.Set("local", true);
        File.WriteAllText(
            Path.Combine(profile.Dir, "models.json"),
            """
            {
              "providers": {
                "strata": { "baseUrl": "http://127.0.0.1:8080/v1", "api": "openai-completions" },
                "remote": { "baseUrl": "https://gpu.example.com:8443/v1" }
              }
            }
            """
        );

        await Assert.That(global::Cli.Run(["use", "local", "--", "-c"])).IsEqualTo(0);

        // One argument per line in the fake's log, and the script spans several.
        var args = srt.Args.ToList();
        var name = args.IndexOf("tabard-srt");
        await Assert.That(args.Skip(3).Take(2)).IsEquivalentTo(new[] { "/bin/sh", "-c" });
        await Assert
            .That(string.Join('\n', args.Skip(5).Take(name - 5)))
            .IsEqualTo(SrtSandbox.LoopbackScript);
        await Assert.That(Path.GetFileName(args[name + 1])).IsEqualTo("pi");
        await Assert.That(args[^1]).IsEqualTo("-c");
        await Assert.That(srt.ConfigDir).IsEqualTo(Path.GetFullPath(profile.Dir));

        var sites = srt.List("network", "allowedDomains");
        await Assert.That(sites).Contains("127.0.0.1:8080");
        await Assert.That(sites).Contains("localhost:8080");
        await Assert.That(sites).Contains("gpu.example.com:8443");
        await Assert.That(sites).DoesNotContain("*.anthropic.com");

        // pi's protected files are read-only, as in a Docker sandbox.
        await Assert
            .That(srt.List("filesystem", "denyWrite"))
            .Contains(Path.Combine(Path.GetFullPath(profile.Dir), "models.json"));
    }

    /// <summary>Without a local port to reach, the agent starts directly under srt.</summary>
    [Test]
    public async Task Without_a_local_port_the_agent_is_not_wrapped_in_a_script()
    {
        var args = SrtSandbox.Args("/s.json", "/bin/claude", ["-p", "hi"], false);
        await Assert
            .That(args)
            .IsEquivalentTo(new[] { "-s", "/s.json", "--", "/bin/claude", "-p", "hi" });
    }

    [Test]
    public async Task The_loopback_script_keeps_everything_but_loopback_and_passes_arguments()
    {
        if (OperatingSystem.IsWindows())
            return;

        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            ArgumentList =
            {
                "-c",
                SrtSandbox.LoopbackScript,
                "tabard-srt",
                "/bin/sh",
                "-c",
                "printf '%s|%s|%s\\n' \"$NO_PROXY\" \"$no_proxy\" \"$1\"",
                "agent",
                "a *b",
            },
        };
        psi.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1,169.254.0.0/16,10.0.0.0/8";
        psi.Environment["no_proxy"] = "localhost,127.0.0.1";

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();

        await Assert.That(output).IsEqualTo("169.254.0.0/16,10.0.0.0/8||a *b");
    }

    [Test]
    public async Task Pi_sites_come_from_its_endpoints_and_its_logins()
    {
        var dir = Sandbox.Scratch();
        File.WriteAllText(
            Path.Combine(dir, "models.json"),
            """
            {
              // pi allows comments,
              "providers": {
                "a": { "baseUrl": "https://api.example.com/v1" },
                "b": { "baseUrl": "http://localhost:11434" },
                "c": { "baseUrl": "not a url" },
              }
            }
            """
        );
        File.WriteAllText(
            Path.Combine(dir, "settings.json"),
            """{ "defaultProvider": "openrouter" }"""
        );
        File.WriteAllText(Path.Combine(dir, "auth.json"), """{ "anthropic": { "key": "x" } }""");

        var endpoints = SrtSandbox.PiEndpoints(dir);
        await Assert.That(endpoints.Hosts).IsEquivalentTo(["api.example.com"]);
        await Assert.That(endpoints.LoopbackPorts).IsEquivalentTo([11434]);

        var sites = SrtSandbox.AgentSites(Harness.Pi, dir);
        await Assert.That(sites).Contains("api.example.com");
        await Assert.That(sites).Contains("openrouter.ai");
        await Assert.That(sites).Contains("*.anthropic.com");
    }

    [Test]
    public async Task A_broken_pi_config_still_launches_with_no_sites()
    {
        var dir = Sandbox.Scratch();
        File.WriteAllText(Path.Combine(dir, "models.json"), "{ nope");
        File.WriteAllText(Path.Combine(dir, "auth.json"), "[]");

        await Assert.That(SrtSandbox.AgentSites(Harness.Pi, dir)).IsEmpty();
    }

    /// <summary>An older Claude Code lives in ~/.claude/local; denying ~/.claude would stop it.
    /// </summary>
    [Test]
    public async Task A_credential_folder_holding_the_agent_stays_readable()
    {
        var home = Sandbox.Scratch();
        var workspace = new Workspace("/w", "/w", [], []);

        var plain = SrtSandbox.Policy(ProfileDir("w"), Harness.Claude, workspace, "/t", [], home);
        await Assert.That(plain.DenyRead).Contains(Path.Combine(home, ".claude"));
        await Assert.That(plain.DenyRead).Contains(Path.Combine(home, ".ssh"));

        var local = SrtSandbox.Policy(
            ProfileDir("w"),
            Harness.Claude,
            workspace,
            "/t",
            [],
            home,
            Path.Combine(home, ".claude", "local", "claude")
        );
        await Assert.That(local.DenyRead).DoesNotContain(Path.Combine(home, ".claude"));
        await Assert.That(local.DenyRead).Contains(Path.Combine(home, ".ssh"));
    }

    /// <summary>~/.claude and ~/.claude.json are tabard's links to the launched profile when it
    /// manages them, and Claude Code reads them; a real folder there is someone else's login.
    /// </summary>
    [Test]
    public async Task Tabards_own_links_in_home_are_not_hidden()
    {
        if (OperatingSystem.IsWindows())
            return;

        var home = Sandbox.Scratch();
        var profile = ProfileStore.Create("work");
        File.WriteAllText(Path.Combine(profile.Dir, ".claude.json"), "{}");
        File.CreateSymbolicLink(Path.Combine(home, ".claude"), Path.GetFullPath(profile.Dir));
        File.CreateSymbolicLink(
            Path.Combine(home, ".claude.json"),
            Path.Combine(Path.GetFullPath(profile.Dir), ".claude.json")
        );
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        var workspace = new Workspace("/w", "/w", [], []);

        var policy = SrtSandbox.Policy(
            ProfileDir("work"),
            Harness.Claude,
            workspace,
            "/t",
            [],
            home
        );

        await Assert.That(policy.DenyRead).DoesNotContain(Path.Combine(home, ".claude"));
        await Assert.That(policy.DenyRead).DoesNotContain(Path.Combine(home, ".claude.json"));
        await Assert.That(policy.DenyRead).Contains(Path.Combine(home, ".ssh"));
    }

    [Test]
    public async Task A_repositorys_hooks_and_config_are_not_writable()
    {
        var workspace = new Workspace("/w", "/w/sub", ["/elsewhere/.git"], ["/w/.git/hooks"]);
        var policy = SrtSandbox.Policy(ProfileDir("w"), Harness.Claude, workspace, "/t", [], "/h");

        await Assert.That(policy.AllowWrite).Contains("/w");
        await Assert.That(policy.AllowWrite).Contains("/elsewhere/.git");
        await Assert.That(policy.AllowWrite).Contains("/t");
        await Assert.That(policy.DenyWrite).Contains("/w/.git/hooks");
    }

    [Test]
    [Arguments("github.com", true)]
    [Arguments("*.npmjs.org", true)]
    [Arguments("localhost:3000", true)]
    [Arguments("10.0.0.5:8080", true)]
    [Arguments("https://github.com", false)]
    [Arguments("github.com/foo", false)]
    [Arguments("*", false)]
    [Arguments("a..b", false)]
    public async Task Allowed_sites_must_be_hosts(string site, bool ok)
    {
        if (ok)
            await Assert.That(SandboxOptions.ParseSites(site)).IsEquivalentTo([site]);
        else
            await Assert.That(() => SandboxOptions.ParseSites(site)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Allowed_sites_are_split_on_commas_and_spaces_once_each()
    {
        await Assert
            .That(SandboxOptions.ParseSites(" github.com, *.npmjs.org  GitHub.com,"))
            .IsEquivalentTo(["github.com", "*.npmjs.org"]);
        await Assert.That(SandboxOptions.ParseSites("  ")).IsEmpty();
    }

    /// <summary>A hand-edited file cannot slip srt something that is not a site, and one that
    /// cannot be read leaves defaults - still sandboxed, no extra site open.</summary>
    [Test]
    public async Task A_bad_options_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(Paths.TabardRoot);
        File.WriteAllText(
            Paths.SandboxOptionsFile,
            """{ "work": { "kind": "firecracker", "allow": ["ok.com", "http://no", 5] } }"""
        );

        await Assert.That(SandboxOptions.KindOf("work")).IsNull();
        await Assert.That(SandboxOptions.AllowedOf("work")).IsEquivalentTo(["ok.com"]);

        File.WriteAllText(Paths.SandboxOptionsFile, "{ nope");
        await Assert.That(SandboxOptions.AllowedOf("work")).IsEmpty();
    }

    /// <summary>The editor's Sandbox field: off, then each installed kind, then off - and
    /// switching off keeps the kind for the picker's 's' key.</summary>
    [Test]
    public async Task The_editor_cycles_through_the_installed_kinds()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var path = new OnlyOnPath();
        var profile = ProfileStore.Create("work");

        string? Step()
        {
            var (on, kind, refusal) = ProfileEditor.NextSandbox(profile);
            if (refusal is not null)
                return refusal;
            if (kind is not null)
                SandboxOptions.SetKind("work", kind.Name);
            Sandboxing.Set("work", on);
            return null;
        }

        string State() => Sandboxing.IsOn("work") ? Sandboxes.For(profile).Name : "off";

        // Nothing installed: it stays off and says what to install.
        await Assert.That(Step()).Contains("npm install -g");
        await Assert.That(State()).IsEqualTo("off");

        using (new FakeSbx())
        {
            Step();
            await Assert.That(State()).IsEqualTo("sbx");
            Step();
            await Assert.That(State()).IsEqualTo("off");

            using (new FakeSrt())
            {
                // sbx was the last kind used, and still the cycle starts from srt - otherwise a
                // profile left on sbx could never get back to it.
                await Assert.That(SandboxOptions.KindOf("work")).IsEqualTo("sbx");
                Step();
                await Assert.That(State()).IsEqualTo("srt");
                Step();
                await Assert.That(State()).IsEqualTo("sbx");
                Step();
                await Assert.That(State()).IsEqualTo("off");
                Step();
                await Assert.That(State()).IsEqualTo("srt");
            }
        }
    }

    /// <summary>An srt sandbox has sites to allow; a Docker one an image, except for pi, whose
    /// sandboxes come from tabard's template.</summary>
    [Test]
    public async Task The_editor_offers_what_the_kind_of_sandbox_takes()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var path = new OnlyOnPath();
        using var srt = new FakeSrt();
        var claude = ProfileStore.Create("work");
        var pi = ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        pi = ProfileStore.List().Single(p => p.Name == "local");

        await Assert.That(ProfileEditor.Fields(claude)).Contains(ProfileEditor.Field.Allow);
        await Assert.That(ProfileEditor.Fields(claude)).DoesNotContain(ProfileEditor.Field.Image);
        await Assert.That(ProfileEditor.Fields(pi)).Contains(ProfileEditor.Field.Allow);

        SandboxOptions.SetKind("work", "sbx");
        SandboxOptions.SetKind("local", "sbx");

        await Assert.That(ProfileEditor.Fields(claude)).Contains(ProfileEditor.Field.Image);
        await Assert.That(ProfileEditor.Fields(claude)).DoesNotContain(ProfileEditor.Field.Allow);
        await Assert
            .That(ProfileEditor.Fields(pi))
            .IsEquivalentTo([
                ProfileEditor.Field.Name,
                ProfileEditor.Field.Sandbox,
                ProfileEditor.Field.Companion,
                ProfileEditor.Field.WaitFor,
            ]);

        foreach (var profile in new[] { claude, pi })
            await Assert
                .That(ProfileEditor.Fields(profile).Count + 6)
                .IsLessThanOrEqualTo(ProfileEditor.Height(profile));
    }

    [Test]
    public async Task Sandbox_kind_and_allow_set_a_profiles_options()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var path = new OnlyOnPath();
        using var srt = new FakeSrt();
        using var sbx = new FakeSbx();
        ProfileStore.Create("work");
        ProfileStore.Create("home");
        global::Cli.Run(["sandbox", "on", "work"]);
        global::Cli.Run(["sandbox", "on", "home"]);

        await Assert.That(global::Cli.Run(["sandbox", "kind", "home", "sbx"])).IsEqualTo(0);
        await Assert.That(global::Cli.Run(["sandbox", "allow", "work", "github.com"])).IsEqualTo(0);
        await Assert.That(global::Cli.Run(["sandbox"])).IsEqualTo(0);
        await Assert.That(global::Cli.Run(["sandbox", "kind", "work"])).IsEqualTo(0);

        await Assert.That(Sandboxes.For("home")).IsTypeOf<SbxSandbox>();
        await Assert.That(Sandboxes.For("work")).IsTypeOf<SrtSandbox>();
        await Assert.That(SandboxOptions.AllowedOf("work")).IsEquivalentTo(["github.com"]);

        await Assert.That(global::Cli.Run(["sandbox", "allow", "work", "--none"])).IsEqualTo(0);
        await Assert.That(SandboxOptions.AllowedOf("work")).IsEmpty();

        await Assert
            .That(() => global::Cli.Run(["sandbox", "kind", "work", "firecracker"]))
            .Throws<ArgumentException>();
    }

    /// <summary>The Network screen's list: the agent's own sites first and fixed, then the
    /// profile's additions, then the developer defaults, each once.</summary>
    [Test]
    public async Task The_network_lists_each_site_once_with_where_it_comes_from()
    {
        var profile = ProfileStore.Create("work");
        using (ProfileStore.AcquireLock())
        {
            SandboxOptions.SetAllowed("work", "example.com github.com");
            SandboxOptions.SetBlocked("work", "pypi.org", true);
            Companions.SetWaitFor("work", "8080");
        }

        var entries = SrtSandbox.Network(profile, Sandboxes.HostPorts(profile));

        await Assert
            .That(entries.Select(e => e.Site))
            .IsEquivalentTo(entries.Select(e => e.Site).Distinct());
        await Assert.That(entries[0].Source).IsEqualTo(SrtSandbox.Source.Agent);
        await Assert
            .That(entries.Single(e => e.Site == "localhost:8080").Source)
            .IsEqualTo(SrtSandbox.Source.Agent);
        await Assert
            .That(entries.Single(e => e.Site == "example.com").Source)
            .IsEqualTo(SrtSandbox.Source.Added);

        // Added and a default both: listed once, as added, so removing it leaves the default.
        await Assert
            .That(entries.Single(e => e.Site == "github.com").Source)
            .IsEqualTo(SrtSandbox.Source.Added);

        var pypi = entries.Single(e => e.Site == "pypi.org");
        await Assert.That(pypi.Source).IsEqualTo(SrtSandbox.Source.Developer);
        await Assert.That(pypi.Allowed).IsFalse();

        await Assert.That(ProfileEditor.NetworkSummary(profile)).Contains("2 added, 1 blocked");
    }

    [Test]
    public async Task A_blocked_default_is_left_out_of_the_launch()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var claude = new FakeClaude();
        using var srt = new FakeSrt();
        ProfileStore.Create("work");
        Sandboxing.Set("work", true);
        using (ProfileStore.AcquireLock())
            SandboxOptions.SetBlocked("work", "registry.npmjs.org", true);

        await Assert.That(global::Cli.Run(["use", "work"])).IsEqualTo(0);

        var sites = srt.List("network", "allowedDomains");
        await Assert.That(sites).DoesNotContain("registry.npmjs.org");
        await Assert.That(sites).Contains("pypi.org");
    }

    [Test]
    public async Task Blocked_defaults_are_kept_put_back_and_follow_a_rename()
    {
        var work = ProfileStore.Create("work");
        SandboxOptions.SetBlocked("work", "pypi.org", true);
        SandboxOptions.SetBlocked("work", "crates.io", true);
        SandboxOptions.SetBlocked("work", "pypi.org", true);
        await Assert
            .That(SandboxOptions.BlockedOf("work"))
            .IsEquivalentTo(["pypi.org", "crates.io"]);

        SandboxOptions.SetBlocked("work", "PYPI.org", false);
        await Assert.That(SandboxOptions.BlockedOf("work")).IsEquivalentTo(["crates.io"]);

        var job = ProfileStore.Rename(work, "job").Profile;
        await Assert.That(SandboxOptions.BlockedOf("job")).IsEquivalentTo(["crates.io"]);

        SandboxOptions.ClearBlocked("job");
        await Assert.That(SandboxOptions.BlockedOf("job")).IsEmpty();
        await Assert.That(File.ReadAllText(Paths.SandboxOptionsFile).Trim()).IsEqualTo("{}");
    }

    [Test]
    public async Task The_network_screens_help_fits_the_window()
    {
        await Assert.That(NetworkEditor.Help.Length).IsLessThanOrEqualTo(80);
        await Assert
            .That(
                ProfileEditor
                    .Hint(ProfileEditor.Field.Allow, new Profile { Name = "w", Dir = "/w" })
                    .Length + 2
            )
            .IsLessThanOrEqualTo(80);
    }
}
