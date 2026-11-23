using System.Text.Json;

namespace Wivuu.Tabard.Cli.Tests;

[NotInParallel]
public class ProfileCopyTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    /// <summary>Configuration and the login come along; history never does.</summary>
    [Test]
    public async Task Copy_takes_the_setup_and_login_but_not_history()
    {
        var from = ProfileStore.Create("work");
        File.WriteAllText(Path.Combine(from.Dir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(from.Dir, "CLAUDE.md"), "be terse");
        File.WriteAllText(Path.Combine(from.Dir, "keybindings.json"), "[]");
        Directory.CreateDirectory(Path.Combine(from.Dir, "skills", "mine"));
        File.WriteAllText(Path.Combine(from.Dir, "skills", "mine", "SKILL.md"), "x");
        File.WriteAllText(from.CredentialsFile, "secret");
        File.WriteAllText(from.ClaudeJsonFile, "{}");
        File.WriteAllText(Path.Combine(from.Dir, "history.jsonl"), "");
        Directory.CreateDirectory(Path.Combine(from.Dir, "projects"));

        var to = ProfileStore.Create("job");
        var warnings = ProfileCopy.Copy(
            Harness.Claude,
            ProfileCopy.Resolve("work", Harness.Claude),
            to.Dir
        );

        await Assert.That(warnings).IsEmpty();
        await Assert
            .That(Directory.EnumerateFileSystemEntries(to.Dir).Select(p => Path.GetFileName(p)!))
            .IsEquivalentTo([
                "settings.json",
                "CLAUDE.md",
                "keybindings.json",
                "skills",
                ".credentials.json",
                ".claude.json",
            ]);
        await Assert.That(File.ReadAllText(to.CredentialsFile)).IsEqualTo("secret");
        await Assert.That(File.Exists(Path.Combine(to.Dir, "skills", "mine", "SKILL.md"))).IsTrue();
    }

    /// <summary>Plugin install paths and the like name the source profile, and would otherwise run
    /// its files from the new one.</summary>
    [Test]
    public async Task Copy_points_json_at_the_new_profile()
    {
        var from = ProfileStore.Create("work");
        Directory.CreateDirectory(Path.Combine(from.Dir, "plugins"));
        File.WriteAllText(
            Path.Combine(from.Dir, "plugins", "installed_plugins.json"),
            $$"""{ "installPath": "{{from.Dir}}/plugins/cache/x", "installLocation": "{{Paths.ClaudeDir}}/plugins/marketplaces/m", "json": "{{Paths.ClaudeJson}}" }"""
        );
        File.WriteAllText(
            Path.Combine(from.Dir, "settings.json"),
            $$"""{ "statusLine": { "command": "{{from.Dir}}/status.sh" } }"""
        );

        var to = ProfileStore.Create("job");
        ProfileCopy.Copy(Harness.Claude, ProfileCopy.Resolve("work", Harness.Claude), to.Dir);

        var plugins = File.ReadAllText(Path.Combine(to.Dir, "plugins", "installed_plugins.json"));
        var settings = File.ReadAllText(Path.Combine(to.Dir, "settings.json"));
        await Assert.That(plugins).Contains($"{to.Dir}/plugins/cache/x");
        await Assert.That(plugins).Contains($"{to.Dir}/plugins/marketplaces/m");
        await Assert.That(plugins).Contains(Paths.ClaudeJson);
        await Assert.That(settings).Contains($"{to.Dir}/status.sh");
        await Assert.That(settings).DoesNotContain(from.Dir + "/");
    }

    [Test]
    public async Task Pi_copies_its_config_but_not_auth_or_sessions()
    {
        Directory.CreateDirectory(ProfileCopy.PiHome);
        File.WriteAllText(Path.Combine(ProfileCopy.PiHome, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(ProfileCopy.PiHome, "models.json"), "{}");
        File.WriteAllText(Path.Combine(ProfileCopy.PiHome, "auth.json"), "{}");
        Directory.CreateDirectory(Path.Combine(ProfileCopy.PiHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(ProfileCopy.PiHome, "bin"));

        var to = ProfileStore.Create("local");
        ProfileCopy.Copy(Harness.Pi, ProfileCopy.Resolve("pi", Harness.Pi), to.Dir);

        await Assert
            .That(Directory.EnumerateFileSystemEntries(to.Dir).Select(p => Path.GetFileName(p)!))
            .IsEquivalentTo(["settings.json", "models.json", "auth.json"]);
    }

    /// <summary>The account and every MCP server come along; usage stats and caches do not.</summary>
    [Test]
    public async Task Copy_keeps_the_account_and_mcp_servers_from_claude_json()
    {
        var from = ProfileStore.Create("work");
        File.WriteAllText(
            from.ClaudeJsonFile,
            """
            {
              "oauthAccount": { "emailAddress": "me@example.com" },
              "hasCompletedOnboarding": true,
              "mcpServers": { "user": { "command": "u" } },
              "numStartups": 40,
              "cachedFeatureFlags": { "x": 1 },
              "projects": {
                "/code/app": {
                  "mcpServers": { "local": { "command": "l" } },
                  "enabledMcpjsonServers": ["team"],
                  "hasTrustDialogAccepted": true,
                  "lastCost": 3.2,
                  "lastSessionId": "abc"
                },
                "/code/stats-only": { "lastCost": 1 }
              }
            }
            """
        );

        var to = ProfileStore.Create("job");
        ProfileCopy.Copy(Harness.Claude, ProfileCopy.Resolve("work", Harness.Claude), to.Dir);

        using var doc = JsonDocument.Parse(File.ReadAllText(to.ClaudeJsonFile));
        var root = doc.RootElement;
        await Assert
            .That(root.EnumerateObject().Select(p => p.Name))
            .IsEquivalentTo(["oauthAccount", "hasCompletedOnboarding", "mcpServers", "projects"]);

        var projects = root.GetProperty("projects");
        await Assert
            .That(projects.EnumerateObject().Select(p => p.Name))
            .IsEquivalentTo(["/code/app"]);
        await Assert
            .That(projects.GetProperty("/code/app").EnumerateObject().Select(p => p.Name))
            .IsEquivalentTo(["mcpServers", "enabledMcpjsonServers", "hasTrustDialogAccepted"]);
        await Assert.That(ProfileStore.Find("job")!.Describe()).Contains("me@example.com");

        if (!OperatingSystem.IsWindows())
            await Assert
                .That(File.GetUnixFileMode(to.ClaudeJsonFile))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The service Claude Code saves a profile's macOS login under, checked against one
    /// it really made for ~/.tabard/profiles/personal.</summary>
    [Test]
    public async Task Keychain_service_hashes_the_config_dir()
    {
        await Assert
            .That(Keychain.ServiceFor("/Users/erik/.tabard/profiles/personal"))
            .IsEqualTo("Claude Code-credentials-d5edd08c");
        await Assert
            .That(Keychain.ServiceFor("/anything", home: true))
            .IsEqualTo("Claude Code-credentials");
    }

    [Test]
    public async Task Sources_offer_the_same_harness_and_your_pi_setup()
    {
        ProfileStore.Create("work");
        ProfileStore.Create("local");
        Harnesses.Set("local", Harness.Pi);
        Directory.CreateDirectory(ProfileCopy.PiHome);
        File.WriteAllText(Path.Combine(ProfileCopy.PiHome, "settings.json"), "{}");

        await Assert
            .That(ProfileCopy.Sources(Harness.Pi).Select(s => s.Dir))
            .IsEquivalentTo([ProfileCopy.PiHome, ProfileStore.Find("local")!.Dir]);
        await Assert
            .That(ProfileCopy.Sources(Harness.Claude).Select(s => s.Label))
            .IsEquivalentTo(["'work'"]);
    }

    [Test]
    public async Task Resolve_refuses_another_harness_and_unknown_names()
    {
        ProfileStore.Create("work");

        await Assert
            .That(() => ProfileCopy.Resolve("work", Harness.Pi))
            .Throws<InvalidOperationException>();
        await Assert
            .That(() => ProfileCopy.Resolve("nope", Harness.Claude))
            .Throws<InvalidOperationException>();
    }

    /// <summary>A copied OpenRouter key would make an Anthropic profile an OpenRouter one.</summary>
    [Test]
    public async Task ForgetOpenRouter_strips_the_copied_setup()
    {
        var dir = ProfileStore.Create("job").Dir;
        Settings.MergeEnv(dir, OpenRouter.Configure("sk-or-test"));
        Settings.MergeEnv(dir, [new("OTHER", "kept")]);

        ProfileCopy.ForgetOpenRouter(dir);

        var env = Settings.ReadEnv(dir);
        await Assert.That(OpenRouter.Configures(env)).IsFalse();
        await Assert.That(env.ContainsKey(OpenRouter.AuthTokenVariable)).IsFalse();
        await Assert.That(env["OTHER"]).IsEqualTo("kept");
    }

    [Test]
    public async Task Add_from_copies_and_launches()
    {
        using var claude = new FakeClaude();
        var work = ProfileStore.Create("work");
        File.WriteAllText(Path.Combine(work.Dir, "CLAUDE.md"), "be terse");

        await Assert.That(global::Cli.Run(["add", "job", "--from", "work"])).IsEqualTo(0);

        var job = ProfileStore.Find("job")!;
        await Assert
            .That(File.ReadAllText(Path.Combine(job.Dir, "CLAUDE.md")))
            .IsEqualTo("be terse");
        await Assert.That(claude.Launches.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Add_from_an_unknown_profile_creates_nothing()
    {
        await Assert
            .That(() => global::Cli.Run(["add", "job", "--from", "nope"]))
            .Throws<InvalidOperationException>();
        await Assert.That(ProfileStore.Find("job")).IsNull();
    }
}
