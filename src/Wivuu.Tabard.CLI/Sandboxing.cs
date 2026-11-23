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

    /// <summary>Called by <see cref="ProfileStore.Rename"/> and <see cref="ProfileStore.Delete"/>
    /// whether or not the profile is sandboxed: an image can be set while sandboxing is off.</summary>
    public static void MoveImage(string from, string? to)
    {
        try
        {
            var images = Images();
            if (!images.Remove(from, out var image))
                return;

            if (to is not null)
                images[to] = image;

            WriteImages(images);
        }
        catch
        {
            // Housekeeping riding along with a rename or delete that has already changed the disk.
        }
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
    public static string SandboxName(string profile, string workspace, string? image = null)
    {
        // The image is part of the key only when there is one, so the sandboxes made before images
        // could be chosen keep their names. With it, choosing another image makes a new sandbox
        // rather than going back into one sbx made from the old image.
        var key = image is { Length: > 0 }
            ? $"{profile}\0{workspace}\0{image}"
            : $"{profile}\0{workspace}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

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
        if (Unmountable(workspace) is { } problem)
            throw new InvalidOperationException(problem);
    }

    /// <summary>Why <paramref name="path"/> cannot be mounted into a sandbox, or null.</summary>
    private static string? Unmountable(string path)
    {
        try
        {
            CheckMount(path);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private static void CheckMount(string workspace)
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
    /// The arguments for 'sbx'. Creating takes the image and the mounts, which sbx fixes at
    /// creation; going back into one that exists takes only the name, because sbx keeps the rest.
    /// The environment and the working directory are passed both times - sbx applies them to the
    /// session, not just to the sandbox it creates. Permission prompts stay on unless
    /// <paramref name="claudeArgs"/> asks for bypass mode; see <see cref="KeepPrompts"/>.
    /// </summary>
    public static List<string> RunArgs(
        string sandbox,
        string profileDir,
        Workspace workspace,
        bool exists,
        IReadOnlyList<string> claudeArgs,
        string? image = null,
        IReadOnlyList<string>? environment = null
    )
    {
        var configDir = Path.GetFullPath(profileDir);

        List<string> args = exists ? ["run", "--name", sandbox] : ["run", Agent, "--name", sandbox];

        if (!exists && image is { Length: > 0 })
            args.AddRange(["-t", image]);

        if (!string.Equals(workspace.WorkDir, workspace.Root, StringComparison.Ordinal))
            args.AddRange(["-w", workspace.WorkDir]);

        args.AddRange(["-e", $"CLAUDE_CONFIG_DIR={configDir}"]);

        foreach (var variable in environment ?? [])
            args.AddRange(["-e", variable]);

        if (!exists)
        {
            args.Add(workspace.Root);
            args.AddRange(workspace.Writable);
            args.Add(configDir);
            args.AddRange(workspace.ReadOnly.Select(path => $"{path}:ro"));
            args.AddRange(ReadOnlyPaths(configDir).Select(path => $"{path}:ro"));
        }

        args.Add("--");

        if (!AsksToBypass(claudeArgs))
            args.AddRange(["--settings", KeepPrompts]);

        args.AddRange(claudeArgs);

        return args;
    }

    /// <summary>
    /// What a launch from <paramref name="cwd"/> mounts. Inside a git repository that is the whole
    /// repository - launched from a subfolder, the sandbox would otherwise have no .git to find -
    /// plus, for a worktree or a submodule, the git directory its .git file points at elsewhere.
    /// The session still starts in <paramref name="cwd"/>.
    ///
    /// The repository's hooks and config are mounted read-only: git runs hooks, and config can name
    /// programs (core.hooksPath, core.fsmonitor, aliases), so either would let the sandbox plant
    /// something that runs here the next time you use git on this machine.
    ///
    /// A repository that cannot be mounted - one rooted at your home directory, say - falls back to
    /// the folder alone, with a warning. Throws only when that folder cannot be mounted either.
    /// </summary>
    public static Workspace ResolveWorkspace(string cwd, List<string> warnings)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        CheckWorkspace(folder);

        if (FindRepository(folder) is not { } repo)
            return new Workspace(folder, folder, [], []);

        var writable = repo
            .GitDirs.Where(dir => !Contains(repo.Root, dir))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (
            new[] { repo.Root }
                .Concat(writable)
                .Select(Unmountable)
                .FirstOrDefault(p => p is not null) is
            { } problem
        )
        {
            warnings.Add(
                $"mounting only {Paths.Pretty(folder)}, not its git repository - {problem.Replace("cannot sandbox ", "")} "
                    + "git will not work inside the sandbox."
            );
            return new Workspace(folder, folder, [], []);
        }

        var readOnly = new List<string>();
        foreach (var dir in repo.GitDirs)
        {
            foreach (var entry in new[] { "hooks", "config" })
            {
                var path = Path.Combine(dir, entry);
                if ((File.Exists(path) || Directory.Exists(path)) && !readOnly.Contains(path))
                    readOnly.Add(path);
            }
        }

        return new Workspace(repo.Root, folder, writable, readOnly);
    }

    private sealed record Repository(string Root, IReadOnlyList<string> GitDirs);

    /// <summary>
    /// The nearest enclosing work tree, found the way git finds it: the first folder up from
    /// <paramref name="folder"/> holding a .git. A .git directory is the git directory; a .git file
    /// is a worktree or a submodule pointing at one elsewhere, and a worktree's git directory names
    /// the shared one it belongs to in 'commondir'. Fails soft to null.
    /// </summary>
    private static Repository? FindRepository(string folder)
    {
        try
        {
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");

                if (Directory.Exists(dotGit))
                    return new Repository(Trim(dir.FullName), [Trim(dotGit)]);

                if (!File.Exists(dotGit))
                    continue;

                var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim() ?? "";
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                    return null;

                var gitDir = Trim(Path.GetFullPath(line["gitdir:".Length..].Trim(), dir.FullName));
                var dirs = new List<string> { gitDir };

                var commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile))
                {
                    var common = File.ReadAllText(commonFile).Trim();
                    if (common.Length > 0)
                        dirs.Insert(0, Trim(Path.GetFullPath(common, gitDir)));
                }

                // A worktree's own git directory sits inside the shared one, which covers it.
                var mounts = dirs.Where(d => !dirs.Any(o => o != d && Contains(o, d))).ToList();
                return new Repository(Trim(dir.FullName), mounts);
            }
        }
        catch
        {
            // An unreadable .git means mounting the folder alone, as before repositories counted.
        }

        return null;

        static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>
    /// Your git identity, for commits made inside the sandbox: its home is not yours, so it has
    /// no ~/.gitconfig of yours to read. Asked of 'git var' in <paramref name="cwd"/>, which answers
    /// with exactly what a commit made here would record - config, an includeIf for this folder,
    /// GIT_AUTHOR_* in the environment, or the name and address git makes up from your account
    /// when none of those says. Empty when git is missing or refuses (user.useConfigOnly).
    /// </summary>
    public static List<string> GitIdentity(string cwd)
    {
        var variables = new List<string>();

        foreach (var role in new[] { "AUTHOR", "COMMITTER" })
        {
            if (ParseIdent(Git(cwd, "var", $"GIT_{role}_IDENT")) is not var (name, email))
                continue;

            variables.Add($"GIT_{role}_NAME={name}");
            variables.Add($"GIT_{role}_EMAIL={email}");
        }

        return variables;
    }

    /// <summary>'Name &lt;email&gt; 1700000000 +0000' into its name and address.</summary>
    internal static (string Name, string Email)? ParseIdent(string? ident)
    {
        if (ident is null)
            return null;

        var open = ident.IndexOf('<');
        var close = ident.IndexOf('>', open + 1);
        if (open < 0 || close < 0)
            return null;

        var name = ident[..open].Trim();
        var email = ident[(open + 1)..close].Trim();

        if (
            name.Length == 0
            || email.Length == 0
            || $"{name}{email}".AsSpan().IndexOfAny('\r', '\n') >= 0
        )
            return null;

        return (name, email);
    }

    private static string? Git(string cwd, params string[] args)
    {
        try
        {
            if (Launcher.Which("git") is not { } git)
                return null;

            var psi = new ProcessStartInfo(git)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = cwd,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            var stderr = proc.StandardError.ReadToEndAsync();
            var value = proc.StandardOutput.ReadToEnd().Trim();

            if (!proc.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                proc.Kill(entireProcessTree: true);
                return null;
            }

            _ = stderr.Result;
            return proc.ExitCode == 0 && value.Length > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The image a profile's sandboxes are made from, or null for sbx's default. Kept like the
    /// on/off list - '&lt;image&gt; &lt;name&gt;' per line, an image never holding a space - and failing
    /// soft to the default the same way.
    /// </summary>
    public static Dictionary<string, string> Images()
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(Paths.ImagesFile))
                return images;

            foreach (var line in File.ReadLines(Paths.ImagesFile))
            {
                var space = line.IndexOf(' ');
                if (space <= 0)
                    continue;

                var name = line[(space + 1)..].Trim();
                if (name.Length > 0)
                    images[name] = line[..space];
            }
        }
        catch
        {
            images.Clear();
        }

        return images;
    }

    public static string? ImageOf(string profile) =>
        Images().TryGetValue(profile, out var image) ? image : null;

    /// <summary>Null goes back to sbx's default. Callers hold
    /// <see cref="ProfileStore.AcquireLock"/>, as for <see cref="Set"/>.</summary>
    public static void SetImage(string profile, string? image)
    {
        if (image is not null && (image.Length == 0 || image.Any(char.IsWhiteSpace)))
            throw new ArgumentException($"'{image}' is not an image reference.");

        if (image is not null && profile.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new InvalidOperationException(
                "cannot set an image for a profile whose name holds a line break - rename it first."
            );

        var images = Images();
        var changed = image is null
            ? images.Remove(profile)
            : !images.TryGetValue(profile, out var was) || was != image;

        if (image is not null)
            images[profile] = image;

        if (changed)
            WriteImages(images);
    }

    private static void WriteImages(Dictionary<string, string> images)
    {
        Directory.CreateDirectory(Paths.TabardRoot);

        var temp = $"{Paths.ImagesFile}.{Environment.ProcessId}.tmp";
        File.WriteAllLines(
            temp,
            images
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Value} {pair.Key}")
        );
        File.Move(temp, Paths.ImagesFile, overwrite: true);
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

/// <summary>What a sandbox mounts: <see cref="Root"/> read-write, as are <see cref="Writable"/>
/// (git directories outside it), <see cref="ReadOnly"/> read-only, and the session starts in
/// <see cref="WorkDir"/>.</summary>
internal sealed record Workspace(
    string Root,
    string WorkDir,
    IReadOnlyList<string> Writable,
    IReadOnlyList<string> ReadOnly
);
