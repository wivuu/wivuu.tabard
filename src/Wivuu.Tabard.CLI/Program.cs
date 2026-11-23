using System.Globalization;
using System.Reflection;
using Wivuu.Tabard.Cli;

try
{
    return Cli.Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"tabard: {ex.Message}");
    return 1;
}

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
            return Default([]);

        // '--' forces everything after it through to claude, so 'tabard -- --help' and
        // 'tabard -- --version' reach Claude Code's own rather than tabard's.
        if (args[0] == "--")
            return Default(args[1..]);

        return args[0] switch
        {
            "help" or "--help" or "-h" => Help(),
            "version" or "--version" or "-v" => Version(),
            "ls" or "list" => List(),
            "select" => Select(args[1..]),
            "add" or "new" => Add(args[1..]),
            "rm" or "remove" => Remove(args[1..]),
            "use" => Use(args[1..]),
            "switch" or "sw" => Switch(args[1..]),
            "sandbox" or "sbx" => SandboxCommand(args[1..]),
            "openrouter" or "or" => OpenRouterCommand(args[1..]),
            "completion" or "completions" => CompletionCommand(args[1..]),
            "__complete" => Complete(args[1..]),
            _ => Default(args),
        };
    }

    /// <summary>
    /// The version tabard was built as: the release tag, injected with -p:Version, or the csproj's
    /// fallback for a local build. The SDK appends '+&lt;commit&gt;' to the informational version;
    /// that is dropped, as nobody asking 'tabard --version' is asking which commit.
    /// </summary>
    internal static string VersionText()
    {
        var informational = typeof(Cli)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var version =
            informational ?? typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    private static int Version()
    {
        Console.WriteLine($"tabard {VersionText()}");
        return 0;
    }

    private const string SwitchUsage = "usage: tabard switch [name]";

    private const string AddUsage =
        "usage: tabard add <name> [--pi | --openrouter] [--from <profile>] [--model <slug>] "
        + "[--opus|--sonnet|--haiku|--fable|--subagent <slug>] [--key-stdin]";

    /// <summary>
    /// Back to whatever was last used in this folder. Looked up with Find because the memory is only
    /// a hint: a profile that has since gone makes this a folder tabard does not know.
    /// </summary>
    private static int Default(string[] claudeArgs)
    {
        Adopt();

        return DirectoryMemory.Recall() is { } name && ProfileStore.Find(name) is { } remembered
            ? LaunchInto(remembered, claudeArgs)
            : SelectAndLaunch(claudeArgs);
    }

    private static int Select(string[] args)
    {
        Adopt();

        return SelectAndLaunch(args is ["--", .. var rest] ? rest : args);
    }

    private static int SelectAndLaunch(string[] claudeArgs)
    {
        if (!Term.Interactive)
            return LaunchUnattended(claudeArgs);

        while (true)
        {
            var choice = Picker.Show(ProfileStore.List());

            if (choice.Profile is { } profile)
                return LaunchInto(profile, claudeArgs);

            if (!choice.CreateNew)
                return 130;

            if (NewProfile() is { } created)
                return LaunchInto(created, claudeArgs);

            // Backed out of creating one, which is not the same as wanting to leave.
        }
    }

    /// <summary>
    /// With no terminal there is nobody to ask, so a pipe or a CI job gets what it has always got:
    /// the first profile made for it, or the only one there is. More than that really is a choice,
    /// which the picker's plain fallback turns into a list and the command to run.
    /// </summary>
    private static int LaunchUnattended(string[] claudeArgs)
    {
        var profiles = ProfileStore.List();

        switch (profiles.Count)
        {
            case 0:
                var created = ProfileStore.Create(ProfileStore.DefaultProfileName);
                Console.Error.WriteLine(
                    "tabard: created profile 'default' - Claude Code will prompt you to log in."
                );
                return LaunchInto(created, claudeArgs);

            case 1:
                return LaunchInto(profiles[0], claudeArgs);

            default:
                Picker.Show(profiles);
                return 130;
        }
    }

    /// <summary>The selector's '+ new profile' row. Null if the user backed out at any step, in which
    /// case nothing was created.</summary>
    private static Profile? NewProfile()
    {
        if (Wizard.ChooseName() is not { } name)
            return null;

        if (Wizard.ChooseHarness() is not { } harness)
            return null;

        if (Wizard.ChooseSource(harness, out var source) is false)
            return null;

        if (harness is Harness.Pi)
            return ProvisionPi(name, source);

        if (Wizard.ChooseProvider() is not { } provider)
            return null;

        return Provision(new AddOptions { Name = name }, provider, source);
    }

    private const string UseUsage =
        "usage: tabard use <name> [--sandbox|--no-sandbox] [-- agent args]";

    private static int Use(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException(UseUsage);

        Adopt();

        var profile = Require(args[0]);
        var rest = args[1..];

        // Only straight after the name, so everything else still goes through to claude as it
        // always has - and '--' is the way to hand claude a flag of the same spelling.
        bool? sandbox = rest switch
        {
            ["--sandbox", ..] => true,
            ["--no-sandbox", ..] => false,
            _ => null,
        };

        if (sandbox is not null)
            rest = rest[1..];

        if (rest.Length > 0 && rest[0] == "--")
            rest = rest[1..];

        return LaunchInto(profile, rest, sandbox);
    }

    private const string SandboxUsage =
        "usage: tabard sandbox [on|off] [name] | tabard sandbox kind <name> [srt|sbx] | "
        + "tabard sandbox allow <name> [<site>...|--none] | tabard sandbox image <name> [<image>|--default]";

    /// <summary>
    /// The picker's 's' key, for a script or a terminal without the picker. With no verb it reports,
    /// so 'tabard sandbox' on its own is as safe to run as 'tabard ls'.
    /// </summary>
    private static int SandboxCommand(string[] args)
    {
        if (args is [("help" or "--help" or "-h"), ..])
        {
            Console.WriteLine(SandboxHelpText);
            return 0;
        }

        switch (args)
        {
            case []:
            {
                var sandboxed = Sandboxing.Names();
                var images = Sbx.Images();
                var profiles = ProfileStore.List();

                if (profiles.Count == 0)
                {
                    Console.WriteLine("No profiles yet. Run 'tabard add <name>'.");
                    return 0;
                }

                foreach (var profile in profiles)
                {
                    var on = sandboxed.Contains(profile.Name);
                    var kind = Sandboxes.For(profile);
                    var detail = kind switch
                    {
                        SrtSandbox
                            when SandboxOptions.AllowedOf(profile.Name) is { Count: > 0 } sites =>
                            $"  -  also {string.Join(", ", sites)}",
                        SbxSandbox when images.TryGetValue(profile.Name, out var set) =>
                            $"  -  {set}",
                        _ => "",
                    };
                    Console.WriteLine(
                        $"{Sandboxing.Mark(on)} {profile.Name, -18}{(on ? kind.Name : "direct"), -7}{(on ? detail : "")}"
                    );
                }

                return 0;
            }

            case ["image", var name]:
            {
                var profile = Require(name);
                Console.WriteLine(
                    Sbx.ImageOf(profile.Name) is { } image
                        ? $"'{profile.Name}' makes its sandboxes from {image}."
                        : $"'{profile.Name}' makes its sandboxes from sbx's default image."
                );
                return 0;
            }

            case ["image", var name, var image]:
            {
                var profile = Require(name);
                var chosen = image is "--default" or "default" ? null : image;

                using (ProfileStore.AcquireLock())
                    Sbx.SetImage(profile.Name, chosen);

                Console.Error.WriteLine(
                    chosen is null
                        ? $"tabard: '{profile.Name}' will make its sandboxes from sbx's default image."
                        : $"tabard: '{profile.Name}' will make its sandboxes from {chosen}."
                );
                Console.Error.WriteLine(
                    "tabard: sandboxes it already has keep their image; the next launch in each "
                        + "folder makes a new one. Tidy the old ones with 'sbx ls' and 'sbx rm'."
                );
                return 0;
            }

            case ["kind", var name]:
            {
                var profile = Require(name);
                Console.WriteLine(
                    $"'{profile.Name}' uses {Sandboxes.For(profile).Description} when sandboxed."
                );
                return 0;
            }

            case ["kind", var name, var kind]:
            {
                var profile = Require(name);

                using (ProfileStore.AcquireLock())
                    SandboxOptions.SetKind(profile.Name, kind);

                var chosen = Sandboxes.For(profile);
                Console.Error.WriteLine(
                    Sandboxing.IsOn(profile.Name)
                        ? $"tabard: {Sandboxing.Status(profile.Name, true)}"
                        : $"tabard: '{profile.Name}' will use {chosen.Description} once sandboxed - "
                            + $"'tabard sandbox on {profile.Name}'."
                );
                WarnIfMissing(profile, chosen);
                return 0;
            }

            case ["allow", var name]:
            {
                var profile = Require(name);
                var sites = SandboxOptions.AllowedOf(profile.Name);
                Console.WriteLine(
                    sites.Count == 0
                        ? $"'{profile.Name}' reaches only what {Harnesses.DisplayName(profile.Harness)} needs."
                        : $"'{profile.Name}' may also reach {string.Join(", ", sites)}."
                );
                return 0;
            }

            case ["allow", var name, .. var sites]:
            {
                var profile = Require(name);
                var text = sites is ["--none" or "none"] ? null : string.Join(' ', sites);

                using (ProfileStore.AcquireLock())
                    SandboxOptions.SetAllowed(profile.Name, text);

                var now = SandboxOptions.AllowedOf(profile.Name);
                Console.Error.WriteLine(
                    now.Count == 0
                        ? $"tabard: '{profile.Name}' will reach only what {Harnesses.DisplayName(profile.Harness)} needs."
                        : $"tabard: '{profile.Name}' may also reach {string.Join(", ", now)}."
                );

                if (Sandboxes.For(profile) is not SrtSandbox)
                    Console.Error.WriteLine(
                        $"tabard: these apply to srt sandboxes; '{profile.Name}' uses "
                            + $"{Sandboxes.For(profile).Description}, whose network is sbx's policy."
                    );

                return 0;
            }

            case [var name]
                when !name.StartsWith('-')
                    && name is not ("on" or "off" or "image" or "kind" or "allow"):
            {
                var profile = Require(name);
                Console.WriteLine(
                    Sandboxing.IsOn(profile.Name)
                        ? $"'{profile.Name}' launches in {Sandboxes.For(profile).Description}."
                        : $"'{profile.Name}' launches directly."
                );
                return 0;
            }

            case [var verb, var name] when verb is "on" or "off":
            {
                var profile = Require(name);
                var on = verb == "on";

                if (on && Sandboxes.Refusal(profile.Name) is { } refusal)
                {
                    Console.Error.WriteLine($"tabard: cannot sandbox '{profile.Name}': {refusal}");
                    return 1;
                }

                using (ProfileStore.AcquireLock())
                    Sandboxing.Set(profile.Name, on);

                Console.Error.WriteLine($"tabard: {Sandboxing.Status(profile.Name, on)}");
                return 0;
            }

            default:
                throw new ArgumentException(SandboxUsage);
        }
    }

    /// <summary>Said when a profile is switched to a kind of sandbox, rather than at its next
    /// launch.</summary>
    private static void WarnIfMissing(Profile profile, ISandbox sandbox)
    {
        if (sandbox.Unsupported(profile) is { } reason)
            Console.Error.WriteLine($"tabard: {reason}");
        else if (!sandbox.Available)
            Console.Error.WriteLine(
                $"tabard: '{sandbox.Executable}' is not on PATH yet - install it with "
                    + $"'{sandbox.Install}' before launching it."
            );
    }

    private const string SandboxHelpText = """
        tabard sandbox - launch a profile's agent inside a sandbox

        Usage:
          tabard sandbox                 Show which profiles launch in a sandbox, and which kind
          tabard sandbox <name>          Show whether one does
          tabard sandbox on <name>       Launch it in a sandbox from now on
          tabard sandbox off <name>      Launch it directly again
          tabard use <name> --sandbox    Just this once, either way
          tabard use <name> --no-sandbox
          tabard sandbox kind <name> srt|sbx       Which kind of sandbox (whichever is installed)
          tabard sandbox allow <name> <site>...    Sites an srt sandbox may also reach
          tabard sandbox allow <name> --none       Only what the agent needs again
          tabard sandbox image <name> <image>      Make its sbx sandboxes from another image
          tabard sandbox image <name> --default    Back to sbx's own

        In the picker, 's' turns it on and off for the highlighted profile, and 'e' opens its
        settings, where Sandbox cycles through off and the kinds installed here. A profile that has
        not picked a kind uses srt if it is installed, otherwise sbx. With neither installed,
        switching it on says what to install.

        Either kind sees the current folder - the whole git repository, when the folder is inside
        one - and the profile's own directory, at the same paths as here. The profile's
        settings.json, CLAUDE.md, hooks, plugins, commands, agents, skills and loose scripts (for
        pi: settings.json, models.json, AGENTS.md, extensions, skills, prompts and themes) are
        read-only, and so are the repository's .git/hooks and .git/config, so nothing a session
        does can plant code that runs on this machine the next time you launch it directly.

        srt (npm install -g @anthropic-ai/sandbox-runtime) runs the agent installed
        here under this machine's own sandbox: Seatbelt on macOS, bubblewrap on Linux. It starts at
        once and keeps nothing between launches. The agent can change only the folder, its profile
        and a temp folder of its own, and cannot read other profiles, ~/.ssh or the usual
        credential stores. Its network goes through srt's proxy, which lets through only the sites
        its agent needs - Anthropic for Claude Code; for pi, the providers in its models.json and
        those it is logged in to - common developer sites (GitHub, GitLab, Bitbucket and the npm,
        PyPI, NuGet, crates.io, Go, RubyGems, Maven and Packagist registries), the profile's allowed
        sites, and the port it waits for (the editor's 'Wait for port'). A refused site answers
        403; allow it with 'tabard sandbox allow' (example.com, *.corp.dev, localhost:3000), or in
        the editor's Network screen, which lists every site and can also block a default one.

        sbx (Docker Sandboxes, brew install docker/tap/sbx) runs the agent in a microVM. One
        sandbox is made per profile and folder (or repository), and launching again goes back into
        it, with whatever was installed in it. Your git identity is passed in. Claude Code runs as
        sbx's 'claude' agent - log in with /login the first time, since a macOS Keychain login
        cannot be read from inside - and an image is anything 'sbx run -t' takes. pi runs under
        sbx's 'shell' agent in a template holding Node.js and the pi version installed here, built
        the first time (a few minutes, once per pi version) and named tabard-pi:<version>. The port
        a profile waits for answers at 127.0.0.1 inside; the network is otherwise sbx's policy.

        Which profiles are sandboxed is kept in ~/.tabard/sandboxed, their kind and sites in
        ~/.tabard/sandbox.json, and sbx images in ~/.tabard/images - outside every profile, so a
        sandbox cannot change any of them.
        """;

    /// <summary>
    /// The first half of a launch on its own: record the choice and repoint ~/.claude, then stop.
    /// Plenty of things run 'claude' without going through tabard - an editor's terminal, a shell
    /// alias, a script - and pointing those at another profile should not need a session started
    /// and quit to do it.
    /// </summary>
    private static int Switch(string[] args)
    {
        if (args.Length > 1)
            throw new ArgumentException($"unexpected argument '{args[1]}'. {SwitchUsage}");

        // Otherwise a typo'd flag would be looked up as a profile name and reported as a missing one.
        if (args.Length == 1 && args[0].StartsWith('-'))
            throw new ArgumentException($"unknown option '{args[0]}'. {SwitchUsage}");

        // As in 'use': this is a decision, and an existing ~/.claude would be stranded outside the
        // store by the relink that follows.
        Adopt();

        var profile = args.Length == 1 ? Require(args[0]) : Choose();
        if (profile is null)
            return 130;

        var warnings = Point(profile);
        foreach (var warning in warnings)
            Console.Error.WriteLine($"tabard: {warning}");

        var bare = Harnesses.Executable(profile.Harness);

        // A warning here means the link was left alone, so the stronger claim would contradict the
        // line above it. The choice was still recorded, and that much is true either way.
        Console.Error.WriteLine(
            profile.Harness is Harness.Claude
                ? warnings.Count == 0
                    ? $"tabard: switched to '{profile.Name}' - a bare 'claude' now uses it."
                    : $"tabard: recorded '{profile.Name}' as the current profile."
                : ExportToShell(profile)
                    ? $"tabard: switched to '{profile.Name}' - a bare '{bare}' in this shell now uses it."
                    : $"tabard: recorded '{profile.Name}' as the current profile - 'tabard use {profile.Name}' "
                        + $"launches it. For a bare '{bare}' to follow 'tabard switch' in your shell, run "
                        + "'tabard completion install' and open a new shell."
        );

        return 0;
    }

    /// <summary>The file the shell function in <see cref="Completions"/> hands 'switch'.</summary>
    internal const string ShellEnvFileVariable = "TABARD_ENV_FILE";

    /// <summary>
    /// A harness without a link to repoint, like pi, can only follow a switch through its config
    /// variable, and a child process cannot set that in the shell that started it. The shell
    /// function that 'tabard completion' installs makes an empty file, names it in
    /// <see cref="ShellEnvFileVariable"/> and exports what it finds there once tabard exits - so
    /// the switch reaches that one shell, and nothing else.
    ///
    /// One 'NAME=value' line, which the function reads as data rather than running: it exports
    /// only the variables it knows, so nothing tabard writes here is ever evaluated. A file that
    /// does not already exist is not one the function made, and is left alone.
    /// </summary>
    private static bool ExportToShell(Profile profile)
    {
        var file = Environment.GetEnvironmentVariable(ShellEnvFileVariable);
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
            return false;

        var dir = Path.GetFullPath(profile.Dir);
        if (dir.AsSpan().IndexOfAny('\r', '\n') >= 0)
            return false;

        try
        {
            File.WriteAllText(file, $"{Harnesses.ConfigVariable(profile.Harness)}={dir}\n");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The profile to switch to when none was named. Creates nothing: 'switch' moves a pointer,
    /// and making the first profile is what 'tabard add' and a bare 'tabard' are for.
    /// </summary>
    private static Profile? Choose()
    {
        var profiles = ProfileStore.List();

        return profiles.Count switch
        {
            0 => throw new InvalidOperationException("no profiles yet. Run 'tabard add <name>'."),
            // Nothing to choose between, and it still repairs links that have drifted.
            1 => profiles[0],
            _ => Picker.Show(profiles, PickerAction.Switch).Profile,
        };
    }

    private static int Add(string[] args)
    {
        var options = AddOptions.Parse(args, AddUsage);

        // The OpenRouter flags only mean something for Claude Code, so they answer this too.
        var harness =
            options.Harness
            ?? (
                options.UseOpenRouter || !Term.Interactive ? Harness.Claude : Wizard.ChooseHarness()
            );

        if (harness is not { } chosenHarness)
            return Cancelled();

        // Resolved before anything is created, so a '--from' naming nothing leaves nothing behind.
        CopySource? source;
        if (options.From is { } from)
            source = ProfileCopy.Resolve(from, chosenHarness);
        else if (!Term.Interactive)
            source = null;
        else if (Wizard.ChooseSource(chosenHarness, out source) is false)
            return Cancelled();

        if (chosenHarness is Harness.Pi)
            return CreatePi(options.Name, source);

        // Asked before anything is created, so escaping the wizard leaves nothing behind. A pipe or
        // a CI job has no one to ask, and gets the login flow it has always got.
        var provider =
            options.UseOpenRouter ? Provider.OpenRouter
            : Term.Interactive ? Wizard.ChooseProvider()
            : Provider.Anthropic;

        return provider is { } chosen ? Create(options, chosen, source) : Cancelled();
    }

    private static int CreatePi(string name, CopySource? source) =>
        LaunchInto(ProvisionPi(name, source), []);

    /// <summary>pi has its own '/login' and provider setup, so a new pi profile is a directory
    /// marked as pi's - empty, or with <paramref name="source"/>'s configuration - ready to launch.
    /// </summary>
    private static Profile ProvisionPi(string name, CopySource? source)
    {
        Adopt();

        var profile = ProfileStore.Create(name);

        try
        {
            using (ProfileStore.AcquireLock())
                Harnesses.Set(profile.Name, Harness.Pi);

            CopyInto(profile, Harness.Pi, source);
        }
        catch
        {
            Discard(profile);
            throw;
        }

        Console.Error.WriteLine(
            $"tabard: created '{profile.Name}'. Launching pi - use /login to sign in."
        );

        return profile;
    }

    private static int Create(AddOptions options, Provider provider, CopySource? source) =>
        Provision(options, provider, source) is { } profile ? LaunchInto(profile, []) : Cancelled();

    /// <summary>Starts <paramref name="profile"/> from <paramref name="source"/>'s configuration,
    /// if there is one, and says so.</summary>
    private static void CopyInto(Profile profile, Harness harness, CopySource? source)
    {
        if (source is null)
            return;

        foreach (var warning in ProfileCopy.Copy(harness, source, profile.Dir))
            Console.Error.WriteLine($"tabard: {warning}");

        Console.Error.WriteLine(
            $"tabard: copied {source.Label}'s login and configuration into '{profile.Name}' - not its history."
        );
    }

    /// <summary>Makes the profile and sets it up, ready to launch. Null if the user backed out of
    /// setup, which leaves nothing behind - see <see cref="Configure"/>.</summary>
    private static Profile? Provision(
        AddOptions options,
        Provider provider,
        CopySource? source = null
    )
    {
        // Before Create: once a profile exists there is nothing left to adopt, and an existing
        // ~/.claude would be stranded outside the store forever. A source of ~/.claude still works
        // afterwards: it is then a link to the profile it became.
        Adopt();

        var profile = ProfileStore.Create(options.Name);

        try
        {
            CopyInto(profile, Harness.Claude, source);

            // A copied OpenRouter setup would otherwise outvote the Anthropic login asked for.
            if (provider is Provider.Anthropic)
            {
                foreach (var warning in ProfileCopy.ForgetOpenRouter(profile.Dir))
                    Console.Error.WriteLine($"tabard: {warning}");
            }
        }
        catch
        {
            Discard(profile);
            throw;
        }

        if (provider is Provider.OpenRouter && !Configure(profile, options))
            return null;

        Console.Error.WriteLine(
            provider is Provider.OpenRouter
                ? $"tabard: created '{profile.Name}'. Launching Claude Code."
                : $"tabard: created '{profile.Name}'. Launching Claude Code to log in."
        );

        return profile;
    }

    /// <summary>
    /// A profile that setup never finished has to go, whether the user escaped or something threw:
    /// an empty one is worse than none at all, because the presence of any profile is what tells
    /// tabard an existing ~/.claude has already been adopted.
    /// </summary>
    private static bool Configure(Profile profile, AddOptions options)
    {
        bool configured;

        try
        {
            configured = Wizard.Setup(profile, options);
        }
        catch
        {
            Discard(profile);
            throw;
        }

        if (!configured)
            Discard(profile);

        return configured;
    }

    /// <summary>Removes a profile the user backed out of before it held anything.</summary>
    private static void Discard(Profile profile)
    {
        try
        {
            Directory.Delete(profile.Dir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"tabard: could not remove the abandoned profile at {profile.Dir} ({ex.Message})."
            );
        }
    }

    private static int Cancelled()
    {
        Console.Error.WriteLine("tabard: cancelled.");
        return 130;
    }

    private static Profile Require(string name) =>
        ProfileStore.Find(name)
        ?? throw new InvalidOperationException($"no profile named '{name}'. Try 'tabard ls'.");

    private static int Remove(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: tabard rm <name>");

        var profile =
            ProfileStore.Find(args[0])
            ?? throw new InvalidOperationException($"no profile named '{args[0]}'.");

        Console.Error.Write($"Delete profile '{profile.Name}' and its saved login? [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            // Declining is not a failure, and stdin at EOF lands here too.
            Console.Error.WriteLine("tabard: cancelled.");
            return 0;
        }

        var warnings = ProfileStore.Delete(profile);
        Console.Error.WriteLine($"tabard: deleted '{profile.Name}'.");

        foreach (var warning in warnings)
            Console.Error.WriteLine($"tabard: {warning}");

        return 0;
    }

    private static int List()
    {
        // No Adopt() here. 'ls' is what a cautious user runs first to see what tabard would do,
        // so it must not be the thing that irreversibly moves ~/.claude.
        var profiles = ProfileStore.List();
        if (profiles.Count == 0)
        {
            Console.WriteLine("No profiles yet. Run 'tabard add <name>'.");
        }
        else
        {
            var active = ProfileStore.LastUsed();
            var sandboxed = Sandboxing.Names();
            foreach (var profile in profiles)
            {
                var marker = string.Equals(profile.Name, active, StringComparison.Ordinal)
                    ? "*"
                    : " ";
                var sandbox = Sandboxing.Mark(sandboxed.Contains(profile.Name));
                Console.WriteLine($"{marker} {sandbox} {profile.Name, -18}{profile.Summary()}");
            }
        }

        if (ProfileStore.WouldAdopt())
        {
            Console.Error.WriteLine(
                "tabard: ~/.claude is not managed by tabard yet. Running 'tabard' or 'tabard add <name>' will "
                    + $"move it to ~/.tabard/profiles/{ProfileStore.DefaultProfileName} and link ~/.claude back at it."
            );
        }

        return 0;
    }

    private static int OpenRouterCommand(string[] args)
    {
        if (args.Length == 0)
            return OpenRouterHelp();

        return args[0] switch
        {
            "add" => OpenRouterAdd(args[1..]),
            "set" => OpenRouterSet(args[1..]),
            "key" => OpenRouterKey(args[1..]),
            "show" => OpenRouterShow(args[1..]),
            "models" => OpenRouterModels(args[1..]),
            "help" or "--help" or "-h" => OpenRouterHelp(),
            _ => throw new ArgumentException(
                $"unknown command 'tabard openrouter {args[0]}'. Try 'tabard openrouter help'."
            ),
        };
    }

    private static int OpenRouterAdd(string[] args)
    {
        var options = AddOptions.Parse(
            args,
            "usage: tabard openrouter add <name> [--from <profile>] [--model <slug>] "
                + "[--opus|--sonnet|--haiku|--fable|--subagent <slug>] [--key-stdin]"
        );

        var source = options.From is { } from ? ProfileCopy.Resolve(from, Harness.Claude) : null;

        return Create(options with { UseOpenRouter = true }, Provider.OpenRouter, source);
    }

    private static int OpenRouterSet(string[] args)
    {
        var options = AddOptions.Parse(
            args,
            "usage: tabard openrouter set <name> [--model <slug>] "
                + "[--opus|--sonnet|--haiku|--fable|--subagent <slug>]"
        );

        var profile = Require(options.Name);
        WarnAboutSavedLogin(profile);

        if (!Wizard.SetModels(profile, options))
            return Cancelled();

        Console.Error.WriteLine($"tabard: updated {profile.SettingsFile}.");
        return 0;
    }

    private static int OpenRouterKey(string[] args)
    {
        var options = AddOptions.Parse(args, "usage: tabard openrouter key <name> [--key-stdin]");

        // Accepted by the shared parser, but this command would silently ignore them.
        if (options.Models.Count > 0)
            throw new ArgumentException("model flags belong to 'tabard openrouter set <name>'.");

        var profile = Require(options.Name);
        WarnAboutSavedLogin(profile);

        if (!Wizard.SetKey(profile, options))
            return Cancelled();

        Console.Error.WriteLine($"tabard: updated the key in {profile.SettingsFile}.");
        return 0;
    }

    private static int OpenRouterShow(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: tabard openrouter show <name>");

        var profile = Require(args[0]);
        var env = Settings.ReadEnv(profile.Dir);

        if (!OpenRouter.Configures(env))
        {
            Console.WriteLine(
                $"'{profile.Name}' is not an OpenRouter profile - it logs in with a Claude account."
            );
            return 0;
        }

        Console.WriteLine($"{"base url", -10}{env[OpenRouter.BaseUrlVariable]}");
        Console.WriteLine($"{"key", -10}{Show(env, OpenRouter.AuthTokenVariable, secret: true)}");

        foreach (var slot in OpenRouter.Slots)
            Console.WriteLine($"{slot.Label, -10}{Show(env, slot.Variable)}");

        WarnAboutSavedLogin(profile);
        return 0;
    }

    private static string Show(
        IReadOnlyDictionary<string, string> env,
        string variable,
        bool secret = false
    ) =>
        env.TryGetValue(variable, out var value) && value.Length > 0
            ? secret
                ? OpenRouter.Redact(value)
                : value
            : "(unset)";

    private static int OpenRouterModels(string[] args)
    {
        var catalog = OpenRouter.FetchCatalog(TimeSpan.FromSeconds(15));

        if (!catalog.Live)
        {
            Console.Error.WriteLine(
                $"tabard: could not fetch the model list ({catalog.Error}); showing a built-in one."
            );
        }

        foreach (var model in catalog.Models)
        {
            var haystack = $"{model.Id} {model.Name}";
            if (!args.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Console.WriteLine(
                model.Summary.Length == 0 ? model.Id : $"{model.Id, -44}{model.Summary}"
            );
        }

        return 0;
    }

    /// <summary>
    /// A profile can hold both an OAuth login and OpenRouter settings. The settings win while they
    /// are there, which is worth saying out loud rather than leaving someone to wonder which one
    /// their requests are going through.
    /// </summary>
    private static void WarnAboutSavedLogin(Profile profile)
    {
        if (!File.Exists(profile.CredentialsFile))
            return;

        Console.Error.WriteLine(
            $"tabard: '{profile.Name}' also has a saved Claude login. Claude Code uses the OpenRouter "
                + "key while these settings are in place; run /logout inside it if the two disagree."
        );
    }

    private static int OpenRouterHelp()
    {
        Console.WriteLine(
            """
            tabard openrouter - configure a profile to talk to OpenRouter

            Usage:
              tabard openrouter add <name>    Create an OpenRouter profile and launch it
              tabard openrouter set <name>    Change which models the profile uses
              tabard openrouter key <name>    Replace the profile's API key
              tabard openrouter show <name>   Print the profile's OpenRouter settings
              tabard openrouter models [term] List the models OpenRouter offers

            Options for add/set:
              --model <slug>                  Use one model for every tier ('auto' means openrouter/auto)
              --opus|--sonnet|--haiku|--fable|--subagent <slug>
                                              Set one tier at a time
            Options for add/key:
              --key-stdin                     Read the API key from stdin

            The key is read from $OPENROUTER_API_KEY when it is set, and asked for otherwise. There is
            no --key flag on purpose: a key in argv ends up in your shell history.

            Everything is written to the profile's own settings.json, which Claude Code reads from
            CLAUDE_CONFIG_DIR - so a bare 'claude' behaves exactly like 'tabard use <name>'.
            """
        );

        return 0;
    }

    private static int CompletionCommand(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            return CompletionHelp();

        if (args[0] == "install")
            return InstallCompletion(args[1..]);

        Console.WriteLine(Completions.Script(args[0]));
        return 0;
    }

    /// <summary>
    /// Everything here goes to stderr. The script itself is what 'tabard completion &lt;shell&gt;'
    /// puts on stdout, and someone who pipes this command somewhere should get nothing.
    /// </summary>
    private static int InstallCompletion(string[] args)
    {
        if (args.Length == 1 && args[0] is "help" or "--help" or "-h")
            return CompletionHelp();

        if (args.Length > 1)
            throw new ArgumentException(
                $"usage: tabard completion install [{string.Join('|', Completions.Shells)}]"
            );

        var result = Completions.Install(args.Length == 1 ? args[0] : null);
        var startup = Paths.Pretty(result.StartupFile);

        Console.Error.WriteLine($"tabard: wrote {Paths.Pretty(result.ScriptFile)}.");

        if (result.Warning is { } problem)
        {
            Console.Error.WriteLine(
                $"tabard: could not add the line to {startup} ({problem}). Add it yourself:"
            );
            Console.Error.WriteLine();
            Console.Error.WriteLine($"    {result.LoadLine}");
            return 1;
        }

        Console.Error.WriteLine(
            result.AlreadyLoaded
                ? $"tabard: {startup} already loads it."
                : $"tabard: added a line to {startup} to load it."
        );

        // The running shell read its startup file long before this command existed.
        Console.Error.WriteLine(
            result.Shell is "bash" or "zsh"
                ? $"tabard: open a new shell, or run 'source {startup}' to use it now."
                : $"tabard: open a new shell, or run '. {startup}' to use it now."
        );

        return 0;
    }

    /// <summary>
    /// The hook the installed scripts call - '__complete &lt;cursor index&gt; &lt;words...&gt;',
    /// answered one candidate per line. Undocumented because it is a protocol rather than a
    /// command, and silent about everything: a completer that writes anything else to stdout
    /// corrupts what the shell is about to insert.
    /// </summary>
    private static int Complete(string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], CultureInfo.InvariantCulture, out var cword))
            return 0;

        foreach (var candidate in Completions.Suggest(args[1..], cword))
            Console.WriteLine(candidate);

        return 0;
    }

    private static int CompletionHelp()
    {
        Console.WriteLine(
            """
            tabard completion - tab completion for profiles, commands and flags

            Usage:
              tabard completion install [shell]   Install it and load it from your startup file
              tabard completion bash              Print the bash script
              tabard completion zsh               Print the zsh script
              tabard completion pwsh              Print the PowerShell script

            'install' writes the script to ~/.tabard/completions and adds one line to your
            ~/.zshrc, ~/.bashrc or PowerShell $PROFILE to load it. It appends nothing twice,
            so it is safe to run again after an upgrade. With no shell named it uses $SHELL.

            To load it without installing anything, in bash or zsh:

              eval "$(tabard completion zsh)"

            or in PowerShell:

              tabard completion pwsh | Out-String | Invoke-Expression

            Profile names are read on every tab press rather than baked into the script, so a
            profile you add or rename can be completed without reloading anything.
            """
        );

        return 0;
    }

    /// <summary><paramref name="sandbox"/> overrides the profile's own setting for this launch
    /// only; null means follow it.</summary>
    private static int LaunchInto(
        Profile profile,
        IReadOnlyList<string> claudeArgs,
        bool? sandbox = null
    )
    {
        foreach (var warning in Point(profile))
            Console.Error.WriteLine($"tabard: {warning}");

        // Not in Point: 'switch' shares that, and starts nothing in this folder. Up front because
        // Launch blocks for the whole session, and a block rather than 'using var' so the lock is
        // not held for that long.
        using (ProfileStore.AcquireLock())
            DirectoryMemory.Remember(profile.Name);

        return Launcher.Launch(profile, claudeArgs, sandbox ?? Sandboxing.IsOn(profile.Name));
    }

    /// <summary>Record the choice and repoint ~/.claude under a lock, so two tabard runs racing
    /// cannot leave the link and ~/.tabard/last naming different profiles. Returns what could not
    /// be done, for a caller that is not about to launch and has to word its own result around it.
    /// </summary>
    private static IReadOnlyList<string> Point(Profile profile)
    {
        using var guard = ProfileStore.AcquireLock();

        ProfileStore.SetLastUsed(profile.Name);

        // Keep a bare 'claude' invocation consistent with the last choice made here. A pi profile
        // is not a Claude Code config, so ~/.claude stays on whichever Claude profile it was on.
        return profile.Harness is Harness.Claude ? ProfileStore.Relink(profile) : [];
    }

    private static void Adopt()
    {
        var result = ProfileStore.AdoptExistingIfNeeded();

        if (result.Adopted)
        {
            Console.Error.WriteLine(
                $"tabard: adopted your existing ~/.claude as profile '{ProfileStore.DefaultProfileName}'."
            );
        }

        foreach (var warning in result.Warnings)
            Console.Error.WriteLine($"tabard: {warning}");
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            tabard - profile switcher for Claude Code and pi

            Usage:
              tabard [args...]            Launch the profile last used in this folder, or ask if it is new
              tabard select [-- ...]      Pick or create a profile, then launch
              tabard use <name> [-- ...]  Launch a specific profile
              tabard switch [name]        Make a profile the current one without launching
              tabard add <name>           Create a profile: Claude Code (Anthropic or OpenRouter) or pi
              tabard rm <name>            Delete a profile
              tabard sandbox <cmd>        Launch a profile inside a sandbox (srt or sbx), or not
              tabard ls                   List profiles
              tabard openrouter <cmd>     Configure a Claude Code profile's OpenRouter settings
              tabard completion install   Install tab completion for zsh, bash or PowerShell
              tabard --version            Show tabard's version ('tabard -- --version' for the agent's)
              tabard -- <args>            Pass everything through to the profile's agent

            Picker keys:
              up/down or j/k   move
              1-9              launch that profile
              enter            launch the highlighted profile, or create one on '+ new profile'
              o                reorder (up/down move it, o or enter done)
              e                edit the highlighted profile: name, sandbox, and a command to
                               run alongside it (started with the agent, stopped when it exits)
                               and a port to wait for before the agent launches
              r                rename the highlighted profile (enter saves, esc cancels)
              s                launch the highlighted profile in a sandbox, or not
              x then x         delete the highlighted profile
              esc or q         quit

            The picker keeps the order you put profiles in; it is saved in ~/.tabard/order.
            Each folder's last profile is saved in ~/.tabard/directories.json.
            A mark beside a profile means it launches in a sandbox; see 'tabard sandbox help'.

            Each profile is a directory in ~/.tabard/profiles/<name> with its own login, settings
            and history, and launches one agent:
              Claude Code   given the directory as CLAUDE_CONFIG_DIR. Launching or switching to a
                            Claude Code profile also points ~/.claude at it, so a bare 'claude'
                            follows your choice.
              pi            given the directory as PI_CODING_AGENT_DIR. ~/.pi/agent is left alone;
                            'tabard switch' sets the variable for the shell it runs in, so a
                            bare 'pi' there follows it (bash and zsh, after 'tabard completion
                            install').
            Which profiles launch pi is saved in ~/.tabard/harnesses.

            'tabard add <name> --pi' makes a pi profile without asking.
            'tabard add <name> --from <profile>' starts it with that profile's login, settings,
            MCP servers, hooks, plugins and skills - never its history. For pi, '--from pi' copies
            your own ~/.pi/agent setup.
            'tabard add <name> --openrouter' makes a Claude Code profile that uses OpenRouter;
            see 'tabard openrouter help' for the rest.
            """
        );

        return 0;
    }
}
