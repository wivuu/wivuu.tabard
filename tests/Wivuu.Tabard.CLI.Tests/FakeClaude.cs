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

    /// <summary><paramref name="command"/> and <paramref name="variable"/> make it stand in for
    /// another harness: 'pi' noting PI_CODING_AGENT_DIR, say.</summary>
    public FakeClaude(string command = "claude", string variable = "CLAUDE_CONFIG_DIR")
    {
        var dir = Sandbox.Scratch($"fake-{command}");
        _log = Path.Combine(dir, "launches");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(dir, $"{command}.cmd"),
                $"@echo off\r\n>>\"{_log}\" echo %{variable}%^|%*\r\n"
            );
        }
        else
        {
            var script = Path.Combine(dir, command);
            File.WriteAllText(
                script,
                $"#!/bin/sh\nprintf '%s|%s\\n' \"${variable}\" \"$*\" >> '{_log}'\n"
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
