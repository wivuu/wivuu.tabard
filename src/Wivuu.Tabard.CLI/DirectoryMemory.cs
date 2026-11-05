using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// Which profile was last launched from which folder, so a bare 'tabard' can go straight back to it.
/// A hint over the profile directories, like ~/.tabard/last and ~/.tabard/order: it cannot conjure a
/// profile, and a name in it that has gone is the caller's cue to ask instead.
///
/// Everything here fails soft. Losing this file costs one extra trip through the selector, which is
/// never worth stopping a launch over. Callers that change it hold
/// <see cref="ProfileStore.AcquireLock"/>, or two launches racing would drop one another's folder.
/// </summary>
internal static class DirectoryMemory
{
    /// <summary>
    /// Windows hands back the casing the directory was entered with, so one folder turns up as both
    /// c:\ and C:\. Elsewhere getcwd reports the path as it is on disk, and two spellings really are
    /// two folders on a case-sensitive filesystem.
    /// </summary>
    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The profile last used in the current folder, or null if it has not been seen.</summary>
    public static string? Recall() => Current() is { } directory ? Recall(directory) : null;

    public static string? Recall(string directory) => Read().GetValueOrDefault(directory);

    /// <summary>Records the profile against the current folder.</summary>
    public static void Remember(string profile)
    {
        if (Current() is { } directory)
            Remember(directory, profile);
    }

    public static void Remember(string directory, string profile)
    {
        var entries = Read();
        entries[directory] = profile;
        Write(entries);
    }

    /// <summary>The folders name profiles, so a rename has to be carried over to them.</summary>
    public static void RenameProfile(string from, string to)
    {
        var entries = Read();
        var folders = FoldersUsing(entries, from);
        if (folders.Count == 0)
            return;

        foreach (var directory in folders)
            entries[directory] = to;

        Write(entries);
    }

    /// <summary>
    /// Not just tidiness: a profile created later under the same name, possibly on another account,
    /// must not inherit the folders this one was used in.
    /// </summary>
    public static void ForgetProfile(string profile)
    {
        var entries = Read();
        var folders = FoldersUsing(entries, profile);
        if (folders.Count == 0)
            return;

        foreach (var directory in folders)
            entries.Remove(directory);

        Write(entries);
    }

    private static List<string> FoldersUsing(Dictionary<string, string> entries, string profile) =>
        [
            .. entries
                .Where(entry => string.Equals(entry.Value, profile, StringComparison.Ordinal))
                .Select(entry => entry.Key),
        ];

    /// <summary>Throws on Unix when the folder has been deleted out from under the shell.</summary>
    private static string? Current()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> Read()
    {
        var entries = new Dictionary<string, string>(Comparer);

        try
        {
            if (!File.Exists(Paths.DirectoriesFile))
                return entries;

            using var stream = File.OpenRead(Paths.DirectoriesFile);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
                return entries;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                // Checked first because GetString throws on anything else, which would lose every
                // entry after a hand-edited one.
                if (
                    entry.Value.ValueKind is JsonValueKind.String
                    && entry.Value.GetString() is { Length: > 0 } profile
                )
                {
                    entries[entry.Name] = profile;
                }
            }
        }
        catch
        {
            // Unreadable, or not JSON at all. The next launch writes a good one over it.
        }

        return entries;
    }

    /// <summary>
    /// JSON rather than the one-per-line of ~/.tabard/order, because a path and a profile name can
    /// each hold a line break on Unix. Temp file plus rename, as for the other state files.
    /// </summary>
    private static void Write(Dictionary<string, string> entries)
    {
        var temp = $"{Paths.DirectoriesFile}.{Environment.ProcessId}.tmp";

        try
        {
            Directory.CreateDirectory(Paths.TabardRoot);

            using (
                var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)
            )
            using (
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })
            )
            {
                writer.WriteStartObject();

                foreach (var (directory, profile) in entries)
                    writer.WriteString(directory, profile);

                writer.WriteEndObject();
            }

            File.Move(temp, Paths.DirectoriesFile, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // A leftover .tmp is inert - nothing reads it.
            }
        }
    }
}
