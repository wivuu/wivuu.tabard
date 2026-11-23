using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>What a profile launches.</summary>
internal enum Harness
{
    Claude,
    Pi,
}

/// <summary>
/// Which harness each profile launches. Claude Code is the default and is never written down, so
/// every profile made before pi support existed keeps meaning what it always meant.
///
/// Lives outside every profile, like <see cref="Paths.SandboxedFile"/>: an agent can write to its
/// own profile, and must not be able to change what tabard launches in it next time. One line per
/// non-default profile, '&lt;harness&gt; &lt;name&gt;' - the harness never holds a space, so everything
/// after the first one is the name.
/// </summary>
internal static class Harnesses
{
    public static string Label(Harness harness) =>
        harness switch
        {
            Harness.Pi => "pi",
            _ => "claude",
        };

    public static Harness? Parse(string text) =>
        text switch
        {
            "claude" => Harness.Claude,
            "pi" => Harness.Pi,
            _ => null,
        };

    /// <summary>The executable a direct launch runs.</summary>
    public static string Executable(Harness harness) => Label(harness);

    /// <summary>The variable that points the harness at the profile's directory.</summary>
    public static string ConfigVariable(Harness harness) =>
        harness switch
        {
            Harness.Pi => "PI_CODING_AGENT_DIR",
            _ => "CLAUDE_CONFIG_DIR",
        };

    public static string DisplayName(Harness harness) =>
        harness switch
        {
            Harness.Pi => "pi",
            _ => "Claude Code",
        };

    /// <summary>
    /// Every profile that does not launch Claude Code. Fails soft like
    /// <see cref="Sandboxing.Names"/>: a file that cannot be read launches Claude Code, which is
    /// what tabard did before it knew about anything else.
    /// </summary>
    public static Dictionary<string, Harness> All()
    {
        var all = new Dictionary<string, Harness>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(Paths.HarnessesFile))
                return all;

            foreach (var line in File.ReadLines(Paths.HarnessesFile))
            {
                var space = line.IndexOf(' ');
                if (space <= 0)
                    continue;

                var name = line[(space + 1)..].Trim();
                if (name.Length > 0 && Parse(line[..space]) is { } harness and not Harness.Claude)
                    all[name] = harness;
            }
        }
        catch
        {
            all.Clear();
        }

        return all;
    }

    public static Harness Of(string profile) =>
        All().TryGetValue(profile, out var harness) ? harness : Harness.Claude;

    /// <summary>Callers hold <see cref="ProfileStore.AcquireLock"/>, as for
    /// <see cref="Sandboxing.Set"/>.</summary>
    public static void Set(string profile, Harness harness)
    {
        if (harness is not Harness.Claude && profile.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new InvalidOperationException(
                $"cannot launch {DisplayName(harness)} for a profile whose name holds a line break - rename it first."
            );

        var all = All();
        var changed =
            harness is Harness.Claude
                ? all.Remove(profile)
                : !all.TryGetValue(profile, out var was) || was != harness;

        if (harness is not Harness.Claude)
            all[profile] = harness;

        if (changed)
            Write(all);
    }

    public static void RenameProfile(string from, string to)
    {
        var all = All();
        if (!all.Remove(from, out var harness))
            return;

        all[to] = harness;
        TryWrite(all);
    }

    public static void ForgetProfile(string profile)
    {
        var all = All();
        if (all.Remove(profile))
            TryWrite(all);
    }

    private static void TryWrite(Dictionary<string, Harness> all)
    {
        try
        {
            Write(all);
        }
        catch
        {
            // Housekeeping riding along with a rename or delete that has already changed the disk.
        }
    }

    private static void Write(Dictionary<string, Harness> all)
    {
        Directory.CreateDirectory(Paths.TabardRoot);

        var temp = $"{Paths.HarnessesFile}.{Environment.ProcessId}.tmp";
        File.WriteAllLines(
            temp,
            all.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Label(pair.Value)} {pair.Key}")
        );
        File.Move(temp, Paths.HarnessesFile, overwrite: true);
    }

    /// <summary>
    /// What the picker shows for a pi profile: the provider and model its settings.json starts
    /// with. pi owns that file, so like <see cref="Profile"/>'s Claude probes this fails soft.
    /// </summary>
    public static string DescribePi(string profileDir)
    {
        try
        {
            var settings = Path.Combine(profileDir, "settings.json");
            if (!File.Exists(settings))
                return "not set up";

            using var stream = File.OpenRead(settings);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            string? Read(string key) =>
                root.ValueKind is JsonValueKind.Object
                && root.TryGetProperty(key, out var value)
                && value.ValueKind is JsonValueKind.String
                    ? value.GetString()
                    : null;

            return (Read("defaultProvider"), Read("defaultModel")) switch
            {
                ({ Length: > 0 } provider, { Length: > 0 } model) => $"{provider}  -  {model}",
                ({ Length: > 0 } provider, _) => provider,
                (_, { Length: > 0 } model) => model,
                _ => "not set up",
            };
        }
        catch
        {
            return "not set up";
        }
    }
}
