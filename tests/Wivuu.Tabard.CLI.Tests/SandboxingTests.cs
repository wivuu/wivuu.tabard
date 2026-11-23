namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// The on/off list and the pieces of a sandboxed launch that can be checked without starting one.
/// Runs against the redirected home from <see cref="Sandbox"/> - the test helper, which is unrelated
/// to Docker sandboxes despite the name - so it shares the one-home-per-process constraint.
/// </summary>
[NotInParallel]
public class SandboxingTests
{
    [Before(HookType.Test)]
    public void StartFromABareHome() => Sandbox.Reset();

    private static Workspace Folder(string path) => new(path, path, [], []);

    [Test]
    public async Task Nothing_is_sandboxed_without_a_file()
    {
        await Assert.That(Sandboxing.Names()).IsEmpty();
        await Assert.That(Sandboxing.IsOn("work")).IsFalse();
    }

    [Test]
    public async Task Set_round_trips_on_and_off()
    {
        Sandboxing.Set("work", true);
        Sandboxing.Set("personal", true);

        await Assert.That(Sandboxing.IsOn("work")).IsTrue();
        await Assert.That(Sandboxing.IsOn("personal")).IsTrue();

        Sandboxing.Set("work", false);

        await Assert.That(Sandboxing.IsOn("work")).IsFalse();
        await Assert.That(Sandboxing.IsOn("personal")).IsTrue();
        await Assert
            .That(File.Exists($"{Paths.SandboxedFile}.{Environment.ProcessId}.tmp"))
            .IsFalse();
    }

    /// <summary>The list has to live where no sandbox can reach it - not in any profile.</summary>
    [Test]
    public async Task The_list_lives_outside_the_profiles()
    {
        var dir = Path.GetFullPath(Path.GetDirectoryName(Paths.SandboxedFile)!);

        await Assert.That(dir).IsEqualTo(Path.GetFullPath(Paths.TabardRoot));
    }

    /// <summary>Two halves on two lines would each name nothing, leaving the profile unsandboxed
    /// while the user believes otherwise.</summary>
    [Test]
    public async Task Set_refuses_a_name_one_line_cannot_hold()
    {
        await Assert
            .That(() => Sandboxing.Set("work\nmate", true))
            .Throws<InvalidOperationException>();

        await Assert.That(Sandboxing.Names()).IsEmpty();
    }

    [Test]
    public async Task Renaming_a_profile_keeps_it_sandboxed()
    {
        var work = ProfileStore.Create("work");
        Sandboxing.Set("work", true);

        var result = ProfileStore.Rename(work, "job");

        await Assert.That(Sandboxing.IsOn("job")).IsTrue();
        await Assert.That(Sandboxing.IsOn("work")).IsFalse();
        await Assert.That(result.Warnings).Contains(w => w.Contains("sbx rm"));
    }

    /// <summary>A profile made later under the same name must not inherit the setting.</summary>
    [Test]
    public async Task Deleting_a_profile_forgets_its_setting()
    {
        var work = ProfileStore.Create("work");
        Sandboxing.Set("work", true);

        var warnings = ProfileStore.Delete(work);

        await Assert.That(Sandboxing.IsOn("work")).IsFalse();
        await Assert.That(warnings).Contains(w => w.Contains("sbx ls"));
    }

    [Test]
    public async Task Deleting_an_unsandboxed_profile_says_nothing_about_sandboxes()
    {
        var work = ProfileStore.Create("work");

        await Assert.That(ProfileStore.Delete(work)).IsEmpty();
    }

    [Test]
    public async Task SandboxName_is_stable_and_names_the_profile_and_folder()
    {
        var name = Sandboxing.SandboxName("Work", "/Users/me/projects/My App");

        await Assert
            .That(name)
            .IsEqualTo(Sandboxing.SandboxName("Work", "/Users/me/projects/My App"));
        await Assert.That(name).StartsWith("tabard-work-my-app-");
    }

    /// <summary>sbx fixes mounts at creation, so a sandbox can never be shared between two
    /// profiles, or two folders - even ones that slug the same.</summary>
    [Test]
    public async Task SandboxName_differs_by_profile_and_by_folder()
    {
        var name = Sandboxing.SandboxName("work", "/projects/app");

        await Assert.That(Sandboxing.SandboxName("personal", "/projects/app")).IsNotEqualTo(name);
        await Assert.That(Sandboxing.SandboxName("work", "/elsewhere/app")).IsNotEqualTo(name);
        await Assert
            .That(Sandboxing.SandboxName("a_b", "/p"))
            .IsNotEqualTo(Sandboxing.SandboxName("a-b", "/p"));
    }

    /// <summary>sbx's rules: 63 at most, letters, digits and hyphens here, alphanumeric at the ends.
    /// </summary>
    [Test]
    [Arguments("work", "/projects/app")]
    [Arguments(
        "a-very-long-profile-name-that-goes-on-and-on",
        "/projects/an-equally-long-folder-name-too"
    )]
    [Arguments("ünïcode", "/projects/日本語")]
    [Arguments("---", "/")]
    public async Task SandboxName_is_one_sbx_accepts(string profile, string workspace)
    {
        var name = Sandboxing.SandboxName(profile, workspace);

        await Assert.That(name.Length).IsLessThanOrEqualTo(63);
        await Assert
            .That(name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            .IsTrue();
        await Assert.That(char.IsLetterOrDigit(name[0]) && char.IsLetterOrDigit(name[^1])).IsTrue();
        await Assert.That(name).DoesNotContain("--");
    }

    [Test]
    public async Task ReadOnlyPaths_covers_what_runs_or_instructs_and_nothing_else()
    {
        var dir = Sandbox.Scratch();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "");
        File.WriteAllText(Path.Combine(dir, "statusline.sh"), "");
        File.WriteAllText(Path.Combine(dir, ".credentials.json"), "{}");
        File.WriteAllText(Path.Combine(dir, ".claude.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "history.jsonl"), "");
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        Directory.CreateDirectory(Path.Combine(dir, "projects"));

        var names = Sandboxing.ReadOnlyPaths(dir).Select(path => Path.GetFileName(path)).ToList();

        await Assert
            .That(names)
            .IsEquivalentTo(new[] { "settings.json", "CLAUDE.md", "hooks", "statusline.sh" });
    }

    [Test]
    public async Task RunArgs_creates_with_the_mounts()
    {
        var dir = Sandbox.Scratch();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");

        var args = Sandboxing.RunArgs(
            "tabard-x",
            dir,
            Folder("/projects/app"),
            exists: false,
            ["--continue"]
        );

        await Assert
            .That(args)
            .IsEquivalentTo(
                new[]
                {
                    "run",
                    "claude",
                    "--name",
                    "tabard-x",
                    "-e",
                    $"CLAUDE_CONFIG_DIR={dir}",
                    "/projects/app",
                    dir,
                    $"{Path.Combine(dir, "settings.json")}:ro",
                    "--",
                    "--settings",
                    Sandboxing.KeepPrompts,
                    "--continue",
                }
            );
    }

    /// <summary>sbx keeps the mounts of a sandbox that exists, so going back in names it and
    /// nothing else - but the environment is per session and has to be passed again.</summary>
    [Test]
    public async Task RunArgs_reattaches_by_name()
    {
        var dir = Sandbox.Scratch();

        var args = Sandboxing.RunArgs("tabard-x", dir, Folder("/projects/app"), exists: true, []);

        await Assert
            .That(args)
            .IsEquivalentTo(
                new[]
                {
                    "run",
                    "--name",
                    "tabard-x",
                    "-e",
                    $"CLAUDE_CONFIG_DIR={dir}",
                    "--",
                    "--settings",
                    Sandboxing.KeepPrompts,
                }
            );
    }

    /// <summary>sbx's image always passes --dangerously-skip-permissions, so tabard keeps the
    /// prompts on unless the launch asks for bypass mode itself.</summary>
    [Test]
    [Arguments("--dangerously-skip-permissions")]
    [Arguments("--allow-dangerously-skip-permissions")]
    [Arguments("--permission-mode bypassPermissions")]
    [Arguments("--permission-mode=bypassPermissions")]
    public async Task RunArgs_leaves_prompts_off_when_asked(string line)
    {
        var claudeArgs = line.Split(' ');
        var dir = Sandbox.Scratch();

        var args = Sandboxing.RunArgs(
            "tabard-x",
            dir,
            Folder("/projects/app"),
            exists: true,
            claudeArgs
        );

        await Assert.That(args).DoesNotContain(Sandboxing.KeepPrompts);
        await Assert.That(args.TakeLast(claudeArgs.Length)).IsEquivalentTo(claudeArgs);
    }

    [Test]
    public async Task RunArgs_keeps_prompts_for_other_permission_modes()
    {
        var dir = Sandbox.Scratch();

        var args = Sandboxing.RunArgs(
            "tabard-x",
            dir,
            Folder("/projects/app"),
            exists: true,
            ["--permission-mode", "acceptEdits"]
        );

        await Assert.That(args).Contains(Sandboxing.KeepPrompts);
    }

    [Test]
    public async Task ResolveWorkspace_is_the_folder_outside_a_repository()
    {
        var dir = Sandbox.Scratch();
        var warnings = new List<string>();

        var workspace = Sandboxing.ResolveWorkspace(dir, warnings);

        await Assert.That(workspace).IsEqualTo(new Workspace(dir, dir, [], []));
        await Assert.That(warnings).IsEmpty();
    }

    /// <summary>From a subfolder the sandbox still needs the .git above it, and git's hooks and
    /// config must not be writable from inside.</summary>
    [Test]
    public async Task ResolveWorkspace_mounts_the_repository_from_a_subfolder()
    {
        var repo = Sandbox.Scratch();
        var git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(Path.Combine(git, "hooks"));
        File.WriteAllText(Path.Combine(git, "config"), "");
        var sub = Directory.CreateDirectory(Path.Combine(repo, "src", "app")).FullName;

        var workspace = Sandboxing.ResolveWorkspace(sub, []);

        await Assert.That(workspace.Root).IsEqualTo(repo);
        await Assert.That(workspace.WorkDir).IsEqualTo(sub);
        await Assert.That(workspace.Writable).IsEmpty();
        await Assert
            .That(workspace.ReadOnly)
            .IsEquivalentTo([Path.Combine(git, "hooks"), Path.Combine(git, "config")]);
    }

    /// <summary>A worktree's .git is a file pointing into the main checkout, and git needs the
    /// shared directory that 'commondir' names.</summary>
    [Test]
    public async Task ResolveWorkspace_mounts_a_worktrees_shared_git_directory()
    {
        var main = Sandbox.Scratch("main");
        var common = Path.Combine(main, ".git");
        var own = Directory.CreateDirectory(Path.Combine(common, "worktrees", "feature")).FullName;
        Directory.CreateDirectory(Path.Combine(common, "hooks"));
        File.WriteAllText(Path.Combine(own, "commondir"), "../..\n");

        var tree = Sandbox.Scratch("tree");
        File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {own}\n");

        var workspace = Sandboxing.ResolveWorkspace(tree, []);

        await Assert.That(workspace.Root).IsEqualTo(tree);
        await Assert.That(workspace.Writable).IsEquivalentTo([common]);
        await Assert.That(workspace.ReadOnly).Contains(Path.Combine(common, "hooks"));
    }

    [Test]
    public async Task RunArgs_starts_in_the_folder_and_mounts_the_repository()
    {
        var dir = Sandbox.Scratch();
        var workspace = new Workspace(
            "/projects/app",
            "/projects/app/src",
            ["/projects/main/.git"],
            ["/projects/app/.git/hooks"]
        );

        var args = Sandboxing.RunArgs(
            "tabard-x",
            dir,
            workspace,
            exists: false,
            [],
            image: "my/image:1",
            environment: ["GIT_AUTHOR_NAME=Me"]
        );

        await Assert
            .That(args.Take(10))
            .IsEquivalentTo(
                new[]
                {
                    "run",
                    "claude",
                    "--name",
                    "tabard-x",
                    "-t",
                    "my/image:1",
                    "-w",
                    "/projects/app/src",
                    "-e",
                    $"CLAUDE_CONFIG_DIR={dir}",
                }
            );
        await Assert.That(args).Contains("GIT_AUTHOR_NAME=Me");
        await Assert.That(args).Contains("/projects/app");
        await Assert.That(args).Contains("/projects/main/.git");
        await Assert.That(args).Contains("/projects/app/.git/hooks:ro");
    }

    /// <summary>sbx fixes the image at creation, so going back in must not pass one.</summary>
    [Test]
    public async Task RunArgs_reattaching_leaves_the_image_out()
    {
        var args = Sandboxing.RunArgs(
            "tabard-x",
            Sandbox.Scratch(),
            Folder("/projects/app"),
            exists: true,
            [],
            image: "my/image:1"
        );

        await Assert.That(args).DoesNotContain("-t");
    }

    /// <summary>Choosing an image has to make a new sandbox, but sandboxes made before images
    /// existed keep their names.</summary>
    [Test]
    public async Task SandboxName_changes_with_the_image_only_when_there_is_one()
    {
        var plain = Sandboxing.SandboxName("work", "/projects/app");

        await Assert.That(Sandboxing.SandboxName("work", "/projects/app", null)).IsEqualTo(plain);
        await Assert
            .That(Sandboxing.SandboxName("work", "/projects/app", "my/image:1"))
            .IsNotEqualTo(plain);
    }

    [Test]
    public async Task Images_round_trip_and_follow_renames_and_deletes()
    {
        var work = ProfileStore.Create("work");

        Sandboxing.SetImage("work", "my/image:1");
        await Assert.That(Sandboxing.ImageOf("work")).IsEqualTo("my/image:1");

        var renamed = ProfileStore.Rename(work, "job").Profile;
        await Assert.That(Sandboxing.ImageOf("job")).IsEqualTo("my/image:1");
        await Assert.That(Sandboxing.ImageOf("work")).IsNull();

        ProfileStore.Delete(renamed);
        await Assert.That(Sandboxing.Images()).IsEmpty();

        Sandboxing.SetImage("other", "x");
        Sandboxing.SetImage("other", null);
        await Assert.That(Sandboxing.ImageOf("other")).IsNull();
    }

    [Test]
    public async Task SetImage_refuses_references_with_spaces()
    {
        await Assert
            .That(() => Sandboxing.SetImage("work", "my image"))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments(
        "Ada Lovelace <ada@example.com> 1700000000 +0000",
        "Ada Lovelace",
        "ada@example.com"
    )]
    [Arguments("Me <me@host.local> 1 -0600", "Me", "me@host.local")]
    public async Task ParseIdent_reads_git_vars_answer(string ident, string name, string email)
    {
        await Assert.That(Sandboxing.ParseIdent(ident)).IsEqualTo((name, email));
    }

    [Test]
    [Arguments("no brackets")]
    [Arguments(" <a@b> 1 +0000")]
    [Arguments("Name <> 1 +0000")]
    public async Task ParseIdent_refuses_what_it_cannot_read(string ident)
    {
        await Assert.That(Sandboxing.ParseIdent(ident)).IsNull();
    }

    [Test]
    public async Task GitIdentity_prefers_the_environment()
    {
        var before = Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "set@example.com");

        try
        {
            await Assert
                .That(Sandboxing.GitIdentity(Sandbox.Scratch()))
                .Contains("GIT_AUTHOR_EMAIL=set@example.com");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", before);
        }
    }

    /// <summary>A folder holding ~/.tabard would hand the sandbox the list that decides whether it
    /// is one, and every other profile's login with it.</summary>
    [Test]
    public async Task CheckWorkspace_refuses_folders_that_hold_tabard()
    {
        await Assert
            .That(() => Sandboxing.CheckWorkspace(Sandbox.Home))
            .Throws<InvalidOperationException>();
        await Assert
            .That(() => Sandboxing.CheckWorkspace(Paths.TabardRoot))
            .Throws<InvalidOperationException>();
        await Assert
            .That(() => Sandboxing.CheckWorkspace(Paths.ProfilesRoot))
            .Throws<InvalidOperationException>();
        await Assert.That(() => Sandboxing.CheckWorkspace("/")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CheckWorkspace_accepts_a_project_folder()
    {
        var project = Sandbox.Scratch();

        Sandboxing.CheckWorkspace(project);

        await Assert.That(project).IsNotEmpty();
    }

    [Test]
    public async Task Prepare_makes_a_settings_file_and_keeps_one_that_is_there()
    {
        var empty = Sandbox.Scratch("empty");
        var full = Sandbox.Scratch("full");
        File.WriteAllText(Path.Combine(full, "settings.json"), """{"env":{"A":"1"}}""");

        Sandboxing.Prepare(empty);
        Sandboxing.Prepare(full);

        await Assert.That(File.Exists(Path.Combine(empty, "settings.json"))).IsTrue();
        await Assert
            .That(File.ReadAllText(Path.Combine(full, "settings.json")))
            .IsEqualTo("""{"env":{"A":"1"}}""");
    }

    /// <summary>What sbx's Claude setup writes into the profile it is handed, and the reason
    /// <see cref="Sandboxing.ForgetGateway"/> exists.</summary>
    private const string WithGateway = """
        {
          "numStartups": 42,
          "projects": { "/x": { "allowedTools": ["Bash"] } },
          "mcpServers": {
            "mine": { "type": "stdio", "command": "my-server" },
            "mcp-gateway": {
              "type": "http",
              "url": "http://mcp-gateway.docker.internal/mcp",
              "headers": { "Authorization": "Bearer proxy-managed" }
            }
          },
          "note": "caffè"
        }
        """;

    [Test]
    public async Task ForgetGateway_removes_the_sandbox_server_and_keeps_the_rest()
    {
        var file = Path.Combine(Sandbox.Scratch(), ".claude.json");
        File.WriteAllText(file, WithGateway);

        await Assert.That(Sandboxing.ForgetGateway(file)).IsNull();

        var text = File.ReadAllText(file);
        await Assert.That(text).DoesNotContain("docker.internal");
        await Assert.That(text).Contains("my-server");
        await Assert.That(text).Contains("\"numStartups\": 42");
        await Assert.That(text).Contains("allowedTools");
        await Assert.That(text).Contains("caffè");
    }

    [Test]
    public async Task ForgetGateway_leaves_a_file_without_one_untouched()
    {
        var file = Path.Combine(Sandbox.Scratch(), ".claude.json");
        const string original =
            """{ "mcpServers": { "mine": { "url": "https://example.com/mcp" } } }""";
        File.WriteAllText(file, original);

        await Assert.That(Sandboxing.ForgetGateway(file)).IsNull();
        await Assert.That(File.ReadAllText(file)).IsEqualTo(original);
    }

    [Test]
    public async Task ForgetGateway_keeps_the_file_private()
    {
        if (OperatingSystem.IsWindows())
            return;

        var file = Path.Combine(Sandbox.Scratch(), ".claude.json");
        File.WriteAllText(file, WithGateway);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Sandboxing.ForgetGateway(file);

        await Assert
            .That(File.GetUnixFileMode(file))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task ForgetGateway_reports_rather_than_throws_on_a_broken_file()
    {
        var file = Path.Combine(Sandbox.Scratch(), ".claude.json");
        File.WriteAllText(file, "{ not json");

        await Assert.That(Sandboxing.ForgetGateway(file)).IsNotNull();
        await Assert.That(File.ReadAllText(file)).IsEqualTo("{ not json");
    }
}
