using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// Which profiles launch inside a Docker sandbox (<c>sbx</c>), and how such a launch is put
/// together.
///
/// A sandboxed launch keeps the rest of tabard's model: the profile is still just a directory
/// handed to Claude Code as CLAUDE_CONFIG_DIR. The difference is that Claude Code runs inside a
/// microVM, which sees the current folder and the profile's directory - both mounted at the same
/// paths they have here, so the per-project entries in .claude.json still line up - and nothing
/// else of the host.
///
/// The on/off list is a hint like ~/.tabard/order: a name in it for a profile that has gone is
/// ignored. It lives outside every profile, because the sandbox can write to its own profile and
/// must not be able to switch itself off.
/// </summary>
internal static class Sandboxing
{
    public const string Executable = "sbx";

    /// <summary>The 'sbx' agent tabard launches. One of sbx's built-ins.</summary>
    private const string Agent = "claude";

    /// <summary>sbx refuses longer names.</summary>
    private const int MaxNameLength = 63;

    private const int SlugLength = 20;

    /// <summary>
    /// Mounted read-only inside the sandbox when they exist. Everything here either runs code or
    /// instructs Claude in a later session on the host, and a sandbox that could rewrite them could
    /// plant a hook that runs outside it the next time the profile is launched without one. The
    /// rest of the profile - credentials, history, .claude.json - stays writable, because Claude
    /// Code rotates tokens and records sessions there.
    /// </summary>
    private static readonly string[] Protected =
    [
        "settings.json",
        "CLAUDE.md",
        "hooks",
        "plugins",
        "commands",
        "agents",
        "skills",
        "output-styles",
    ];

    /// <summary>Loose scripts at the top of a profile are usually what a status line or a hook in
    /// settings.json points at, so they get the same treatment as the folders above.</summary>
    private static readonly string[] ScriptExtensions =
    [
        ".sh",
        ".bash",
        ".zsh",
        ".py",
        ".js",
        ".mjs",
        ".cjs",
        ".ts",
        ".rb",
        ".pl",
        ".ps1",
    ];

    /// <summary>
    /// The profiles marked to launch in a sandbox. Fails soft like
    /// <see cref="ProfileStore.Order"/>: a list that cannot be read launches everything directly,
    /// which is how tabard behaved before sandboxing existed.
    /// </summary>
    public static HashSet<string> Names()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(Paths.SandboxedFile))
                return names;

            foreach (var line in File.ReadLines(Paths.SandboxedFile))
            {
                var name = line.Trim();
                if (name.Length > 0)
                    names.Add(name);
            }
        }
        catch
        {
            names.Clear();
        }

        return names;
    }

    public static bool IsOn(string profile) => Names().Contains(profile);

    /// <summary>
    /// Marks a profile to launch in a sandbox, or not. Callers hold
    /// <see cref="ProfileStore.AcquireLock"/>, or two of these racing could drop one another's
    /// change.
    /// </summary>
    public static void Set(string profile, bool on)
    {
        // One per line cannot hold a name with a line break in it, which Unix allows. Refusing is
        // better than writing two halves that each name nothing and leave the profile unsandboxed.
        if (on && profile.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new InvalidOperationException(
                $"cannot sandbox a profile whose name holds a line break - rename it first."
            );

        var names = Names();
        var changed = on ? names.Add(profile) : names.Remove(profile);

        if (changed)
            Write(names);
    }

    /// <summary>Carried over by <see cref="ProfileStore.Rename"/>, or renaming a sandboxed profile
    /// would quietly launch it outside the sandbox from then on.</summary>
    public static void RenameProfile(string from, string to)
    {
        var names = Names();
        if (!names.Remove(from))
            return;

        names.Add(to);
        TryWrite(names);
    }

    /// <summary>A profile created later under the same name must start out the way every new
    /// profile does, not inherit this one's setting.</summary>
    public static void ForgetProfile(string profile)
    {
        var names = Names();
        if (names.Remove(profile))
            TryWrite(names);
    }

    private static void TryWrite(HashSet<string> names)
    {
        try
        {
            Write(names);
        }
        catch
        {
            // Housekeeping riding along with a rename or delete that has already changed the disk.
        }
    }

    private static void Write(HashSet<string> names)
    {
        Directory.CreateDirectory(Paths.TabardRoot);

        // Temp file plus rename, like the other state files. Sorted so the file reads the same
        // however the set happened to be built.
        var temp = $"{Paths.SandboxedFile}.{Environment.ProcessId}.tmp";
        File.WriteAllLines(temp, names.Order(StringComparer.Ordinal));
        File.Move(temp, Paths.SandboxedFile, overwrite: true);
    }

    /// <summary>
    /// One sandbox per profile and folder. sbx only takes mounts when it creates a sandbox, so a
    /// sandbox made for one profile can never be reused for another - the profile has to be in the
    /// name. The readable part is for 'sbx ls'; the hash is what keeps two folders that slug the
    /// same, or two profiles that do, from colliding.
    /// </summary>
    public static string SandboxName(string profile, string workspace)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{profile}\0{workspace}"))
        )[..8];

        var parts = new List<string> { "tabard" };

        if (Slug(profile) is { Length: > 0 } p)
            parts.Add(p);

        if (
            Slug(Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace))) is
            { Length: > 0 } w
        )
            parts.Add(w);

        parts.Add(hash);

        var name = string.Join('-', parts);
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    /// <summary>Lower-case letters, digits and single hyphens - sbx accepts periods too, but its
    /// cloud side does not, and a name that works in one place should work in both.</summary>
    internal static string Slug(string text)
    {
        var slug = new StringBuilder(SlugLength);

        foreach (var c in text.ToLowerInvariant())
        {
            if (slug.Length == SlugLength)
                break;

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        return slug.ToString().Trim('-');
    }

    /// <summary>What of <paramref name="profileDir"/> the sandbox may read but not change.</summary>
    public static List<string> ReadOnlyPaths(string profileDir)
    {
        var paths = new List<string>();

        foreach (var entry in Protected)
        {
            var path = Path.Combine(profileDir, entry);
            if (File.Exists(path) || Directory.Exists(path))
                paths.Add(path);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(profileDir))
            {
                if (paths.Contains(file, StringComparer.Ordinal))
                    continue;

                if (
                    ScriptExtensions.Contains(
                        Path.GetExtension(file),
                        StringComparer.OrdinalIgnoreCase
                    ) || IsExecutable(file)
                )
                    paths.Add(file);
            }
        }
        catch
        {
            // A profile that cannot be listed gets the fixed list above and nothing more.
        }

        return paths;
    }

    private static bool IsExecutable(string file)
    {
        if (OperatingSystem.IsWindows())
            return false;

        try
        {
            const UnixFileMode anyExecute =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            return (File.GetUnixFileMode(file) & anyExecute) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Throws if <paramref name="workspace"/> cannot be handed to a sandbox. A folder that holds
    /// ~/.tabard - your home directory, say - would give the sandbox the list that decides whether
    /// it is a sandbox, along with every other profile's login.
    /// </summary>
    public static void CheckWorkspace(string workspace)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Paths.TabardRoot));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));

        if (Contains(folder, root) || Contains(root, folder))
            throw new InvalidOperationException(
                $"cannot sandbox {Paths.Pretty(folder)}: it would give the sandbox ~/.tabard, and with "
                    + "it every profile. Launch from a project folder instead."
            );

        // sbx reads mounts as PATH[:TARGET][:ro], so a colon in the path itself would be taken for
        // one of those and mount something else.
        if (!OperatingSystem.IsWindows() && folder.Contains(':'))
            throw new InvalidOperationException(
                $"cannot sandbox {Paths.Pretty(folder)}: sbx cannot mount a path with ':' in it."
            );
    }

    private static bool Contains(string outer, string inner) =>
        string.Equals(outer, inner, StringComparison.Ordinal)
        || inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        // The filesystem root contains everything, and already ends in a separator.
        || (
            outer.EndsWith(Path.DirectorySeparatorChar)
            && inner.StartsWith(outer, StringComparison.Ordinal)
        );

    /// <summary>
    /// The arguments for 'sbx'. Creating takes the mounts, which sbx fixes at creation; going back
    /// into one that exists takes only the name, because sbx keeps the rest. The environment is
    /// passed both times - sbx applies it to the session, not just to the sandbox it creates.
    /// Permission prompts stay on unless <paramref name="claudeArgs"/> asks for bypass mode; see
    /// <see cref="KeepPrompts"/>.
    /// </summary>
    public static List<string> RunArgs(
        string sandbox,
        string profileDir,
        string workspace,
        bool exists,
        IReadOnlyList<string> claudeArgs
    )
    {
        var configDir = Path.GetFullPath(profileDir);

        List<string> args = exists ? ["run", "--name", sandbox] : ["run", Agent, "--name", sandbox];

        args.AddRange(["-e", $"CLAUDE_CONFIG_DIR={configDir}"]);

        if (!exists)
        {
            args.Add(workspace);
            args.Add(configDir);
            args.AddRange(ReadOnlyPaths(configDir).Select(path => $"{path}:ro"));
        }

        args.Add("--");

        if (!AsksToBypass(claudeArgs))
            args.AddRange(["--settings", KeepPrompts]);

        args.AddRange(claudeArgs);

        return args;
    }

    /// <summary>
    /// sbx's claude image launches <c>claude --dangerously-skip-permissions</c> and appends the
    /// agent arguments after it, so nothing passed here can take that flag back. This setting
    /// can: Claude Code refuses bypass mode while it is set, whatever the command line says, and
    /// falls back to asking. As a <c>--settings</c> argument it lasts for this session only and is
    /// never written into the profile, which a direct launch shares.
    /// </summary>
    internal const string KeepPrompts =
        """{"permissions":{"disableBypassPermissionsMode":"disable"}}""";

    /// <summary>Asking for bypass mode the way you would ask a direct launch for it is the opt-in:
    /// the sandbox then runs without prompts, as sbx intends.</summary>
    internal static bool AsksToBypass(IReadOnlyList<string> claudeArgs)
    {
        for (var i = 0; i < claudeArgs.Count; i++)
        {
            var arg = claudeArgs[i];

            if (arg is "--dangerously-skip-permissions" or "--allow-dangerously-skip-permissions")
                return true;

            if (arg == "--permission-mode=bypassPermissions")
                return true;

            if (
                arg == "--permission-mode"
                && i + 1 < claudeArgs.Count
                && claudeArgs[i + 1] == "bypassPermissions"
            )
                return true;
        }

        return false;
    }

    /// <summary>
    /// The settings file has to exist before the sandbox is made, because read-only only covers
    /// what is there: a profile without one would let the sandbox create it, hooks and all.
    /// </summary>
    public static void Prepare(string profileDir)
    {
        var settings = Settings.FileFor(profileDir);
        if (!File.Exists(settings))
            File.WriteAllText(settings, "{}\n");
    }

    /// <summary>
    /// Whether sbx already has a sandbox by this name. A failed listing counts as no: the create
    /// that follows will fail with sbx's own explanation - not signed in, daemon down - which is
    /// more use than anything tabard could say about it.
    /// </summary>
    public static bool Exists(string executable, string sandbox)
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("ls");
            psi.ArgumentList.Add("-q");

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            // Drained concurrently: a full stderr pipe would stall a process we are waiting on.
            var stderr = proc.StandardError.ReadToEndAsync();
            var stdout = proc.StandardOutput.ReadToEnd();

            if (!proc.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                proc.Kill(entireProcessTree: true);
                return false;
            }

            _ = stderr.Result;

            return proc.ExitCode == 0
                && stdout
                    .Split(
                        '\n',
                        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                    )
                    .Contains(sandbox, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Takes back the MCP server sbx registers in the profile's .claude.json when a sandbox starts.
    /// It points at a host name that only resolves inside a sandbox, so left there it would greet
    /// every session of this profile on the host with a server that fails to connect. Only entries
    /// aimed at <c>*.docker.internal</c> go; the file is rewritten only if there is one, and is
    /// otherwise left byte for byte as it was. Returns a problem to report, or null.
    /// </summary>
    public static string? ForgetGateway(string claudeJson)
    {
        var temp = $"{claudeJson}.{Environment.ProcessId}.tmp";

        try
        {
            if (!File.Exists(claudeJson))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(claudeJson));
            var root = doc.RootElement;

            if (
                root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind is not JsonValueKind.Object
                || !servers.EnumerateObject().Any(IsGateway)
            )
            {
                return null;
            }

            using (
                var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)
            )
            {
                CopyMode(claudeJson, temp);

                using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions
                    {
                        Indented = true,
                        // The file is Claude Code's, and holds paths and project names in whatever
                        // script they were written. Keep them readable rather than \u-escaped.
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }
                );

                writer.WriteStartObject();

                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals("mcpServers"))
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    writer.WriteStartObject();

                    foreach (var server in property.Value.EnumerateObject())
                    {
                        if (!IsGateway(server))
                            server.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            File.Move(temp, claudeJson, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // Inert if it lingers.
            }

            return $"could not remove the sandbox's MCP gateway from {Paths.Pretty(claudeJson)} ({ex.Message}).";
        }
    }

    private static bool IsGateway(JsonProperty server) =>
        server.Value.ValueKind is JsonValueKind.Object
        && server.Value.TryGetProperty("url", out var url)
        && url.ValueKind is JsonValueKind.String
        && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
        && (
            uri.Host.Equals("docker.internal", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".docker.internal", StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>.claude.json is 0600 when Claude Code made it - it names the account - and a
    /// rewrite must not loosen that.</summary>
    private static void CopyMode(string from, string to)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch
        {
            // Best effort, as for the profile directories.
        }
    }

    /// <summary>Shared by the picker's status line and 'tabard sandbox', so the key and the command
    /// say the same thing.</summary>
    public static string Status(string profile, bool on) =>
        on ? $"'{profile}' will launch in a Docker sandbox." : $"'{profile}' will launch directly.";

    /// <summary>
    /// The mark beside a sandboxed profile, and the pair the picker's legend shows for on and off.
    /// Single-width characters on purpose - rows are measured in UTF-16 units, so anything a
    /// terminal draws two columns wide would knock the row out of line - and plain ASCII when the
    /// console cannot encode them.
    /// </summary>
    public static string Mark(bool on) => on ? (Unicode ? "■" : "*") : " ";

    public static string Legend(bool on) => Unicode ? (on ? "■" : "□") : (on ? "*" : "-");

    private static bool Unicode
    {
        get
        {
            try
            {
                return Console.OutputEncoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;
            }
            catch
            {
                return false;
            }
        }
    }
}
