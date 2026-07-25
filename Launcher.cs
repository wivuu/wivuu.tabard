using System.Diagnostics;

namespace Envy.Cli;

internal static class Launcher
{
    public static int Launch(Profile profile, IReadOnlyList<string> claudeArgs)
    {
        var executable = Resolve()
            ?? throw new FileNotFoundException(
                "could not find 'claude' on PATH. Install Claude Code first, or put it on PATH.");

        var psi = BuildStartInfo(executable, claudeArgs);

        // Must be absolute and fully expanded. A '~' left in this value has been written
        // literally by some Claude Code versions, stranding credentials under the CWD.
        psi.Environment["CLAUDE_CONFIG_DIR"] = Path.GetFullPath(profile.Dir);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start claude.");

        // Ctrl+C belongs to the child; envy just waits for it to finish.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> args)
    {
        var isBatch = OperatingSystem.IsWindows()
            && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        // npm installs a .cmd shim on Windows, which cannot be exec'd directly
        // with UseShellExecute=false - it has to go through cmd.exe.
        var psi = isBatch
            ? new ProcessStartInfo("cmd.exe") { UseShellExecute = false }
            : new ProcessStartInfo(executable) { UseShellExecute = false };

        if (isBatch)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(executable);
        }

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    private static string? Resolve()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];

        foreach (var rawDir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0)
                continue;

            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, "claude" + extension);
                }
                catch
                {
                    continue; // Malformed PATH entry.
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
