using System.Diagnostics;

namespace Wivuu.Tabard.Cli;

internal static class Launcher
{
    public static int Launch(Profile profile, IReadOnlyList<string> claudeArgs, bool sandboxed)
    {
        var harness = profile.Harness;

        if (sandboxed)
        {
            // Refused rather than launched directly: whoever marked the profile sandboxed decided
            // it should not run loose on this machine.
            if (harness is not Harness.Claude)
                throw new InvalidOperationException(
                    $"'{profile.Name}' is set to launch {Harnesses.DisplayName(harness)} in a sandbox, "
                        + "which tabard does not support yet. Run "
                        + $"'tabard sandbox off {profile.Name}' to launch it directly."
                );

            return LaunchSandboxed(profile, claudeArgs);
        }

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

        return Run(psi, command);
    }

    /// <summary>
    /// Claude Code inside a Docker sandbox. Every refusal here happens before anything starts: a
    /// profile marked as sandboxed must never fall back to launching directly, because whoever
    /// marked it decided it should not run loose on this machine.
    /// </summary>
    private static int LaunchSandboxed(Profile profile, IReadOnlyList<string> claudeArgs)
    {
        // The sandbox sees the host's paths at the same paths, and a Windows path means nothing to
        // the Linux inside it.
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                $"'{profile.Name}' is set to launch in a sandbox, which tabard does not support on "
                    + $"Windows yet. Run 'tabard sandbox off {profile.Name}' to launch it directly."
            );

        var executable =
            Which(Sandboxing.Executable)
            ?? throw new FileNotFoundException(
                $"'{profile.Name}' is set to launch in a Docker sandbox, but 'sbx' is not on PATH. "
                    + "Install it with 'brew install docker/tap/sbx', or run "
                    + $"'tabard sandbox off {profile.Name}' to launch it directly."
            );

        var cwd = Directory.GetCurrentDirectory();
        var warnings = new List<string>();
        var workspace = Sandboxing.ResolveWorkspace(cwd, warnings);
        Sandboxing.Prepare(profile.Dir);

        var image = Sandboxing.ImageOf(profile.Name);
        var sandbox = Sandboxing.SandboxName(profile.Name, workspace.Root, image);
        var exists = Sandboxing.Exists(executable, sandbox);

        foreach (var warning in warnings)
            Console.Error.WriteLine($"tabard: {warning}");

        Console.Error.WriteLine(
            exists ? $"tabard: launching '{profile.Name}' in sandbox {sandbox}."
            : image is null ? $"tabard: creating sandbox {sandbox} for '{profile.Name}'."
            : $"tabard: creating sandbox {sandbox} for '{profile.Name}' from {image}."
        );

        var psi = BuildStartInfo(
            executable,
            Sandboxing.RunArgs(
                sandbox,
                profile.Dir,
                workspace,
                exists,
                claudeArgs,
                image,
                Sandboxing.GitIdentity(cwd)
            )
        );

        var code = Run(psi, Sandboxing.Executable);

        if (Sandboxing.ForgetGateway(profile.ClaudeJsonFile) is { } problem)
            Console.Error.WriteLine($"tabard: {problem}");

        return code;
    }

    private static int Run(ProcessStartInfo psi, string command)
    {
        // Ctrl+C belongs to the child; tabard just waits for it to finish. Registered before the
        // start so there is no window where an early ^C kills tabard out from under the child.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

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
