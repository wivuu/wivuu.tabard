using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// Docker Sandboxes (<c>sbx</c>): everything about a sandboxed launch that is sbx's own - its
/// command line, how it names sandboxes, the images they are made from, and what it leaves behind
/// in a profile. <see cref="SbxSandbox"/> is the <see cref="ISandbox"/> built on it; what is not
/// sbx's - which profiles are sandboxed, what a launch mounts - stays in <see cref="Sandboxing"/>.
/// </summary>
internal static partial class Sbx
{
    public const string Executable = "sbx";

    /// <summary>The 'sbx' agent tabard launches. One of sbx's built-ins.</summary>
    private const string Agent = "claude";

    /// <summary>sbx refuses longer names.</summary>
    private const int MaxNameLength = 63;

    private const int SlugLength = 20;

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
            args.AddRange(Sandboxing.ReadOnlyPaths(configDir).Select(path => $"{path}:ro"));
        }

        args.Add("--");

        if (!AsksToBypass(claudeArgs))
            args.AddRange(["--settings", KeepPrompts]);

        args.AddRange(claudeArgs);

        return args;
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
}
