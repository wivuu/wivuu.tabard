using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// How a sandboxed profile is sandboxed: which kind of sandbox (<see cref="Sandboxes"/>), and the
/// sites beyond its agent's own that it may reach. Whether it is sandboxed at all stays in
/// <see cref="Sandboxing"/>'s list, so switching a profile off and on keeps these.
///
/// Kept in ~/.tabard/sandbox.json, outside every profile, for the same reason as that list: a
/// sandbox can write to its own profile, and must not be able to loosen itself. A profile with
/// nothing set uses the default kind and only its agent's own sites.
/// </summary>
internal static partial class SandboxOptions
{
    private sealed record Entry(
        string? Kind,
        IReadOnlyList<string> Allow,
        IReadOnlyList<string> Block
    )
    {
        public bool IsEmpty => Kind is null && Allow.Count == 0 && Block.Count == 0;

        public bool Same(Entry other) =>
            Kind == other.Kind
            && Allow.SequenceEqual(other.Allow)
            && Block.SequenceEqual(other.Block);
    }

    private static readonly Entry Empty = new(null, [], []);

    /// <summary>The kind the profile chose, or null to use whichever is installed
    /// (<see cref="Sandboxes.Default"/>).</summary>
    public static string? KindOf(string profile) => Read().GetValueOrDefault(profile)?.Kind;

    /// <summary>The extra sites the profile's sandboxes may reach, as typed.</summary>
    public static IReadOnlyList<string> AllowedOf(string profile) =>
        Read().GetValueOrDefault(profile)?.Allow ?? [];

    /// <summary>
    /// Chooses the kind of sandbox by its <see cref="ISandbox.Name"/>; null or blank goes back to
    /// whichever is installed. A choice is kept as made, so it does not change under the profile
    /// when another kind is installed later. Callers hold <see cref="ProfileStore.AcquireLock"/>.
    /// </summary>
    public static void SetKind(string profile, string? kind)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();

        if (kind is not null)
        {
            kind = (
                Sandboxes.Named(kind)
                ?? throw new ArgumentException(
                    $"'{kind}' is not a kind of sandbox - try {string.Join(" or ", Sandboxes.All.Select(s => s.Name))}."
                )
            ).Name;
        }

        Update(profile, entry => entry with { Kind = kind });
    }

    /// <summary>
    /// Replaces the extra sites with those in <paramref name="text"/>, separated by commas or
    /// spaces; blank clears them. Throws an <see cref="ArgumentException"/> worded for the editor's
    /// status line at the first one that is not a site. Callers hold
    /// <see cref="ProfileStore.AcquireLock"/>.
    /// </summary>
    public static void SetAllowed(string profile, string? text) =>
        Update(profile, entry => entry with { Allow = ParseSites(text) });

    /// <summary>The default sites (<see cref="SrtSandbox.DeveloperSites"/>) the profile has taken
    /// off its sandboxes' list.</summary>
    public static IReadOnlyList<string> BlockedOf(string profile) =>
        Read().GetValueOrDefault(profile)?.Block ?? [];

    /// <summary>Takes a default site off the profile's list, or puts it back. Callers hold
    /// <see cref="ProfileStore.AcquireLock"/>.</summary>
    public static void SetBlocked(string profile, string site, bool blocked) =>
        Update(
            profile,
            entry =>
                entry with
                {
                    Block = blocked
                        ? [.. entry.Block.Append(site).Distinct(StringComparer.OrdinalIgnoreCase)]
                        :
                        [
                            .. entry.Block.Where(s =>
                                !string.Equals(s, site, StringComparison.OrdinalIgnoreCase)
                            ),
                        ],
                }
        );

    /// <summary>Every default site back on the list. Callers hold
    /// <see cref="ProfileStore.AcquireLock"/>.</summary>
    public static void ClearBlocked(string profile) =>
        Update(profile, entry => entry with { Block = [] });

    internal static List<string> ParseSites(string? text)
    {
        var sites = new List<string>();

        foreach (
            var site in (text ?? "").Split(
                [',', ' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (!SitePattern().IsMatch(site) || site.Length > 253)
                throw new ArgumentException(
                    $"'{site}' is not a site - try github.com, *.npmjs.org or localhost:3000."
                );

            if (!sites.Contains(site, StringComparer.OrdinalIgnoreCase))
                sites.Add(site);
        }

        return sites;
    }

    /// <summary>A host name, optionally starting '*.', or an IPv4 address, optionally with a port.
    /// </summary>
    [GeneratedRegex(
        @"^(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*(:[0-9]{1,5})?$"
    )]
    private static partial Regex SitePattern();

    public static void RenameProfile(string from, string to)
    {
        try
        {
            var entries = Read();
            if (!entries.Remove(from, out var entry))
                return;

            entries[to] = entry;
            Write(entries);
        }
        catch
        {
            // Housekeeping riding along with a rename that has already changed the disk.
        }
    }

    public static void ForgetProfile(string profile)
    {
        try
        {
            var entries = Read();
            if (entries.Remove(profile))
                Write(entries);
        }
        catch
        {
            // Likewise for a delete.
        }
    }

    private static void Update(string profile, Func<Entry, Entry> change)
    {
        var entries = Read();
        var was = entries.GetValueOrDefault(profile) ?? Empty;
        var now = change(was);

        if (now.Same(was))
            return;

        if (now.IsEmpty)
            entries.Remove(profile);
        else
            entries[profile] = now;

        Write(entries);
    }

    /// <summary>Fails soft: a file that cannot be read means defaults everywhere, which is still a
    /// sandbox - only the kind may differ from the one chosen, and no extra site is open.</summary>
    private static Dictionary<string, Entry> Read()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(Paths.SandboxOptionsFile))
                return entries;

            using var stream = File.OpenRead(Paths.SandboxOptionsFile);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
                return entries;

            foreach (var profile in doc.RootElement.EnumerateObject())
            {
                if (profile.Value.ValueKind is not JsonValueKind.Object)
                    continue;

                string? kind = null;
                if (
                    profile.Value.TryGetProperty("kind", out var k)
                    && k.ValueKind is JsonValueKind.String
                    && Sandboxes.Named(k.GetString()!) is { } sandbox
                )
                {
                    kind = sandbox.Name;
                }

                var entry = new Entry(
                    kind,
                    Sites(profile.Value, "allow"),
                    Sites(profile.Value, "block")
                );
                if (!entry.IsEmpty)
                    entries[profile.Name] = entry;
            }
        }
        catch
        {
            entries.Clear();
        }

        return entries;
    }

    private static List<string> Sites(JsonElement profile, string name)
    {
        var sites = new List<string>();
        if (
            !profile.TryGetProperty(name, out var list) || list.ValueKind is not JsonValueKind.Array
        )
            return sites;

        foreach (var site in list.EnumerateArray())
        {
            // Each one is checked again, so a hand-edited file cannot slip srt something that is
            // not a site.
            if (
                site.ValueKind is JsonValueKind.String
                && site.GetString() is { } text
                && SitePattern().IsMatch(text)
            )
            {
                sites.Add(text);
            }
        }

        return sites;
    }

    private static void Write(Dictionary<string, Entry> entries)
    {
        Directory.CreateDirectory(Paths.TabardRoot);

        var temp = $"{Paths.SandboxOptionsFile}.{Environment.ProcessId}.tmp";

        try
        {
            using (
                var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)
            )
            using (
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })
            )
            {
                writer.WriteStartObject();

                foreach (
                    var (profile, entry) in entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                )
                {
                    writer.WriteStartObject(profile);

                    if (entry.Kind is { } kind)
                        writer.WriteString("kind", kind);

                    foreach (
                        var (name, sites) in new[]
                        {
                            ("allow", entry.Allow),
                            ("block", entry.Block),
                        }
                    )
                    {
                        if (sites.Count == 0)
                            continue;

                        writer.WriteStartArray(name);
                        foreach (var site in sites)
                            writer.WriteStringValue(site);
                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            File.Move(temp, Paths.SandboxOptionsFile, overwrite: true);
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

            throw;
        }
    }
}
