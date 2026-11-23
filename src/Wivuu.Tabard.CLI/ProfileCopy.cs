namespace Wivuu.Tabard.Cli;

/// <summary>Where a new profile's configuration is copied from.</summary>
/// <param name="Label">How the picker and messages name it.</param>
/// <param name="Dir">The directory to copy from.</param>
internal sealed record CopySource(string Label, string Dir);

/// <summary>
/// Starts a new profile from another one's configuration: settings, instructions, hooks, plugins,
/// skills, custom providers. Never its identity or its past - logins, account files, history and
/// sessions stay where they are, so the new profile signs in on its own, as every profile does.
/// </summary>
internal static class ProfileCopy
{
    /// <summary>
    /// What a Claude Code profile's configuration is, beyond what
    /// <see cref="Sandboxing.ReadOnlyPaths"/> already covers (settings.json, CLAUDE.md, hooks,
    /// plugins, commands, agents, skills, output-styles and loose scripts).
    /// </summary>
    private static readonly string[] ClaudeExtras = ["keybindings.json"];

    /// <summary>pi's configuration. auth.json holds its logins and keys, and sessions/, bin/ and
    /// install/ are history and pi's own tooling, so none of those come along.</summary>
    private static readonly string[] PiEntries =
    [
        "settings.json",
        "models.json",
        "keybindings.json",
        "mcp.json",
        "AGENTS.md",
        "SYSTEM.md",
        "APPEND_SYSTEM.md",
        "extensions",
        "skills",
        "prompts",
        "themes",
    ];

    /// <summary>pi's own config directory when PI_CODING_AGENT_DIR is unset.</summary>
    public static string PiHome { get; } = Path.Combine(Paths.Home, ".pi", "agent");

    /// <summary>
    /// What a new profile of <paramref name="harness"/> could start from: every existing profile of
    /// the same harness, and your own setup: ~/.pi/agent for pi, and for Claude Code ~/.claude
    /// while tabard has not adopted it yet - afterwards it is a link to a profile already listed.
    /// </summary>
    public static List<CopySource> Sources(Harness harness, string? excluding = null)
    {
        var sources = new List<CopySource>();

        if (harness is Harness.Claude && ProfileStore.WouldAdopt())
            sources.Add(new CopySource("your Claude setup (~/.claude)", Paths.ClaudeDir));

        if (harness is Harness.Pi && Directory.Exists(PiHome) && Entries(harness, PiHome).Count > 0)
            sources.Add(new CopySource($"your pi setup ({Paths.Pretty(PiHome)})", PiHome));

        foreach (var profile in ProfileStore.List())
        {
            if (profile.Harness == harness && profile.Name != excluding)
                sources.Add(new CopySource($"'{profile.Name}'", profile.Dir));
        }

        return sources;
    }

    /// <summary>
    /// The source a '--from' value names: a profile, or 'pi' / '~/.pi/agent' for your own pi setup.
    /// Throws with the reason when it names nothing usable.
    /// </summary>
    public static CopySource Resolve(string from, Harness harness)
    {
        if (harness is Harness.Pi && from is "pi" or "~/.pi/agent" or "~/.pi")
        {
            if (!Directory.Exists(PiHome))
                throw new InvalidOperationException(
                    $"there is no pi setup to copy at {Paths.Pretty(PiHome)}."
                );

            return new CopySource($"your pi setup ({Paths.Pretty(PiHome)})", PiHome);
        }

        var profile =
            ProfileStore.Find(from)
            ?? throw new InvalidOperationException(
                $"no profile named '{from}' to copy from. Try 'tabard ls'."
            );

        if (profile.Harness != harness)
            throw new InvalidOperationException(
                $"'{profile.Name}' is a {Harnesses.DisplayName(profile.Harness)} profile, so its "
                    + $"configuration means nothing to {Harnesses.DisplayName(harness)}."
            );

        return new CopySource($"'{profile.Name}'", profile.Dir);
    }

    /// <summary>The configuration in <paramref name="dir"/>, as full paths that exist.</summary>
    public static List<string> Entries(Harness harness, string dir)
    {
        if (harness is Harness.Pi)
            return [.. PiEntries.Select(e => Path.Combine(dir, e)).Where(Exists)];

        var entries = Sandboxing.ReadOnlyPaths(dir);
        entries.AddRange(
            ClaudeExtras
                .Select(e => Path.Combine(dir, e))
                .Where(path => Exists(path) && !entries.Contains(path))
        );
        return entries;
    }

    /// <summary>
    /// Copies <paramref name="source"/>'s configuration into <paramref name="destination"/>, a
    /// profile directory just made. Links are copied as links. JSON at the top and in plugins/ that
    /// names the source directory - the plugin install paths, a status line script - is pointed at
    /// the new one, or the copy would quietly run the other profile's files. Returns what could not
    /// be copied, to warn about; nothing here fails the whole copy.
    /// </summary>
    public static List<string> Copy(Harness harness, CopySource source, string destination)
    {
        var warnings = new List<string>();
        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Dir));
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        foreach (var entry in Entries(harness, from))
        {
            var target = Path.Combine(to, Path.GetFileName(entry));

            try
            {
                CopyEntry(entry, target);
            }
            catch (Exception ex)
            {
                warnings.Add($"could not copy {Path.GetFileName(entry)} ({ex.Message}).");
            }
        }

        foreach (var json in JsonToRepoint(to))
        {
            try
            {
                var text = File.ReadAllText(json);
                var updated = text.Replace(from, to, StringComparison.Ordinal);

                // A profile adopted from ~/.claude names its plugins by that path, which is only
                // this profile while ~/.claude links to it. The trailing separator keeps
                // ~/.claude.json, a different file, out of it.
                if (harness is Harness.Claude)
                    updated = updated.Replace(
                        ClaudeDirPrefix,
                        to + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    );

                if (updated != text)
                    File.WriteAllText(json, updated);
            }
            catch (Exception ex)
            {
                warnings.Add($"could not update paths in {Path.GetFileName(json)} ({ex.Message}).");
            }
        }

        return warnings;
    }

    private static string ClaudeDirPrefix =>
        Path.TrimEndingDirectorySeparator(Paths.ClaudeDir) + Path.DirectorySeparatorChar;

    private static IEnumerable<string> JsonToRepoint(string dir)
    {
        foreach (var folder in new[] { dir, Path.Combine(dir, "plugins") })
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (new FileInfo(file).LinkTarget is null)
                    yield return file;
            }
        }
    }

    private static void CopyEntry(string source, string target)
    {
        var info = new FileInfo(source);

        if (info.LinkTarget is { } link)
        {
            File.CreateSymbolicLink(target, link);
            return;
        }

        if (!Directory.Exists(source))
        {
            File.Copy(source, target, overwrite: false);

            // Keep a script runnable and a settings file private, whatever the umask says.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(source));

            return;
        }

        Directory.CreateDirectory(target);

        foreach (var child in Directory.EnumerateFileSystemEntries(source))
            CopyEntry(child, Path.Combine(target, Path.GetFileName(child)));
    }

    private static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>
    /// Takes the OpenRouter settings back out of a copied settings.json, for a new profile that is
    /// to log in with Anthropic: they would otherwise make it an OpenRouter profile, with the
    /// source's key, no matter what was chosen.
    /// </summary>
    public static IReadOnlyList<string> ForgetOpenRouter(string profileDir)
    {
        if (!OpenRouter.Configures(Settings.ReadEnv(profileDir)))
            return [];

        List<KeyValuePair<string, string?>> removals =
        [
            new(OpenRouter.BaseUrlVariable, null),
            new(OpenRouter.ApiKeyVariable, null),
            new(OpenRouter.AuthTokenVariable, null),
            .. OpenRouter.Slots.Select(slot => new KeyValuePair<string, string?>(
                slot.Variable,
                null
            )),
        ];

        return Settings.MergeEnv(profileDir, removals);
    }
}
