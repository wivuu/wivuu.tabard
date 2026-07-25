namespace Envy.Cli;

internal sealed record MigrationResult(bool Adopted, IReadOnlyList<string> Warnings)
{
    public static MigrationResult None { get; } = new(false, []);
}

internal static class ProfileStore
{
    public const string DefaultProfileName = "default";

    /// <summary>Profiles are just directories - there is no index file to drift out of sync.</summary>
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

            var profile = new Profile { Name = name, Dir = dir };
            profile.ReadMetadata();
            profiles.Add(profile);
        }

        // Most recently used first so enter-enter is the common path.
        var last = LastUsed();
        profiles.Sort((a, b) =>
        {
            var aLast = string.Equals(a.Name, last, StringComparison.Ordinal);
            var bLast = string.Equals(b.Name, last, StringComparison.Ordinal);
            if (aLast != bLast)
                return aLast ? -1 : 1;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return profiles;
    }

    public static Profile? Find(string name) =>
        List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

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
        File.WriteAllText(Paths.LastUsedFile, name);
    }

    public static Profile Create(string name)
    {
        ValidateName(name);

        var dir = Path.Combine(Paths.ProfilesRoot, name);
        if (Directory.Exists(dir))
            throw new InvalidOperationException($"Profile '{name}' already exists.");

        Directory.CreateDirectory(dir);
        Harden(dir);

        return new Profile { Name = name, Dir = dir };
    }

    public static void Delete(Profile profile)
    {
        // Drop any link pointing here first, so we never delete through a link
        // and never leave a dangling ~/.claude behind.
        if (Links.PointsAt(Paths.ClaudeDir, profile.Dir))
            Links.Unlink(Paths.ClaudeDir);

        if (Links.PointsAt(Paths.ClaudeJson, profile.ClaudeJsonFile))
            Links.Unlink(Paths.ClaudeJson);

        Directory.Delete(profile.Dir, recursive: true);

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
    }

    /// <summary>
    /// Points ~/.claude (and ~/.claude.json) at the given profile, but only if they are
    /// already links we own. A real directory there is the user's, and we leave it alone.
    /// </summary>
    public static void Relink(Profile profile)
    {
        if (Links.IsLink(Paths.ClaudeDir) && !Links.PointsAt(Paths.ClaudeDir, profile.Dir))
        {
            Links.Unlink(Paths.ClaudeDir);
            Links.TryCreateDirectoryLink(Paths.ClaudeDir, Path.GetFullPath(profile.Dir), out _);
        }

        if (Links.IsLink(Paths.ClaudeJson) && !Links.PointsAt(Paths.ClaudeJson, profile.ClaudeJsonFile))
        {
            Links.Unlink(Paths.ClaudeJson);
            if (File.Exists(profile.ClaudeJsonFile))
                Links.TryCreateFileLink(Paths.ClaudeJson, Path.GetFullPath(profile.ClaudeJsonFile), out _);
        }
    }

    /// <summary>
    /// First run only: adopt an existing login as the 'default' profile by moving
    /// ~/.claude into the store and linking the original path back at it. Moving rather
    /// than copying keeps tokens, expiry and history intact with no second copy to go stale.
    /// </summary>
    public static MigrationResult AdoptExistingIfNeeded()
    {
        Directory.CreateDirectory(Paths.ProfilesRoot);
        Harden(Paths.EnvyRoot);

        if (Directory.EnumerateDirectories(Paths.ProfilesRoot).Any())
            return MigrationResult.None;

        // Nothing to adopt: fresh install, or already linked by a previous run.
        if (!Directory.Exists(Paths.ClaudeDir) || Links.IsLink(Paths.ClaudeDir))
            return MigrationResult.None;

        var warnings = new List<string>();
        var target = Path.Combine(Paths.ProfilesRoot, DefaultProfileName);

        MoveDirectory(Paths.ClaudeDir, target);
        Harden(target);

        if (!Links.TryCreateDirectoryLink(Paths.ClaudeDir, target, out var dirError))
        {
            warnings.Add(
                $"could not link ~/.claude back to the default profile ({dirError}). "
                + "envy still works; running 'claude' directly will start a fresh login. "
                + "On Windows, enable Developer Mode to allow links.");
        }

        AdoptClaudeJson(target, warnings);

        return new MigrationResult(true, warnings);
    }

    private static void AdoptClaudeJson(string profileDir, List<string> warnings)
    {
        var inProfile = Path.Combine(profileDir, ".claude.json");

        try
        {
            if (File.Exists(Paths.ClaudeJson) && !Links.IsLink(Paths.ClaudeJson))
            {
                // If the moved config dir already had one, keep both rather than clobber.
                if (File.Exists(inProfile))
                    File.Move(Paths.ClaudeJson, inProfile + ".home-backup", overwrite: true);
                else
                    File.Move(Paths.ClaudeJson, inProfile);
            }

            if (File.Exists(inProfile) && !File.Exists(Paths.ClaudeJson)
                && !Links.TryCreateFileLink(Paths.ClaudeJson, inProfile, out var fileError))
            {
                warnings.Add($"could not link ~/.claude.json ({fileError}).");
            }
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

        if (name.StartsWith('.') || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\'))
        {
            throw new ArgumentException($"'{name}' is not a valid profile name.");
        }
    }

    private static void Harden(string dir)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            // 0700 - these directories hold OAuth tokens.
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Different volume - fall through to copy + delete.
        }

        CopyDirectory(source, destination);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
