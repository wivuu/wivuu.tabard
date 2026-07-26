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

        // '--' forces everything after it through to claude, so 'tabard -- --help'
        // reaches Claude Code's own help rather than tabard's.
        if (args[0] == "--")
            return Default(args[1..]);

        return args[0] switch
        {
            "help" or "--help" or "-h" => Help(),
            "ls" or "list" => List(),
            "add" or "new" => Add(args[1..]),
            "rm" or "remove" => Remove(args[1..]),
            "use" => Use(args[1..]),
            "openrouter" or "or" => OpenRouterCommand(args[1..]),
            _ => Default(args),
        };
    }

    private const string AddUsage =
        "usage: tabard add <name> [--openrouter] [--model <slug>] "
        + "[--opus|--sonnet|--haiku|--fable|--subagent <slug>] [--key-stdin]";

    private static int Default(string[] claudeArgs)
    {
        Adopt();

        var profiles = ProfileStore.List();

        if (profiles.Count == 0)
        {
            var created = ProfileStore.Create(ProfileStore.DefaultProfileName);
            Console.Error.WriteLine(
                "tabard: created profile 'default' - Claude Code will prompt you to log in."
            );
            return LaunchInto(created, claudeArgs);
        }

        // Case 1: exactly one profile, so there is nothing to choose. Go straight through.
        if (profiles.Count == 1)
            return LaunchInto(profiles[0], claudeArgs);

        // Case 2: let them pick.
        var chosen = Picker.Show(profiles);
        return chosen is null ? 130 : LaunchInto(chosen, claudeArgs);
    }

    private static int Use(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: tabard use <name> [-- claude args]");

        Adopt();

        var profile = Require(args[0]);
        var rest = args[1..];
        if (rest.Length > 0 && rest[0] == "--")
            rest = rest[1..];

        return LaunchInto(profile, rest);
    }

    private static int Add(string[] args)
    {
        var options = AddOptions.Parse(args, AddUsage);

        // Asked before anything is created, so escaping the wizard leaves nothing behind. A pipe or
        // a CI job has no one to ask, and gets the login flow it has always got.
        var provider =
            options.UseOpenRouter ? Provider.OpenRouter
            : Term.Interactive ? Wizard.ChooseProvider()
            : Provider.Anthropic;

        return provider is { } chosen ? Create(options, chosen) : Cancelled();
    }

    private static int Create(AddOptions options, Provider provider)
    {
        // Before Create: once a profile exists there is nothing left to adopt, and an existing
        // ~/.claude would be stranded outside the store forever.
        Adopt();

        var profile = ProfileStore.Create(options.Name);

        if (provider is Provider.OpenRouter && !Configure(profile, options))
            return Cancelled();

        Console.Error.WriteLine(
            provider is Provider.OpenRouter
                ? $"tabard: created '{profile.Name}'. Launching Claude Code."
                : $"tabard: created '{profile.Name}'. Launching Claude Code to log in."
        );

        return LaunchInto(profile, []);
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
            foreach (var profile in profiles)
            {
                var marker = string.Equals(profile.Name, active, StringComparison.Ordinal)
                    ? "*"
                    : " ";
                Console.WriteLine($"{marker} {profile.Name, -18}{profile.Describe()}");
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
            "usage: tabard openrouter add <name> [--model <slug>] "
                + "[--opus|--sonnet|--haiku|--fable|--subagent <slug>] [--key-stdin]"
        );

        return Create(options with { UseOpenRouter = true }, Provider.OpenRouter);
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

    private static int LaunchInto(Profile profile, IReadOnlyList<string> claudeArgs)
    {
        Point(profile);
        return Launcher.Launch(profile, claudeArgs);
    }

    /// <summary>Record the choice and repoint ~/.claude under a lock, so two tabard runs racing
    /// cannot leave the link and ~/.tabard/last naming different profiles.</summary>
    private static void Point(Profile profile)
    {
        using var guard = ProfileStore.AcquireLock();

        ProfileStore.SetLastUsed(profile.Name);

        // Keep a bare 'claude' invocation consistent with the last choice made here.
        foreach (var warning in ProfileStore.Relink(profile))
            Console.Error.WriteLine($"tabard: {warning}");
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
            tabard - Claude Code profile switcher

            Usage:
              tabard [claude args...]     Pick a profile (or skip the picker if there is only one), then launch
              tabard use <name> [-- ...]  Launch a specific profile
              tabard add <name>           Create a profile, choosing Anthropic or OpenRouter
              tabard rm <name>            Delete a profile
              tabard ls                   List profiles
              tabard openrouter <cmd>     Configure a profile's OpenRouter settings
              tabard -- <claude args>     Force everything through to claude

            Picker keys:
              up/down or j/k   move
              1-9              launch that profile
              enter            launch the highlighted profile
              o                reorder (up/down move it, o or enter done)
              r                rename the highlighted profile (enter saves, esc cancels)
              x then x         delete the highlighted profile
              esc or q         quit

            The picker keeps the order you put profiles in; it is saved in ~/.tabard/order.

            Profiles live in ~/.tabard/profiles/<name> and are passed to Claude Code
            as CLAUDE_CONFIG_DIR, so each one keeps its own login, settings and history.

            'tabard add <name> --openrouter' skips the provider question; see
            'tabard openrouter help' for the rest.
            """
        );

        return 0;
    }
}
