namespace Tabard.Cli;

internal static class Paths
{
    public static string Home { get; } =
        Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify
        );

    public static string TabardRoot { get; } = Path.Combine(Home, ".tabard");
    public static string ProfilesRoot { get; } = Path.Combine(TabardRoot, "profiles");
    public static string LastUsedFile { get; } = Path.Combine(TabardRoot, "last");
    public static string LockFile { get; } = Path.Combine(TabardRoot, "lock");

    /// <summary>The config root Claude Code uses when CLAUDE_CONFIG_DIR is unset.</summary>
    public static string ClaudeDir { get; } = Path.Combine(Home, ".claude");

    /// <summary>
    /// Sits beside the config dir rather than inside it on at least some versions,
    /// so tabard tracks it separately. See README for the caveat.
    /// </summary>
    public static string ClaudeJson { get; } = Path.Combine(Home, ".claude.json");
}
