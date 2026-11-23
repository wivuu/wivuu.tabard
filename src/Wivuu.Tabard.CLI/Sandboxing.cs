using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// Which profiles launch inside a sandbox, and what any sandbox is given - whichever kind of
/// sandbox it is (<see cref="Sandboxes"/>).
///
/// A sandboxed launch keeps the rest of tabard's model: the profile is still just a directory
/// handed to the agent. The difference is that the agent runs somewhere that sees the current
/// folder and the profile's directory - both at the same paths they have here, so the per-project
/// entries in .claude.json still line up - and nothing else of the host.
///
/// The on/off list is a hint like ~/.tabard/order: a name in it for a profile that has gone is
/// ignored. It lives outside every profile, because the sandbox can write to its own profile and
/// must not be able to switch itself off.
/// </summary>
internal static class Sandboxing
{
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

    /// <summary>
    /// pi's equivalent. Extensions are code pi loads on start; settings.json can name packages to
    /// install and run; models.json says where pi sends its prompts; the rest instruct it. auth.json
    /// and sessions stay writable, for the same reasons as Claude Code's credentials and history.
    /// </summary>
    private static readonly string[] PiProtected =
    [
        "settings.json",
        "models.json",
        "keybindings.json",
        "AGENTS.md",
        "SYSTEM.md",
        "APPEND_SYSTEM.md",
        "extensions",
        "skills",
        "prompts",
        "themes",
        "packages",
        "bin",
    ];

    /// <summary>What has to exist before a pi sandbox is made, because read-only only covers what
    /// is there: a missing extensions folder is one the sandbox could create, and fill.</summary>
    private static readonly string[] PiFolders = ["extensions", "skills", "prompts", "themes"];

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

    /// <summary>What of <paramref name="profileDir"/> the sandbox may read but not change.</summary>
    public static List<string> ReadOnlyPaths(string profileDir, Harness harness = Harness.Claude)
    {
        var paths = new List<string>();

        foreach (var entry in harness is Harness.Pi ? PiProtected : Protected)
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
    /// Fills in what <see cref="ReadOnlyPaths"/> has to find, because read-only only covers what
    /// exists: a profile without a settings file would let the sandbox create one, hooks and all.
    /// </summary>
    public static void Prepare(string profileDir, Harness harness = Harness.Claude)
    {
        var settings = Settings.FileFor(profileDir);
        if (!File.Exists(settings))
            File.WriteAllText(settings, "{}\n");

        if (harness is not Harness.Pi)
            return;

        foreach (var folder in PiFolders)
            Directory.CreateDirectory(Path.Combine(profileDir, folder));
    }

    /// <summary>Shared by the picker's status line and 'tabard sandbox', so the key and the command
    /// say the same thing.</summary>
    public static string Status(string profile, bool on) =>
        on
            ? $"'{profile}' will launch in {Sandboxes.For(profile).Description}."
            : $"'{profile}' will launch directly.";

    /// <summary>
    /// The mark beside a sandboxed profile, and the pair the picker's legend shows for on and off.
    /// Single-width characters on purpose - rows are measured in UTF-16 units, so anything a
    /// terminal draws two columns wide would knock the row out of line - and plain ASCII when the
    /// console cannot encode them.
    /// </summary>
    public static string Mark(bool on) => on ? (Term.Unicode ? "■" : "*") : " ";

    public static string Legend(bool on) => Term.Unicode ? (on ? "■" : "□") : (on ? "*" : "-");
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
