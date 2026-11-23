namespace Wivuu.Tabard.Cli;

internal static class Paths
{
    /// <summary>
    /// Resolved from the environment first. On Windows <see cref="Environment.GetFolderPath"/> asks
    /// the known-folder API, which answers from the process token and ignores USERPROFILE - so the
    /// tests' sandboxed home would be silently bypassed there while working everywhere else.
    /// </summary>
    public static string Home { get; } =
        Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify
        );

    public static string TabardRoot { get; } = Path.Combine(Home, ".tabard");
    public static string ProfilesRoot { get; } = Path.Combine(TabardRoot, "profiles");
    public static string LastUsedFile { get; } = Path.Combine(TabardRoot, "last");
    public static string LockFile { get; } = Path.Combine(TabardRoot, "lock");

    /// <summary>The picker's order, one profile name per line. A preference, not a source of
    /// truth - the profiles themselves are still just the directories under <see cref="ProfilesRoot"/>.</summary>
    public static string OrderFile { get; } = Path.Combine(TabardRoot, "order");

    /// <summary>Which profile each folder last launched, as a JSON object. Another preference: see
    /// <see cref="DirectoryMemory"/>.</summary>
    public static string DirectoriesFile { get; } = Path.Combine(TabardRoot, "directories.json");

    /// <summary>The profiles that launch inside a Docker sandbox, one name per line. Outside every
    /// profile on purpose: a sandbox can write to its own profile, and must not be able to turn
    /// itself off. See <see cref="Sandboxing"/>.</summary>
    public static string SandboxedFile { get; } = Path.Combine(TabardRoot, "sandboxed");

    /// <summary>The image each sandboxed profile's sandboxes are made from, for profiles that do not
    /// use sbx's default. Outside every profile for the same reason as <see cref="SandboxedFile"/>.
    /// </summary>
    public static string ImagesFile { get; } = Path.Combine(TabardRoot, "images");

    /// <summary>The profiles that launch something other than Claude Code. Outside every profile
    /// for the same reason as <see cref="SandboxedFile"/>. See <see cref="Harnesses"/>.</summary>
    public static string HarnessesFile { get; } = Path.Combine(TabardRoot, "harnesses");

    /// <summary>The command each profile runs alongside its agent. Outside every profile for the same
    /// reason as <see cref="SandboxedFile"/>. See <see cref="Companions"/>.</summary>
    public static string CompanionsFile { get; } = Path.Combine(TabardRoot, "companions.json");

    /// <summary>Where 'tabard completion install' leaves the script it points a shell at.</summary>
    public static string CompletionsRoot { get; } = Path.Combine(TabardRoot, "completions");

    /// <summary>The config root Claude Code uses when CLAUDE_CONFIG_DIR is unset.</summary>
    public static string ClaudeDir { get; } = Path.Combine(Home, ".claude");

    /// <summary>
    /// Sits beside the config dir rather than inside it on at least some versions,
    /// so tabard tracks it separately. See README for the caveat.
    /// </summary>
    public static string ClaudeJson { get; } = Path.Combine(Home, ".claude.json");

    /// <summary>
    /// A path as someone would type it. A message about ~/.bashrc reads better than one about
    /// /Users/you/.bashrc, and both shells take the short form back.
    /// </summary>
    public static string Pretty(string path) =>
        Home.Length > 0 && path.StartsWith(Home, StringComparison.Ordinal)
            ? $"~{path[Home.Length..]}"
            : path;
}
