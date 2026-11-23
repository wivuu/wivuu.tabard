using System.Diagnostics;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// Docker Sandboxes as an <see cref="ISandbox"/>. Claude Code runs as sbx's built-in 'claude'
/// agent. pi has no built-in, so it runs under sbx's 'shell' agent, in sandboxes made from a
/// template tabard builds once per pi version (<see cref="Sbx.EnsurePiTemplate"/>).
/// </summary>
internal sealed class SbxSandbox : ISandbox
{
    public string Name => "sbx";

    public string Description => "a Docker sandbox";

    public string Executable => Sbx.Executable;

    public string Install => "brew install docker/tap/sbx";

    /// <summary>The sandbox sees the host's paths at the same paths, and a Windows path means
    /// nothing to the Linux inside it.</summary>
    public bool SupportedHere => !OperatingSystem.IsWindows();

    public string? Unsupported(Profile profile)
    {
        // The sandbox sees the host's paths at the same paths, and a Windows path means nothing to
        // the Linux inside it.
        if (OperatingSystem.IsWindows())
            return $"'{profile.Name}' is set to launch in a sandbox, which tabard does not support on "
                + $"Windows yet. Run 'tabard sandbox off {profile.Name}' to launch it directly.";

        return null;
    }

    public SandboxLaunch Prepare(
        Profile profile,
        IReadOnlyList<string> agentArgs,
        SandboxRequest request
    )
    {
        if (Unsupported(profile) is { } reason)
            throw new PlatformNotSupportedException(reason);

        var executable =
            Launcher.Which(Sbx.Executable)
            ?? throw new FileNotFoundException(
                $"'{profile.Name}' is set to launch in a Docker sandbox, but 'sbx' is not on PATH. "
                    + "Install it with 'brew install docker/tap/sbx', or run "
                    + $"'tabard sandbox off {profile.Name}' to launch it directly."
            );

        var warnings = new List<string>();
        var workspace = Sandboxing.ResolveWorkspace(request.Cwd, warnings);
        Sandboxing.Prepare(profile.Dir, profile.Harness);

        foreach (var warning in warnings)
            Console.Error.WriteLine($"tabard: {warning}");

        // pi's image is tabard's own, built from the pi installed here; Claude Code's is sbx's, or
        // whatever the profile was pointed at.
        var image =
            profile.Harness is Harness.Pi
                ? Sbx.EnsurePiTemplate(executable)
                : Sbx.ImageOf(profile.Name);

        var sandbox = Sbx.SandboxName(profile.Name, workspace.Root, image);
        var exists = Sbx.Exists(executable, sandbox);

        Console.Error.WriteLine(
            exists ? $"tabard: launching '{profile.Name}' in sandbox {sandbox}."
            : image is null ? $"tabard: creating sandbox {sandbox} for '{profile.Name}'."
            : $"tabard: creating sandbox {sandbox} for '{profile.Name}' from {image}."
        );

        var identity = Sandboxing.GitIdentity(request.Cwd);
        var args = profile.Harness switch
        {
            Harness.Pi => Sbx.PiRunArgs(
                sandbox,
                profile.Dir,
                workspace,
                exists,
                agentArgs,
                image!,
                request.HostPorts,
                identity
            ),
            _ => Sbx.RunArgs(sandbox, profile.Dir, workspace, exists, agentArgs, image, identity),
        };

        var psi = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return new SandboxLaunch(
            psi,
            Sbx.Executable,
            profile.Harness is Harness.Claude ? () => ForgetGateway(profile) : null
        );
    }

    private static void ForgetGateway(Profile profile)
    {
        if (Sbx.ForgetGateway(profile.ClaudeJsonFile) is { } problem)
            Console.Error.WriteLine($"tabard: {problem}");
    }
}
