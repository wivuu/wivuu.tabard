using System.Diagnostics;

namespace Envy.Cli;

internal static class Launcher
{
    public static int Launch(Profile profile, IReadOnlyList<string> claudeArgs)
    {
        var executable =
            Resolve()
            ?? throw new FileNotFoundException(
                "could not find 'claude' on PATH. Install Claude Code first, or put it on PATH."
            );

        var psi = BuildStartInfo(executable, claudeArgs);

        // Must be absolute and fully expanded. A '~' left in this value has been written
        // literally by some Claude Code versions, stranding credentials under the CWD.
        psi.Environment["CLAUDE_CONFIG_DIR"] = Path.GetFullPath(profile.Dir);

        // Ctrl+C belongs to the child; envy just waits for it to finish. Registered before the
        // start so there is no window where an early ^C kills envy out from under the child.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        using var proc =
            Process.Start(psi) ?? throw new InvalidOperationException("failed to start claude.");

        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> args)
    {
        var isBatch =
            OperatingSystem.IsWindows()
            && (
                executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            );

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

        foreach (
            var rawDir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        )
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

                // Keep looking rather than returning: a broken or non-executable 'claude' earlier
                // in PATH must not hide a working one later on.
                if (IsExecutable(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            // File.Exists is true for a dangling symlink - ~/.local/bin/claude is one after a
            // 'migrate-installer' install loses its target - so make sure it resolves to content.
            if (File.ResolveLinkTarget(path, returnFinalTarget: true) is { Exists: false })
                return false;

            if (OperatingSystem.IsWindows())
                return true;

            const UnixFileMode anyExecute =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch
        {
            return false;
        }
    }
}
