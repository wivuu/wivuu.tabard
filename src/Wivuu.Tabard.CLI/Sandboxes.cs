using System.Diagnostics;

namespace Wivuu.Tabard.Cli;

/// <summary>
/// A kind of sandbox a profile's agent can be launched in. tabard decides whether a launch is
/// sandboxed (<see cref="Sandboxing"/>) and starts and waits on what comes back from
/// <see cref="Prepare"/>; everything about how the agent ends up isolated is the implementation's.
/// There are two: srt (<see cref="SrtSandbox"/>), the default, and Docker Sandboxes
/// (<see cref="SbxSandbox"/>).
/// </summary>
internal interface ISandbox
{
    /// <summary>Short and stable, for messages and any future setting naming the kind.</summary>
    string Name { get; }

    /// <summary>What a launch inside it is called in a sentence: "a Docker sandbox".</summary>
    string Description { get; }

    /// <summary>The program it needs on PATH.</summary>
    string Executable { get; }

    /// <summary>How to install <see cref="Executable"/>, as a command to run.</summary>
    string Install { get; }

    /// <summary>Whether this kind can run on this operating system at all.</summary>
    bool SupportedHere { get; }

    /// <summary>Whether it is ready to use here: supported, and its program on PATH.</summary>
    bool Available => SupportedHere && Launcher.Which(Executable) is not null;

    /// <summary>Why <paramref name="profile"/> cannot be launched in this kind of sandbox on this
    /// machine, worded as a whole sentence, or null if it can.</summary>
    string? Unsupported(Profile profile);

    /// <summary>
    /// Everything up to starting the agent: any refusal happens here, before anything runs, so a
    /// profile marked sandboxed can never fall back to running loose. May take a while the first
    /// time - building an image, say - and says so on stderr.
    /// </summary>
    SandboxLaunch Prepare(Profile profile, IReadOnlyList<string> agentArgs, SandboxRequest request);
}

/// <summary>What a sandboxed launch is asked for beyond the profile and its arguments.</summary>
/// <param name="Cwd">The folder the launch is made from.</param>
/// <param name="HostPorts">Ports on this machine's loopback the agent expects to reach at
/// 127.0.0.1 - a local model server's, say. A sandbox with its own network makes them answer there
/// too, and opens nothing else.</param>
internal sealed record SandboxRequest(string Cwd, IReadOnlyList<int> HostPorts);

/// <summary>The process that runs the agent in its sandbox, and anything to tidy once it exits.
/// </summary>
internal sealed record SandboxLaunch(
    ProcessStartInfo StartInfo,
    string Command,
    Action? AfterExit = null
);

/// <summary>The kinds of sandbox tabard knows, and which one a profile uses.</summary>
internal static class Sandboxes
{
    /// <summary>Every kind, the default first - the order the editor cycles through them in.
    /// </summary>
    public static IReadOnlyList<ISandbox> All { get; } = [new SrtSandbox(), new SbxSandbox()];

    /// <summary>
    /// The kind a profile that has not chosen one uses: the first in <see cref="All"/> that is
    /// installed here, so someone with only Docker Sandboxes gets those. With neither, srt - which
    /// is what the install hint then offers (<see cref="Refusal"/>).
    /// </summary>
    public static ISandbox Default => All.FirstOrDefault(s => s.Available) ?? All[0];

    /// <summary>The sandbox <paramref name="profile"/> launches in when sandboxed.</summary>
    public static ISandbox For(Profile profile) => For(profile.Name);

    public static ISandbox For(string profile) =>
        SandboxOptions.KindOf(profile) is { } kind && Named(kind) is { } chosen ? chosen : Default;

    /// <summary>
    /// Why sandboxing cannot be switched on for <paramref name="profile"/> - its kind is not
    /// installed, or cannot run here - worded with what to install and how, or null if it can.
    /// Asked when the setting is switched on, so nobody finds out at the next launch instead.
    /// </summary>
    public static string? Refusal(string profile)
    {
        var chosen = SandboxOptions.KindOf(profile) is { } kind ? Named(kind) : null;

        if (chosen is not null)
        {
            if (!chosen.SupportedHere)
                return $"{chosen.Name} sandboxes do not run on this system yet.";

            return chosen.Available
                ? null
                : $"'{profile}' uses {chosen.Name}, which is not installed - install it with "
                    + $"'{chosen.Install}', or pick another kind with 'tabard sandbox kind'.";
        }

        if (All.Any(s => s.Available))
            return null;

        var supported = All.Where(s => s.SupportedHere).ToList();
        return supported.Count == 0
            ? "sandboxes do not run on this system yet."
            : "no sandbox is installed - install "
                + string.Join(" or ", supported.Select(s => $"{s.Name} with '{s.Install}'"))
                + ".";
    }

    public static ISandbox? Named(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The loopback ports a sandboxed launch of <paramref name="profile"/> should be able to reach:
    /// the one it waits for before launching, when that is on this machine. A server elsewhere is
    /// reached the same way from inside a sandbox as from outside, so it needs nothing.
    /// </summary>
    public static IReadOnlyList<int> HostPorts(Profile profile)
    {
        if (Companions.WaitForOf(profile.Name) is not { } address)
            return [];

        try
        {
            var (host, port) = Companions.ParseAddress(address);
            return host is "localhost" or "127.0.0.1" or "::1" ? [port] : [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }
}
