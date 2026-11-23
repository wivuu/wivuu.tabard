using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wivuu.Tabard.Cli;

/// <summary>Where a new profile's configuration is copied from.</summary>
/// <param name="Label">How the picker and messages name it.</param>
/// <param name="Dir">The directory to copy from.</param>
internal sealed record CopySource(string Label, string Dir);

/// <summary>
/// Starts a new profile as a copy of another one's setup: settings, instructions, hooks, plugins,
/// skills, custom providers, MCP servers and its login, so the new profile is signed in to the same
/// account from its first launch. Never its past - history, sessions, caches and usage stats stay
/// where they are.
/// </summary>
internal static class ProfileCopy
{
    /// <summary>
    /// What a Claude Code profile's configuration is, beyond what
    /// <see cref="Sandboxing.ReadOnlyPaths"/> already covers (settings.json, CLAUDE.md, hooks,
    /// plugins, commands, agents, skills, output-styles and loose scripts).
    /// </summary>
    private static readonly string[] ClaudeExtras = ["keybindings.json", ".credentials.json"];

    /// <summary>What of .claude.json comes along: the account, the API key login, onboarding (so it
    /// is not asked again for an account that has done it) and the user-scope MCP servers. The rest
    /// is usage stats, caches and announcements.</summary>
    private static readonly string[] ClaudeJsonKeys =
    [
        "oauthAccount",
        "primaryApiKey",
        "customApiKeyResponses",
        "hasCompletedOnboarding",
        "lastOnboardingVersion",
        "mcpServers",
    ];

    /// <summary>What of each project's entry in .claude.json comes along: its local-scope MCP
    /// servers, which .mcp.json servers were approved, and whether the folder is trusted. The rest
    /// is the last session's stats.</summary>
    private static readonly string[] ClaudeProjectKeys =
    [
        "mcpServers",
        "enabledMcpjsonServers",
        "disabledMcpjsonServers",
        "mcpContextUris",
        "allowedTools",
        "hasTrustDialogAccepted",
        "hasClaudeMdExternalIncludesApproved",
        "hasClaudeMdExternalIncludesWarningShown",
    ];

    /// <summary>pi's setup, its logins (auth.json) included. sessions/, bin/ and install/ are
    /// history and pi's own tooling, so none of those come along.</summary>
    private static readonly string[] PiEntries =
    [
        "settings.json",
        "auth.json",
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

        if (harness is Harness.Claude)
        {
            if (
                CopyClaudeJson(
                    Path.Combine(from, ".claude.json"),
                    Path.Combine(to, ".claude.json")
                ) is
                { } problem
            )
                warnings.Add(problem);

            if (Keychain.CopyLogin(from, to, sourceIsHome: IsHome(source.Dir)) is { } keychain)
                warnings.Add(keychain);
        }

        foreach (var json in JsonToRepoint(to))
        {
            try
            {
                var text = File.ReadAllText(json);
                var updated = Repoint(text, from, to, PathEnd);

                // A profile adopted from ~/.claude names its plugins by that path, which is only
                // this profile while ~/.claude links to it. Only what is inside it: ~/.claude.json
                // is a different file.
                if (harness is Harness.Claude)
                    updated = Repoint(
                        updated,
                        Path.TrimEndingDirectorySeparator(Paths.ClaudeDir),
                        to,
                        Separator
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

    private static bool IsHome(string dir) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
            Path.TrimEndingDirectorySeparator(Paths.ClaudeDir),
            StringComparison.Ordinal
        );

    /// <summary>
    /// The parts of .claude.json in <see cref="ClaudeJsonKeys"/> and, per project,
    /// <see cref="ClaudeProjectKeys"/>, written 0600 like Claude Code writes it - it names the
    /// account and can hold an API key. Read as a stream: this file reaches tens of megabytes.
    /// Returns a problem to report, or null.
    /// </summary>
    private static string? CopyClaudeJson(string source, string target)
    {
        try
        {
            if (!File.Exists(source))
                return null;

            using var stream = File.OpenRead(source);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
                return null;

            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                using var writer = new Utf8JsonWriter(
                    output,
                    new JsonWriterOptions
                    {
                        Indented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }
                );

                writer.WriteStartObject();

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (ClaudeJsonKeys.Contains(property.Name, StringComparer.Ordinal))
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    if (
                        property.NameEquals("projects")
                        && property.Value.ValueKind is JsonValueKind.Object
                    )
                    {
                        writer.WritePropertyName("projects");
                        writer.WriteStartObject();

                        foreach (var project in property.Value.EnumerateObject())
                        {
                            if (project.Value.ValueKind is not JsonValueKind.Object)
                                continue;

                            var kept = project
                                .Value.EnumerateObject()
                                .Where(p =>
                                    ClaudeProjectKeys.Contains(p.Name, StringComparer.Ordinal)
                                )
                                .ToList();

                            if (kept.Count == 0)
                                continue;

                            writer.WritePropertyName(project.Name);
                            writer.WriteStartObject();
                            foreach (var p in kept)
                                p.WriteTo(writer);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndObject();
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"could not copy the account and MCP servers from .claude.json ({ex.Message}).";
        }
    }

    /// <summary>Where a path in JSON can end: at a separator going further in, or at the end of
    /// the string. Without it, copying 'work' would repoint every path naming 'work2'.</summary>
    private const string PathEnd = """(?=[/\\"])""";

    private const string Separator = """(?=[/\\])""";

    /// <summary>
    /// <paramref name="from"/> to <paramref name="to"/> wherever it is followed by
    /// <paramref name="end"/>. A Windows path appears in JSON with its backslashes escaped, so
    /// there the escaped spelling is replaced as well.
    /// </summary>
    private static string Repoint(string text, string from, string to, string end)
    {
        string Replace(string input, string a, string b) =>
            Regex.Replace(input, Regex.Escape(a) + end, b.Replace("$", "$$"));

        if (OperatingSystem.IsWindows())
            text = Replace(text, from.Replace(@"\", @"\\"), to.Replace(@"\", @"\\"));

        return Replace(text, from, to);
    }

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
