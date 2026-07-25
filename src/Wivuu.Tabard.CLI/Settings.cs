using System.Text.Json;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// The profile's settings.json. Claude Code reads this from CLAUDE_CONFIG_DIR and applies its 'env'
/// block to every session, which is what lets a profile carry a whole provider configuration without
/// tabard having to inject anything at launch - a bare 'claude' following the ~/.claude link picks up
/// exactly the same settings.
/// </summary>
internal static class Settings
{
    public static string FileFor(string profileDir) => Path.Combine(profileDir, "settings.json");

    /// <summary>
    /// The env block as a dictionary, or empty if there is not one. Fails soft: the file belongs to
    /// Claude Code and anything unexpected in it is the user's business, not something to crash over.
    /// </summary>
    public static Dictionary<string, string> ReadEnv(string profileDir)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var file = FileFor(profileDir);
            if (!File.Exists(file))
                return env;

            using var stream = File.OpenRead(file);
            using var doc = JsonDocument.Parse(stream);

            if (
                doc.RootElement.ValueKind is not JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("env", out var block)
                || block.ValueKind is not JsonValueKind.Object
            )
            {
                return env;
            }

            foreach (var entry in block.EnumerateObject())
            {
                if (entry.Value.ValueKind is JsonValueKind.String)
                    env[entry.Name] = entry.Value.GetString() ?? "";
            }
        }
        catch
        {
            // Unreadable or unexpected shape - treat it as configuring nothing.
        }

        return env;
    }

    /// <summary>
    /// Applies <paramref name="values"/> to the env block, where a null value removes a key. Every
    /// other property in the file - and every env entry tabard does not set - is copied through
    /// verbatim, so hand edits and whatever Claude Code writes here survive. Returns anything the
    /// caller should warn about.
    /// </summary>
    public static IReadOnlyList<string> MergeEnv(
        string profileDir,
        IReadOnlyList<KeyValuePair<string, string?>> values
    )
    {
        Directory.CreateDirectory(profileDir);

        var file = FileFor(profileDir);
        var warnings = new List<string>();

        // Held open across the write below: the elements copied through belong to this document.
        using var existing = TryParse(file, warnings);
        var root = existing?.RootElement is { ValueKind: JsonValueKind.Object } element
            ? element
            : (JsonElement?)null;

        // Temp file plus rename, like ~/.tabard/last: a half-written settings.json would leave the
        // profile unable to authenticate at all.
        var temp = $"{file}.{Environment.ProcessId}.tmp";

        try
        {
            using (
                var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)
            )
            {
                // Locked down while the file is still empty - it is about to hold an API key.
                Harden(temp);

                using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions { Indented = true }
                );

                writer.WriteStartObject();

                var wrote = false;
                if (root is { } r)
                {
                    foreach (var property in r.EnumerateObject())
                    {
                        if (!property.NameEquals("env"))
                        {
                            property.WriteTo(writer);
                            continue;
                        }

                        writer.WritePropertyName("env");
                        WriteEnv(
                            writer,
                            property.Value.ValueKind is JsonValueKind.Object
                                ? property.Value
                                : null,
                            values
                        );
                        wrote = true;
                    }
                }

                if (!wrote)
                {
                    writer.WritePropertyName("env");
                    WriteEnv(writer, null, values);
                }

                writer.WriteEndObject();
            }

            File.Move(temp, file, overwrite: true);
            Harden(file);
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

        return warnings;
    }

    private static void WriteEnv(
        Utf8JsonWriter writer,
        JsonElement? existing,
        IReadOnlyList<KeyValuePair<string, string?>> values
    )
    {
        var pending = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            pending[key] = value;

        writer.WriteStartObject();

        if (existing is { } env)
        {
            foreach (var entry in env.EnumerateObject())
            {
                // Removing it from pending as it is written keeps the original ordering and stops
                // the append below emitting the same key twice.
                if (pending.Remove(entry.Name, out var replacement))
                {
                    if (replacement is not null)
                        writer.WriteString(entry.Name, replacement);

                    continue;
                }

                entry.WriteTo(writer);
            }
        }

        foreach (var (key, value) in pending)
        {
            if (value is not null)
                writer.WriteString(key, value);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The parsed file, or null if there is nothing usable to preserve. An unparseable file is moved
    /// aside rather than silently overwritten - it is the user's content, however broken.
    /// </summary>
    private static JsonDocument? TryParse(string file, List<string> warnings)
    {
        if (!File.Exists(file))
            return null;

        try
        {
            using var stream = File.OpenRead(file);
            return JsonDocument.Parse(stream);
        }
        catch (JsonException)
        {
            try
            {
                var backup = $"{file}.broken";
                File.Move(file, backup, overwrite: true);
                warnings.Add(
                    $"{file} was not valid JSON, so it could not be merged into; kept it as {backup}."
                );
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"{file} was not valid JSON and could not be set aside ({ex.Message})."
                );
            }

            return null;
        }
        catch (Exception ex)
        {
            warnings.Add($"could not read {file} ({ex.Message}); it will be rewritten.");
            return null;
        }
    }

    private static void Harden(string file)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            // 0600 - an OpenRouter key lives in here.
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort, same as the profile directories.
        }
    }
}
