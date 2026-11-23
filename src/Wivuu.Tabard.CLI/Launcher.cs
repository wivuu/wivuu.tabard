using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wivuu.Tabard.Cli;

internal static class Launcher
{
    public static int Launch(Profile profile, IReadOnlyList<string> claudeArgs, bool sandboxed)
    {
        var harness = profile.Harness;

        if (sandboxed)
            return LaunchSandboxed(profile, claudeArgs);

        var command = Harnesses.Executable(harness);
        var executable =
            Which(command)
            ?? throw new FileNotFoundException(
                $"could not find '{command}' on PATH. Install {Harnesses.DisplayName(harness)} "
                    + "first, or put it on PATH."
            );

        var psi = BuildStartInfo(executable, claudeArgs);

        // Must be absolute and fully expanded. A '~' left in this value has been written
        // literally by some Claude Code versions, stranding credentials under the CWD.
        psi.Environment[Harnesses.ConfigVariable(harness)] = Path.GetFullPath(profile.Dir);

        return Run(psi, command, profile);
    }

    /// <summary>
    /// The agent inside the profile's kind of sandbox. Every refusal happens in Prepare, before
    /// anything starts: a profile marked as sandboxed must never fall back to launching directly,
    /// because whoever marked it decided it should not run loose on this machine.
    /// </summary>
    private static int LaunchSandboxed(Profile profile, IReadOnlyList<string> agentArgs)
    {
        var sandbox = Sandboxes.For(profile);
        var launch = sandbox.Prepare(
            profile,
            agentArgs,
            new SandboxRequest(Directory.GetCurrentDirectory(), Sandboxes.HostPorts(profile))
        );

        var code = Run(launch.StartInfo, launch.Command, profile);
        launch.AfterExit?.Invoke();
        return code;
    }

    private static int Run(ProcessStartInfo psi, string command, Profile profile)
    {
        // Ctrl+C belongs to the child; tabard just waits for it to finish. Registered before the
        // start so there is no window where an early ^C kills tabard out from under the child.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        // Started first so whatever the agent talks to is already coming up when it asks. Stopped
        // however tabard leaves: the agent exiting, failing to start, or the terminal closing.
        using var companion = Companions.Start(profile);
        using var hangup = companion is null
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => companion.Dispose());
        using var terminate = companion is null
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => companion.Dispose());

        // Not launching at all: ^C while waiting, or the command alongside died before answering.
        if (Companions.WaitFor(profile, companion) is { } refused)
            return refused;

        using var proc =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {command}.");

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

    /// <summary>The first working <paramref name="command"/> on PATH, or null.</summary>
    public static string? Which(string command)
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
                    candidate = Path.Combine(dir, command + extension);
                }
                catch
                {
                    continue; // Malformed PATH entry.
                }

                // Keep looking rather than returning: a broken or non-executable copy earlier in
                // PATH must not hide a working one later on.
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
