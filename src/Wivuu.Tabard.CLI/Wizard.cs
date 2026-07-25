using System.Globalization;

namespace Wivuu.Tabard.Cli;

/// <summary>One row of a menu: the value it stands for, what it is called, and why you would pick it.</summary>
internal sealed record Choice<T>(T Value, string Label, string Detail);

/// <summary>
/// The interactive half of OpenRouter setup. Nothing here decides anything on its own - every step
/// can be supplied on the command line instead, and every step can be escaped, which abandons the
/// command without writing.
/// </summary>
internal static class Wizard
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Null if the user escaped.</summary>
    public static Provider? ChooseProvider()
    {
        Choice<Provider>[] choices =
        [
            new(Provider.Anthropic, "Anthropic", "log in with your Claude account"),
            new(Provider.OpenRouter, "OpenRouter", "use an OpenRouter API key"),
        ];

        return Prompt
            .Select("Which provider should this profile use?", choices, c => c.Label, c => c.Detail)
            ?.Value;
    }

    /// <summary>
    /// Key, models, confirmation, write. False means the user backed out and the caller is holding a
    /// profile it should not keep.
    /// </summary>
    public static bool Setup(Profile profile, AddOptions options)
    {
        if (ResolveKey(options) is not { } key)
            return false;

        if (ResolveModels(options, fillDefaults: true) is not { } models)
            return false;

        if (!Confirm(profile, key, models))
            return false;

        Apply(profile, key, models);
        return true;
    }

    /// <summary>
    /// Models only - the existing key is left where it is, and so is every tier the caller did not
    /// name. Defaulting the rest here would quietly undo an earlier '--opus'.
    /// </summary>
    public static bool SetModels(Profile profile, AddOptions options)
    {
        if (ResolveModels(options, fillDefaults: false) is not { } models)
            return false;

        if (models.Count == 0)
        {
            throw new ArgumentException(
                "nothing to set - pass --model, or a tier flag such as --opus <slug>."
            );
        }

        Apply(profile, null, models);
        return true;
    }

    /// <summary>Key only - the existing model choices are left where they are.</summary>
    public static bool SetKey(Profile profile, AddOptions options)
    {
        if (ResolveKey(options) is not { } key)
            return false;

        Apply(profile, key, null);
        return true;
    }

    private static void Apply(
        Profile profile,
        string? key,
        IReadOnlyDictionary<string, string>? models
    )
    {
        foreach (var warning in Settings.MergeEnv(profile.Dir, OpenRouter.Configure(key, models)))
            Console.Error.WriteLine($"tabard: {warning}");
    }

    /// <summary>
    /// stdin, then the environment, then the terminal. Returns null when the user escapes, and
    /// throws when there is no way left to ask.
    /// </summary>
    private static string? ResolveKey(AddOptions options)
    {
        if (options.KeyFromStdin)
        {
            var piped = Console.In.ReadToEnd().Trim();
            if (piped.Length == 0)
                throw new ArgumentException("--key-stdin was given but stdin was empty.");

            Report(OpenRouter.ValidateKey(piped, Timeout));
            return piped;
        }

        if (
            Environment.GetEnvironmentVariable(OpenRouter.KeyEnvironmentVariable) is
            { Length: > 0 } inherited
        )
        {
            var key = inherited.Trim();
            var use = Term.Interactive
                ? Prompt.Confirm(
                    $"Use the key in ${OpenRouter.KeyEnvironmentVariable} ({OpenRouter.Redact(key)})?"
                )
                : true;

            if (use is null)
                return null;

            if (use is true)
            {
                Report(OpenRouter.ValidateKey(key, Timeout));
                return key;
            }
        }

        if (!Term.Interactive)
        {
            throw new InvalidOperationException(
                "no OpenRouter key and no terminal to ask on - pipe one in with --key-stdin, or set "
                    + $"${OpenRouter.KeyEnvironmentVariable}."
            );
        }

        // Three goes at a key OpenRouter actively rejects; past that they need to go and find a
        // working one rather than keep typing.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Prompt.Text("OpenRouter API key", secret: true) is not { } typed)
                return null;

            if (typed.Length == 0)
            {
                Console.Error.WriteLine("tabard: a key is required (esc to give up).");
                continue;
            }

            var check = OpenRouter.ValidateKey(typed, Timeout);
            Report(check);

            if (check.Status is not KeyStatus.Rejected)
                return typed;
        }

        return null;
    }

    private static void Report(KeyCheck check)
    {
        switch (check.Status)
        {
            case KeyStatus.Valid:
                var bits = new List<string>(3);

                if (check.Label is { Length: > 0 } label)
                    bits.Add($"'{label}'");

                if (check.Remaining is { } left)
                    bits.Add(
                        $"${left.ToString("0.##", CultureInfo.InvariantCulture)} of credit left"
                    );

                if (check.FreeTier)
                    bits.Add("free tier");

                Console.Error.WriteLine(
                    bits.Count == 0
                        ? "tabard: OpenRouter accepted the key."
                        : $"tabard: OpenRouter accepted the key - {string.Join(", ", bits)}."
                );
                break;

            case KeyStatus.Rejected:
                Console.Error.WriteLine("tabard: OpenRouter rejected that key.");
                break;

            default:
                // Saving it anyway: refusing a good key because the network is down would be worse
                // than saving a bad one, which Claude Code reports on the first request.
                Console.Error.WriteLine(
                    $"tabard: could not reach OpenRouter to check the key ({check.Error}); saving it anyway."
                );
                break;
        }
    }

    /// <summary>
    /// Flags win outright: passing any of them is an instruction, and asking anyway would be
    /// second-guessing it.
    /// </summary>
    private static Dictionary<string, string>? ResolveModels(AddOptions options, bool fillDefaults)
    {
        if (options.Models.Count == 0 && Term.Interactive)
            return ChooseModels();

        var models = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var slot in OpenRouter.Slots)
        {
            if (options.Models.TryGetValue(slot.Variable, out var chosen))
                models[slot.Variable] = chosen;
            else if (fillDefaults)
                models[slot.Variable] = slot.Default;
        }

        return models;
    }

    private static Dictionary<string, string>? ChooseModels()
    {
        Choice<string>[] choices =
        [
            new(
                "tiers",
                "Anthropic models via OpenRouter",
                "/model opus|sonnet|haiku|fable keeps meaning what it means"
            ),
            new("router", "Let OpenRouter route", "one router picks a model per request"),
            new("single", "One model for everything", "pick a single model from the catalog"),
            new("each", "Choose per tier", "pick a model for each of the five slots"),
        ];

        if (
            Prompt.Select(
                "How should this profile map Claude Code's model tiers?",
                choices,
                c => c.Label,
                c => c.Detail
            )
            is not { } how
        )
        {
            return null;
        }

        return how.Value switch
        {
            "tiers" => Every(slot => slot.Default),
            "router" => Router(),
            "single" => Single(),
            _ => PerTier(),
        };
    }

    private static Dictionary<string, string> Every(Func<OpenRouter.Slot, string> model)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in OpenRouter.Slots)
            models[slot.Variable] = model(slot);

        return models;
    }

    private static Dictionary<string, string>? Router()
    {
        Choice<string>[] routers =
        [
            new(OpenRouter.Auto, OpenRouter.Auto, "the original auto router"),
            new(
                OpenRouter.AutoBeta,
                OpenRouter.AutoBeta,
                "what OpenRouter's routing docs now recommend"
            ),
        ];

        return Prompt.Select("Which router?", routers, c => c.Label, c => c.Detail) is { } chosen
            ? Every(_ => chosen.Value)
            : null;
    }

    private static Dictionary<string, string>? Single()
    {
        var catalog = LoadCatalog();

        return
            Prompt.Select(
                "Pick one model for every tier",
                catalog.Models,
                m => m.Id,
                m => m.Summary
            )
                is { } chosen
            ? Every(_ => chosen.Id)
            : null;
    }

    private static Dictionary<string, string>? PerTier()
    {
        var catalog = LoadCatalog();
        var models = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var slot in OpenRouter.Slots)
        {
            var start = 0;
            for (var i = 0; i < catalog.Models.Count; i++)
            {
                if (string.Equals(catalog.Models[i].Id, slot.Default, StringComparison.Ordinal))
                {
                    start = i;
                    break;
                }
            }

            var chosen = Prompt.Select(
                $"Model for {slot.Label}",
                catalog.Models,
                m => m.Id,
                m => m.Summary,
                start
            );

            if (chosen is null)
                return null;

            models[slot.Variable] = chosen.Id;
        }

        return models;
    }

    private static Catalog LoadCatalog()
    {
        Console.Error.WriteLine("tabard: fetching the OpenRouter model list...");

        var catalog = OpenRouter.FetchCatalog(Timeout);

        if (!catalog.Live)
        {
            Console.Error.WriteLine(
                $"tabard: could not fetch the model list ({catalog.Error}); showing a built-in one. "
                    + "Any slug can still be set with 'tabard openrouter set <name> --model <slug>'."
            );
        }

        return catalog;
    }

    /// <summary>The last chance to back out, and the only place the whole configuration is visible
    /// at once. Non-interactive runs have already said what they want on the command line.</summary>
    private static bool Confirm(
        Profile profile,
        string key,
        IReadOnlyDictionary<string, string> models
    )
    {
        if (!Term.Interactive)
            return true;

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Profile '{profile.Name}' will use OpenRouter:");
        Console.Error.WriteLine($"    {"key", -10}{OpenRouter.Redact(key)}");

        foreach (var slot in OpenRouter.Slots)
        {
            if (models.TryGetValue(slot.Variable, out var model))
                Console.Error.WriteLine($"    {slot.Label, -10}{model}");
        }

        Console.Error.WriteLine();

        return Prompt.Confirm($"Write this to {profile.SettingsFile}?") is true;
    }
}
