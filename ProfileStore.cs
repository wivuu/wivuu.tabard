namespace Envy.Cli;

internal sealed record MigrationResult(bool Adopted, IReadOnlyList<string> Warnings)
{
    public static MigrationResult None { get; } = new(false, []);
}

internal static class ProfileStore
{
    public const string DefaultProfileName = "default";

    private const int MaxNameLength = 64;

    // Reserved on Windows even with an extension appended, and a profile store should stay portable.
    private static readonly string[] DeviceNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    ];

    /// <summary>
    /// Profiles are just directories, so nothing can drift out of sync with what is on disk.
    /// Metadata is not read here - most commands never display it, and ~/.claude.json can be
    /// tens of megabytes.
    /// </summary>
    public static List<Profile> List()
    {
        var profiles = new List<Profile>();
        if (!Directory.Exists(Paths.ProfilesRoot))
            return profiles;

        foreach (var dir in Directory.EnumerateDirectories(Paths.ProfilesRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                continue;

            profiles.Add(new Profile { Name = name, Dir = dir });
        }

        // Most recently used first so enter-enter is the common path.
        var last = LastUsed();
        profiles.Sort(
            (a, b) =>
            {
                var aLast = string.Equals(a.Name, last, StringComparison.Ordinal);
                var bLast = string.Equals(b.Name, last, StringComparison.Ordinal);
                if (aLast != bLast)
                    return aLast ? -1 : 1;

                // Ordinal tiebreak: names differing only in case must not compare equal, or an
                // unstable Sort would let them swap places between runs.
                var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
            }
        );

        return profiles;
    }

    /// <summary>
    /// Exact match only. Matching case-insensitively here would let 'envy rm Work' delete 'work'
    /// on a case-sensitive filesystem; Create() is what keeps the two from coexisting.
    /// </summary>
    public static Profile? Find(string name) =>
        List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public static string? LastUsed()
    {
        try
        {
            if (!File.Exists(Paths.LastUsedFile))
                return null;

            var value = File.ReadAllText(Paths.LastUsedFile).Trim();
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static void SetLastUsed(string name)
    {
        Directory.CreateDirectory(Paths.EnvyRoot);

        // Temp file plus rename: a half-written 'last' would point a bare 'claude' at nothing.
        var temp = $"{Paths.LastUsedFile}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, name);
        File.Move(temp, Paths.LastUsedFile, overwrite: true);
    }

    /// <summary>
    /// Serialises the 'record last used, then relink' pair against other envy processes, which
    /// would otherwise interleave and leave ~/.claude and ~/.envy/last naming different profiles.
    /// Best effort - a stuck lock must not stop someone launching Claude Code.
    /// </summary>
    public static IDisposable? AcquireLock()
    {
        try
        {
            Directory.CreateDirectory(Paths.EnvyRoot);
        }
        catch
        {
            return null;
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                return new FileStream(
                    Paths.LockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static Profile Create(string name)
    {
        ValidateName(name);

        // Case-insensitive: 'work' and 'Work' can both exist on a case-sensitive filesystem, and
        // then no lookup can tell them apart safely.
        if (
            List()
                .FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                ) is
            { } clash
        )
        {
            throw new InvalidOperationException($"Profile '{clash.Name}' already exists.");
        }

        var dir = Path.Combine(Paths.ProfilesRoot, name);
        Directory.CreateDirectory(dir);
        Harden(dir);

        return new Profile { Name = name, Dir = dir };
    }

    /// <summary>
    /// Deletes the profile directory first and drops the links only once that has succeeded, so a
    /// delete that fails part-way cannot strand ~/.claude. If the deleted profile was the linked
    /// one, ~/.claude is repointed at a survivor. Returns anything the caller should warn about.
    /// </summary>
    public static IReadOnlyList<string> Delete(Profile profile)
    {
        // Read these while the target still exists; both are needed after the directory is gone.
        var linkedDir = Links.PointsAt(Paths.ClaudeDir, profile.Dir);
        var linkedJson = Links.PointsAt(Paths.ClaudeJson, profile.ClaudeJsonFile);

        Directory.Delete(profile.Dir, recursive: true);

        if (linkedDir)
            Links.Unlink(Paths.ClaudeDir);

        if (linkedJson)
            Links.Unlink(Paths.ClaudeJson);

        if (string.Equals(LastUsed(), profile.Name, StringComparison.Ordinal))
        {
            try
            {
                File.Delete(Paths.LastUsedFile);
            }
            catch
            {
                // Harmless if it lingers - List() tolerates a name that no longer exists.
            }
        }

        if (!linkedDir && !linkedJson)
            return [];

        using var guard = AcquireLock();

        var survivor = (LastUsed() is { } name ? Find(name) : null) ?? List().FirstOrDefault();
        if (survivor is null)
            return []; // Nothing left to point at, and an absent ~/.claude is the honest state.

        SetLastUsed(survivor.Name);
        return Relink(survivor);
    }

    /// <summary>
    /// Points ~/.claude and ~/.claude.json at the given profile, creating the links if they are
    /// absent. Anything envy did not put there - a real file or directory, or a link aimed outside
    /// the profile store - belongs to the user and is left alone. Returns what could not be done.
    /// </summary>
    public static IReadOnlyList<string> Relink(Profile profile)
    {
        var warnings = new List<string>();
        var dir = Path.GetFullPath(profile.Dir);
        var json = Path.GetFullPath(profile.ClaudeJsonFile);
        var claudeDir = Classify(Paths.ClaudeDir);

        // Backing out has to cover both links, or a bare 'claude' would read one profile's config
        // dir alongside another profile's .claude.json.
        if (
            claudeDir is Owner.Ours
            && !Links.PointsAt(Paths.ClaudeDir, dir)
            && WouldOrphanLocalInstall(profile, warnings)
        )
        {
            return warnings;
        }

        switch (claudeDir)
        {
            case Owner.Absent:
                Link(Paths.ClaudeDir, dir, "~/.claude", directory: true, warnings);
                break;

            case Owner.Ours when !Links.PointsAt(Paths.ClaudeDir, dir):
                Links.Unlink(Paths.ClaudeDir);
                Link(Paths.ClaudeDir, dir, "~/.claude", directory: true, warnings);
                break;

            case Owner.Foreign:
                warnings.Add(
                    "~/.claude is a link pointing outside ~/.envy/profiles, so envy left it alone. "
                        + "A bare 'claude' will not follow your profile choice."
                );
                break;

            case Owner.Occupied:
                warnings.Add(
                    "~/.claude is real content envy did not create, so it was left alone. "
                        + "A bare 'claude' will not follow your profile choice."
                );
                break;
        }

        switch (Classify(Paths.ClaudeJson))
        {
            // Link it even though the file does not exist yet: Claude Code creates it on first
            // write and it lands inside the profile. Leaving the link at the previous profile
            // instead would cross-contaminate the two.
            case Owner.Absent:
                Link(Paths.ClaudeJson, json, "~/.claude.json", directory: false, warnings);
                break;

            case Owner.Ours when !Links.PointsAt(Paths.ClaudeJson, json):
                Links.Unlink(Paths.ClaudeJson);
                Link(Paths.ClaudeJson, json, "~/.claude.json", directory: false, warnings);
                break;

            case Owner.Foreign:
                warnings.Add(
                    "~/.claude.json is a link pointing outside ~/.envy/profiles, so envy left it alone."
                );
                break;

            case Owner.Occupied:
                warnings.Add(
                    "~/.claude.json is a real file envy did not create, so it was left alone. "
                        + $"Move it to {json} if you want this profile to own it."
                );
                break;
        }

        return warnings;
    }

    /// <summary>True if a first run would adopt ~/.claude. Reads only, so 'envy ls' can say so
    /// without performing the move.</summary>
    public static bool WouldAdopt() =>
        !AnyProfiles() && Directory.Exists(Paths.ClaudeDir) && !Links.IsLink(Paths.ClaudeDir);

    /// <summary>
    /// First run only: adopt an existing login as the 'default' profile by moving
    /// ~/.claude into the store and linking the original path back at it. Moving rather
    /// than copying keeps tokens, expiry and history intact with no second copy to go stale.
    /// </summary>
    public static MigrationResult AdoptExistingIfNeeded()
    {
        Directory.CreateDirectory(Paths.ProfilesRoot);
        Harden(Paths.EnvyRoot);

        // Nothing to adopt: fresh install, or already linked by a previous run.
        if (!WouldAdopt())
            return MigrationResult.None;

        var warnings = new List<string>();
        var target = Path.Combine(Paths.ProfilesRoot, DefaultProfileName);

        try
        {
            MoveDirectory(Paths.ClaudeDir, target, warnings);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"could not move ~/.claude into {target} ({ex.Message}) - your ~/.claude was left "
                    + "where it is. Quit any running 'claude', check there is disk space, then try again.",
                ex
            );
        }

        Harden(target);
        AdoptClaudeJson(target, warnings);

        // The links themselves are Relink's job, and every caller of this reaches it.
        return new MigrationResult(true, warnings);
    }

    private static void AdoptClaudeJson(string profileDir, List<string> warnings)
    {
        var inProfile = Path.Combine(profileDir, ".claude.json");

        try
        {
            if (!File.Exists(Paths.ClaudeJson) || Links.IsLink(Paths.ClaudeJson))
                return;

            // The file at ~/.claude.json is the one Claude Code actually reads, so it wins.
            // Anything that came along inside the config dir is a vestige of an older layout;
            // keep it, but under a name nothing will load.
            if (File.Exists(inProfile))
            {
                var backup = Unique(inProfile + ".vestigial");
                File.Move(inProfile, backup);
                warnings.Add(
                    $"the adopted config dir had its own .claude.json; kept it as {backup}."
                );
            }

            File.Move(Paths.ClaudeJson, inProfile);
        }
        catch (Exception ex)
        {
            warnings.Add($"could not adopt ~/.claude.json ({ex.Message}).");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Profile name cannot be empty.");

        if (name.Length > MaxNameLength)
            throw new ArgumentException(
                $"Profile name is too long - keep it under {MaxNameLength} characters."
            );

        if (
            name.StartsWith('.')
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/')
            || name.Contains('\\')
            || name.EndsWith('.')
            || name.EndsWith(' ')
            || IsDeviceName(name)
        )
        {
            throw new ArgumentException($"'{name}' is not a valid profile name.");
        }
    }

    private static bool IsDeviceName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        return DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static bool AnyProfiles() =>
        Directory.Exists(Paths.ProfilesRoot)
        && Directory.EnumerateDirectories(Paths.ProfilesRoot).Any();

    private enum Owner
    {
        Absent,
        Ours,
        Foreign,
        Occupied,
    }

    private static Owner Classify(string path)
    {
        if (Links.ResolveTarget(path) is { } target)
            return Links.IsInside(target, Paths.ProfilesRoot) ? Owner.Ours : Owner.Foreign;

        return Path.Exists(path) ? Owner.Occupied : Owner.Absent;
    }

    private static void Link(
        string linkPath,
        string target,
        string label,
        bool directory,
        List<string> warnings
    )
    {
        string error;
        var created = directory
            ? Links.TryCreateDirectoryLink(linkPath, target, out error)
            : Links.TryCreateFileLink(linkPath, target, out error);

        if (created)
            return;

        warnings.Add(
            $"could not link {label} at {target} ({error}). envy itself still works - only a bare "
                + "'claude' is affected. On Windows, enable Developer Mode to allow links."
        );
    }

    /// <summary>
    /// 'claude migrate-installer' puts the binary in ~/.claude/local and points ~/.local/bin/claude
    /// at it, so repointing ~/.claude at a profile without one breaks the claude command machine-wide
    /// - including envy's own launch. Refusing costs only the bare-'claude' convenience, because this
    /// session still gets the profile through CLAUDE_CONFIG_DIR.
    /// </summary>
    private static bool WouldOrphanLocalInstall(Profile profile, List<string> warnings)
    {
        if (
            Links.ResolveTarget(Paths.ClaudeDir) is not { } current
            || !Directory.Exists(Path.Combine(current, "local"))
            || Directory.Exists(Path.Combine(profile.Dir, "local"))
        )
        {
            return false;
        }

        warnings.Add(
            $"~/.claude holds a migrate-installer install (local/) and '{profile.Name}' does not, so "
                + "repointing it would break the 'claude' command. Left it where it was - this session "
                + "still uses the profile via CLAUDE_CONFIG_DIR."
        );

        return true;
    }

    private static string Unique(string path)
    {
        if (!Path.Exists(path))
            return path;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{path}.{n}";
            if (!Path.Exists(candidate))
                return candidate;
        }

        return $"{path}.{Guid.NewGuid():N}";
    }

    private static void Harden(string dir)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            // 0700 - these directories hold OAuth tokens.
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        catch
        {
            // Best effort.
        }
    }

    private static void MoveDirectory(string source, string destination, List<string> warnings)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Almost always a cross-volume rename, which POSIX and Win32 both refuse. Anything
            // else that lands here has changed nothing either, so guessing wrong costs a copy.
        }

        // Stage into a sibling of the profiles root: a copy that dies part-way must not leave a
        // half-populated profiles/default behind, because that looks adopted and is never retried.
        var staging = Path.Combine(Paths.EnvyRoot, $".incoming-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(source, staging);
            Directory.Move(staging, destination);
        }
        catch
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // Invisible to List() where it is, so not worth failing over.
            }

            throw;
        }

        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"copied ~/.claude into the profile but could not remove the original ({ex.Message}); "
                    + "delete it by hand once you are happy with the new profile."
            );
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);

            // Recreate links rather than following them: ~/.claude/local is an npm tree full of
            // symlinked bins, and the source is deleted once this returns.
            if (entry.LinkTarget is { } linkTarget)
            {
                if (entry is DirectoryInfo)
                    Directory.CreateSymbolicLink(target, linkTarget);
                else
                    File.CreateSymbolicLink(target, linkTarget);
            }
            else if (entry is DirectoryInfo)
            {
                CopyDirectory(entry.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target, overwrite: true);
            }
        }
    }
}
