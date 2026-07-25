using Envy.Cli;

try
{
    return Cli.Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"envy: {ex.Message}");
    return 1;
}

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
            return Default([]);

        // '--' forces everything after it through to claude, so 'envy -- --help'
        // reaches Claude Code's own help rather than envy's.
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
            Console.Error.WriteLine("envy: created profile 'default' - Claude Code will prompt you to log in.");
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
            throw new ArgumentException("usage: envy use <name> [-- claude args]");

        Adopt();

        var profile = ProfileStore.Find(args[0])
            ?? throw new InvalidOperationException($"no profile named '{args[0]}'. Try 'envy ls'.");

        var rest = args[1..];
        if (rest.Length > 0 && rest[0] == "--")
            rest = rest[1..];

        return LaunchInto(profile, rest);
    }

    private static int Add(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: envy add <name>");

        Adopt();

        var profile = ProfileStore.Create(args[0]);
        Console.Error.WriteLine($"envy: created '{profile.Name}'. Launching Claude Code to log in.");
        return LaunchInto(profile, []);
    }

    private static int Remove(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: envy rm <name>");

        var profile = ProfileStore.Find(args[0])
            ?? throw new InvalidOperationException($"no profile named '{args[0]}'.");

        Console.Error.Write($"Delete profile '{profile.Name}' and its saved login? [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("envy: cancelled.");
            return 1;
        }

        ProfileStore.Delete(profile);
        Console.Error.WriteLine($"envy: deleted '{profile.Name}'.");
        return 0;
    }

    private static int List()
    {
        Adopt();

        var profiles = ProfileStore.List();
        if (profiles.Count == 0)
        {
            Console.WriteLine("No profiles yet. Run 'envy add <name>'.");
            return 0;
        }

        var active = ProfileStore.LastUsed();
        foreach (var profile in profiles)
        {
            var marker = string.Equals(profile.Name, active, StringComparison.Ordinal) ? "*" : " ";
            Console.WriteLine($"{marker} {profile.Name,-18}{profile.Describe()}");
        }

        return 0;
    }

    private static int LaunchInto(Profile profile, IReadOnlyList<string> claudeArgs)
    {
        ProfileStore.SetLastUsed(profile.Name);

        // Keep a bare 'claude' invocation consistent with the last choice made here.
        ProfileStore.Relink(profile);

        return Launcher.Launch(profile, claudeArgs);
    }

    private static void Adopt()
    {
        var result = ProfileStore.AdoptExistingIfNeeded();

        if (result.Adopted)
        {
            Console.Error.WriteLine(
                $"envy: adopted your existing ~/.claude as profile '{ProfileStore.DefaultProfileName}'.");
        }

        foreach (var warning in result.Warnings)
            Console.Error.WriteLine($"envy: {warning}");
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            envy - Claude Code profile switcher

            Usage:
              envy [claude args...]     Pick a profile (or skip the picker if there is only one), then launch
              envy use <name> [-- ...]  Launch a specific profile
              envy add <name>           Create a profile and log in
              envy rm <name>            Delete a profile
              envy ls                   List profiles
              envy -- <claude args>     Force everything through to claude

            Picker keys:
              up/down or j/k   move
              enter            launch the highlighted profile
              x then x         delete the highlighted profile
              esc or q         quit

            Profiles live in ~/.envy/profiles/<name> and are passed to Claude Code
            as CLAUDE_CONFIG_DIR, so each one keeps its own login, settings and history.
            """);

        return 0;
    }
}
