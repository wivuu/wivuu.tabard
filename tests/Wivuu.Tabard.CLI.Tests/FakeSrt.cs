using System.Text.Json;

namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// A stand-in 'srt' at the front of PATH, like <see cref="FakeSbx"/>: each call's arguments are
/// noted, along with a copy of the settings file it was handed - tabard deletes that once the
/// agent exits - and the profile directory it was pointed at. Nothing is run. PATH belongs to the
/// whole process, so tests that use this must not run in parallel. Unix only, as srt launches are.
/// </summary>
internal sealed class FakeSrt : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _dir;

    public FakeSrt(string variable = "CLAUDE_CONFIG_DIR")
    {
        _dir = Sandbox.Scratch("fake-srt");

        var script = Path.Combine(_dir, "srt");
        File.WriteAllText(
            script,
            $$"""
            #!/bin/sh
            for arg in "$@"; do printf '%s\n' "$arg"; done > '{{_dir}}/args'
            cp "$2" '{{_dir}}/settings.json'
            printf '%s\n' "${{variable}}" > '{{_dir}}/config'
            printf '%s\n' "$TMPDIR" > '{{_dir}}/tmpdir'
            """
        );

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        Environment.SetEnvironmentVariable("PATH", $"{_dir}{Path.PathSeparator}{_originalPath}");
    }

    public bool Ran => File.Exists(Path.Combine(_dir, "args"));

    /// <summary>The last call's arguments.</summary>
    public IReadOnlyList<string> Args => File.ReadAllLines(Path.Combine(_dir, "args"));

    public string ConfigDir => File.ReadAllText(Path.Combine(_dir, "config")).Trim();

    public string TempDir => File.ReadAllText(Path.Combine(_dir, "tmpdir")).Trim();

    /// <summary>The settings the last call was handed.</summary>
    public JsonElement Settings
    {
        get
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(_dir, "settings.json"))
            );
            return doc.RootElement.Clone();
        }
    }

    public IReadOnlyList<string> List(string section, string name) =>
        [
            .. Settings
                .GetProperty(section)
                .GetProperty(name)
                .EnumerateArray()
                .Select(e => e.GetString()!),
        ];

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _originalPath);
}
