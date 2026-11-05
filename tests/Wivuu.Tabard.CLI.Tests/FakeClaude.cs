namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// A stand-in 'claude' at the front of PATH that notes each launch in a file and exits, which is what
/// lets a test drive a launching command all the way through. PATH belongs to the whole process, so
/// tests that use this must not run in parallel - the same constraint <see cref="Sandbox"/> has.
/// </summary>
internal sealed class FakeClaude : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _log;

    public FakeClaude()
    {
        var dir = Sandbox.Scratch("fake-claude");
        _log = Path.Combine(dir, "launches");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(dir, "claude.cmd"),
                $"@echo off\r\n>>\"{_log}\" echo %CLAUDE_CONFIG_DIR%^|%*\r\n"
            );
        }
        else
        {
            var script = Path.Combine(dir, "claude");
            File.WriteAllText(
                script,
                $"#!/bin/sh\nprintf '%s|%s\\n' \"$CLAUDE_CONFIG_DIR\" \"$*\" >> '{_log}'\n"
            );
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        Environment.SetEnvironmentVariable("PATH", $"{dir}{Path.PathSeparator}{_originalPath}");
    }

    /// <summary>What each launch was given, oldest first.</summary>
    public IReadOnlyList<(string ConfigDir, string Args)> Launches =>
        File.Exists(_log)
            ?
            [
                .. File.ReadAllLines(_log)
                    .Select(line => line.Split('|', 2))
                    .Select(parts => (parts[0].Trim(), parts[1].Trim())),
            ]
            : [];

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _originalPath);
}
