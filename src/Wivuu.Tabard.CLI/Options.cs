namespace Wivuu.Tabard.Cli;

/// <summary>
/// The flags 'tabard add' and the 'tabard openrouter' commands share. A model flag or --key-stdin
/// implies --openrouter, because there is nothing else a model slug or an API key could configure.
/// </summary>
internal sealed record AddOptions
{
    public required string Name { get; init; }
    public bool UseOpenRouter { get; init; }

    /// <summary>--pi: launch pi rather than Claude Code. Null when no harness flag was given.</summary>
    public Harness? Harness { get; init; }

    /// <summary>--from: the profile (or, for pi, 'pi') whose configuration a new profile starts with.</summary>
    public string? From { get; init; }
    public bool KeyFromStdin { get; init; }

    /// <summary>Environment variable name to model slug, for the slots that were given explicitly.</summary>
    public IReadOnlyDictionary<string, string> Models { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static AddOptions Parse(IReadOnlyList<string> args, string usage)
    {
        string? name = null;
        var openRouter = false;
        Harness? harness = null;
        string? from = null;
        var keyFromStdin = false;
        var models = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg == "--openrouter")
            {
                openRouter = true;
                continue;
            }

            if (arg == "--pi")
            {
                harness = Cli.Harness.Pi;
                continue;
            }

            if (arg == "--from")
            {
                if (i + 1 >= args.Count)
                    throw new ArgumentException("'--from' needs a profile to copy from.");

                from = args[++i];
                continue;
            }

            if (arg == "--key-stdin")
            {
                keyFromStdin = true;
                openRouter = true;
                continue;
            }

            // Worth its own message: it is the flag people reach for first, and a key in argv is left
            // behind in shell history and visible in anyone's 'ps' output.
            if (arg is "--key" or "--api-key")
            {
                throw new ArgumentException(
                    "there is no --key flag - a key on the command line ends up in your shell history. "
                        + $"Pipe it in with --key-stdin, or set ${OpenRouter.KeyEnvironmentVariable}."
                );
            }

            if (arg == "--model")
            {
                var every = Slug(args, ref i, arg);
                foreach (var slot in OpenRouter.Slots)
                    models[slot.Variable] = every;

                openRouter = true;
                continue;
            }

            if (OpenRouter.Slots.FirstOrDefault(slot => slot.Flag == arg) is { } match)
            {
                models[match.Variable] = Slug(args, ref i, arg);
                openRouter = true;
                continue;
            }

            if (arg.StartsWith('-'))
                throw new ArgumentException($"unknown option '{arg}'. {usage}");

            if (name is not null)
                throw new ArgumentException($"unexpected argument '{arg}'. {usage}");

            name = arg;
        }

        // The OpenRouter flags write Claude Code's settings.json; pi keeps its own providers.
        if (harness is Cli.Harness.Pi && openRouter)
            throw new ArgumentException(
                "--pi does not take the OpenRouter flags - set pi's provider from inside pi."
            );

        return new AddOptions
        {
            Name = name ?? throw new ArgumentException(usage),
            UseOpenRouter = openRouter,
            Harness = harness,
            From = from,
            KeyFromStdin = keyFromStdin,
            Models = models,
        };
    }

    private static string Slug(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count)
            throw new ArgumentException($"'{flag}' needs a model slug.");

        return Expand(args[++i]);
    }

    /// <summary>'auto' is what people type; the router's slug is what OpenRouter answers to.</summary>
    public static string Expand(string slug) =>
        slug switch
        {
            "auto" => OpenRouter.Auto,
            "auto-beta" => OpenRouter.AutoBeta,
            _ => slug,
        };
}
