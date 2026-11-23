namespace Wivuu.Tabard.Cli.Tests;

/// <summary>
/// A stand-in 'sbx' at the front of PATH, like <see cref="FakeClaude"/>: 'sbx ls -q' answers with
/// the sandboxes it has been told exist, and every other call is noted in a file and exits. PATH
/// belongs to the whole process, so tests that use this must not run in parallel.
/// Unix only - a sandboxed launch refuses to start on Windows before it looks for sbx.
/// </summary>
internal sealed class FakeSbx : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _log;
    private readonly string _existing;
    private readonly string _templates;

    public FakeSbx()
    {
        var dir = Sandbox.Scratch("fake-sbx");
        _log = Path.Combine(dir, "runs");
        _existing = Path.Combine(dir, "existing");
        File.WriteAllText(_existing, "");
        _templates = Path.Combine(dir, "templates");
        File.WriteAllText(_templates, "[]");

        var script = Path.Combine(dir, "sbx");
        File.WriteAllText(
            script,
            $$"""
            #!/bin/sh
            if [ "$1" = ls ]; then cat '{{_existing}}'; exit 0; fi
            if [ "$1" = template ] && [ "$2" = ls ]; then cat '{{_templates}}'; exit 0; fi
            for arg in "$@"; do printf '%s\n' "$arg"; done >> '{{_log}}'
            printf '%s\n' '--end--' >> '{{_log}}'
            """
        );

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        Environment.SetEnvironmentVariable("PATH", $"{dir}{Path.PathSeparator}{_originalPath}");
    }

    /// <summary>Makes 'sbx ls -q' list a sandbox, as if an earlier launch had created it.</summary>
    public void Exists(string sandbox) => File.AppendAllText(_existing, sandbox + "\n");

    /// <summary>Makes 'sbx template ls' list a template, as if an earlier launch had built it.
    /// </summary>
    public void Template(string repository, string tag) =>
        File.WriteAllText(
            _templates,
            $$"""[{"repository": "{{repository}}", "tag": "{{tag}}", "flavor": "shell"}]"""
        );

    /// <summary>The arguments of each call other than 'ls' and 'template ls', oldest first.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Runs
    {
        get
        {
            var runs = new List<IReadOnlyList<string>>();
            if (!File.Exists(_log))
                return runs;

            var current = new List<string>();
            foreach (var line in File.ReadAllLines(_log))
            {
                if (line == "--end--")
                {
                    runs.Add(current);
                    current = [];
                }
                else
                {
                    current.Add(line);
                }
            }

            return runs;
        }
    }

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _originalPath);
}
