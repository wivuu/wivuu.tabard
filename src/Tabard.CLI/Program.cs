using Tabard.Cli;

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
            _ => Default(args),
        };
    }

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

        var profile =
            ProfileStore.Find(args[0])
            ?? throw new InvalidOperationException(
                $"no profile named '{args[0]}'. Try 'tabard ls'."
            );

        var rest = args[1..];
        if (rest.Length > 0 && rest[0] == "--")
            rest = rest[1..];

        return LaunchInto(profile, rest);
    }

    private static int Add(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: tabard add <name>");

        Adopt();

        var profile = ProfileStore.Create(args[0]);
        Console.Error.WriteLine(
            $"tabard: created '{profile.Name}'. Launching Claude Code to log in."
        );
        return LaunchInto(profile, []);
    }

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
              tabard add <name>           Create a profile and log in
              tabard rm <name>            Delete a profile
              tabard ls                   List profiles
              tabard -- <claude args>     Force everything through to claude

            Picker keys:
              up/down or j/k   move
              enter            launch the highlighted profile
              x then x         delete the highlighted profile
              esc or q         quit

            Profiles live in ~/.tabard/profiles/<name> and are passed to Claude Code
            as CLAUDE_CONFIG_DIR, so each one keeps its own login, settings and history.
            """
        );

        return 0;
    }
}
